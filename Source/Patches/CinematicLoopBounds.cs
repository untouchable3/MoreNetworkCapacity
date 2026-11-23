using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Camera searches use UInt16 cursors/counters, including an ID wrap test.
    // Explicitly recognize and preserve the reviewed original bounds; do not
    // infer narrowing from a fixed instruction window.
    internal static class CinematicLoopBounds
    {
        internal static bool Handles(MethodBase original)
        {
            MethodInfo m = original as MethodInfo;
            if (m == null || m.DeclaringType != typeof(CinematicCameraController) || m.IsStatic
                || m.ReturnType != typeof(ushort)) return false;
            ParameterInfo[] p = m.GetParameters();
            return m.Name == "GetRandomRoad" && p.Length == 0
                || m.Name == "GetRoadWithName" && p.Length == 1 && p[0].ParameterType == typeof(string);
        }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null || !TypedLocals(original, code)) return null;
            int[] found = null;
            for (int bound = 0; bound < code.Count; ++bound)
            {
                if (!Constant(code[bound], 36864)) continue;
                int[] candidate = original.Name == "GetRandomRoad" ? Random(original, code, bound)
                    : Named(original, code, bound);
                if (candidate == null) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }
        private static bool TypedLocals(MethodBase original, List<CodeInstruction> code)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            Dictionary<int, Type> extras = new Dictionary<int, Type>();
            foreach (CodeInstruction c in code)
            {
                string name = c.opcode.Name ?? "";
                if (!name.StartsWith("ldloc") && !name.StartsWith("stloc")) continue;
                int local = Local(c, false);
                if (local < 0) local = Local(c, true);
                if (local < 0) local = Address(c);
                if (local < 0) return false;
                LocalVariableInfo declared = c.operand as LocalVariableInfo;
                Type actual = declared == null ? (local < locals.Count ? locals[local].LocalType : null) : declared.LocalType;
                if (local < locals.Count) { if (actual != locals[local].LocalType) return false; }
                else
                {
                    Type previous;
                    if (actual == null || extras.TryGetValue(local, out previous) && previous != actual) return false;
                    extras[local] = actual;
                }
            }
            return true;
        }
        private static int LastStore(List<CodeInstruction> code, int local, int end)
        {
            for (int i = end - 1; i >= 0; --i) if (Local(code[i], true) == local) return i;
            return -1;
        }
        private static bool Singleton(CodeInstruction c, Type type)
        {
            MethodInfo m = c.operand as MethodInfo;
            return c.opcode == OpCodes.Call && m != null && m.IsStatic && m.Name == "get_instance"
                && m.GetParameters().Length == 0 && m.ReturnType == type
                && m.DeclaringType == typeof(Singleton<>).MakeGenericType(type);
        }
        private static bool Manager(MethodBase original, List<CodeInstruction> code, int load, Type type)
        {
            if (load < 0) return false;
            int local = Local(code[load], false);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (local < 0 || local >= locals.Count || locals[local].LocalType != type) return false;
            int store = LastStore(code, local, load), source = Previous(code, store);
            if (source < 0 || !Singleton(code[source], type)) return false;
            for (int i = store + 1; i < code.Count; ++i)
                if (Local(code[i], true) == local || Address(code[i]) == local) return false;
            return true;
        }
        private static bool SegmentElement(MethodBase original, List<CodeInstruction> code, int element, int cursor)
        {
            int index = Previous(code, element), buffer = Previous(code, index), pool = Previous(code, buffer);
            int manager = Previous(code, pool);
            return manager >= 0 && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, typeof(NetSegment))
                && Local(code[index], false) == cursor && Field(code[buffer], typeof(Array16<NetSegment>), "m_buffer")
                && Field(code[pool], typeof(NetManager), "m_segments") && Manager(original, code, manager, typeof(NetManager));
        }
        private static bool UShort(MethodBase original, int local)
        {
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            return local >= 0 && local < locals.Count && locals[local].LocalType == typeof(ushort);
        }
        private static bool ReturnZero(List<CodeInstruction> code, int after)
        {
            int ret = Next(code, after);
            return ret >= 0 && code[after].opcode == OpCodes.Ldc_I4_0 && code[ret].opcode == OpCodes.Ret;
        }
        private static int[] Named(MethodBase original, List<CodeInstruction> code, int bound)
        {
            int branch = Next(code, bound), load = Previous(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return null;
            int cursor = load < 0 ? -1 : Local(code[load], false);
            int body = Skip(code, LabelIndex(code, code[branch].operand));
            if (!UShort(original, cursor) || body < 0 || body >= load || !ReturnZero(code, Next(code, branch))) return null;
            int store = Previous(code, load), conv = Previous(code, store), add = Previous(code, conv);
            int one = Previous(code, add), increment = Previous(code, one);
            if (increment < body || Local(code[store], true) != cursor || code[conv].opcode != OpCodes.Conv_U2
                || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1
                || Local(code[increment], false) != cursor) return null;
            int entry = Previous(code, body), init = Previous(code, entry), initial = Previous(code, init);
            if (initial < 0 || code[initial].opcode != OpCodes.Ldc_I4_1 || Local(code[init], true) != cursor
                || !Branch(code[entry].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[entry].operand)) != load) return null;
            int element = body;
            for (int j = 0; j < 4; ++j) element = Next(code, element);
            int flags = Next(code, element), mask = Next(code, flags), and = Next(code, mask), skip = Next(code, and);
            if (skip < 0 || !SegmentElement(original, code, element, cursor) || !Field(code[flags], typeof(NetSegment), "m_flags")
                || !Constant(code[mask], 134217728) || code[and].opcode != OpCodes.And
                || !Branch(code[skip].opcode, OpCodes.Brfalse) || Skip(code, LabelIndex(code, code[skip].operand)) != increment) return null;
            for (int i = 0; i < code.Count; ++i)
                if (Address(code[i]) == cursor || i != init && i != store && Local(code[i], true) == cursor) return null;
            return new[] { bound };
        }
        private static int[] Random(MethodBase original, List<CodeInstruction> code, int bound)
        {
            int branch = Next(code, bound), test = Previous(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, OpCodes.Blt)) return null;
            int counter = test < 0 ? -1 : Local(code[test], false);
            int start = Skip(code, LabelIndex(code, code[branch].operand));
            if (!UShort(original, counter) || start < 0 || start >= test || !ReturnZero(code, Next(code, branch))) return null;
            int counterStore = Previous(code, test), conv = Previous(code, counterStore), add = Previous(code, conv);
            int one = Previous(code, add), increment = Previous(code, one);
            if (increment < start || Local(code[increment], false) != counter || Local(code[counterStore], true) != counter
                || code[conv].opcode != OpCodes.Conv_U2 || code[add].opcode != OpCodes.Add || code[one].opcode != OpCodes.Ldc_I4_1) return null;
            int entry = Previous(code, start), init = Previous(code, entry), initial = Previous(code, init);
            if (initial < 0 || code[initial].opcode != OpCodes.Ldc_I4_1 || Local(code[init], true) != counter
                || !Branch(code[entry].opcode, OpCodes.Br) || Skip(code, LabelIndex(code, code[entry].operand)) != test) return null;
            int cursor = Local(code[start], false), a = Next(code, start), b = Next(code, a), c = Next(code, b);
            int dup = Next(code, c), store = Next(code, dup), wrap = Next(code, store), wrapBranch = Next(code, wrap);
            int resetOne = Next(code, wrapBranch), reset = Next(code, resetOne), body = Next(code, reset);
            if (!UShort(original, cursor) || cursor == counter || body < 0 || code[a].opcode != OpCodes.Ldc_I4_1
                || code[b].opcode != OpCodes.Add || code[c].opcode != OpCodes.Conv_U2 || code[dup].opcode != OpCodes.Dup
                || Local(code[store], true) != cursor || !Constant(code[wrap], 36864)
                || !Branch(code[wrapBranch].opcode, OpCodes.Bne_Un)
                || Skip(code, LabelIndex(code, code[wrapBranch].operand)) != body
                || code[resetOne].opcode != OpCodes.Ldc_I4_1 || Local(code[reset], true) != cursor) return null;
            int seed = Previous(code, initial), narrow = Previous(code, seed), random = Previous(code, narrow);
            int max = Previous(code, random), min = Previous(code, max), address = Previous(code, min), simulation = Previous(code, address);
            MethodInfo randomCall = random < 0 ? null : code[random].operand as MethodInfo;
            FieldInfo randomizer = address < 0 ? null : code[address].operand as FieldInfo;
            if (simulation < 0 || Local(code[seed], true) != cursor || code[narrow].opcode != OpCodes.Conv_U2
                || code[min].opcode != OpCodes.Ldc_I4_1 || !Constant(code[max], 36863)
                || code[address].opcode != OpCodes.Ldflda || randomizer == null || randomizer.DeclaringType != typeof(SimulationManager)
                || randomizer.Name != "m_randomizer" || !Manager(original, code, simulation, typeof(SimulationManager))
                || randomCall == null || code[random].opcode != OpCodes.Call || randomCall.DeclaringType != randomizer.FieldType
                || randomCall.Name != "Int32" || randomCall.ReturnType != typeof(int) || randomCall.GetParameters().Length != 2
                || randomCall.GetParameters()[0].ParameterType != typeof(int) || randomCall.GetParameters()[1].ParameterType != typeof(int)) return null;
            int element = body;
            for (int j = 0; j < 4; ++j) element = Next(code, element);
            int copy = Next(code, element), segmentStore = Next(code, copy), segmentLoad = Next(code, segmentStore);
            int flags = Next(code, segmentLoad), skip = Next(code, flags);
            int segment = segmentStore < 0 ? -1 : Local(code[segmentStore], true);
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (skip < 0 || !SegmentElement(original, code, element, cursor) || code[copy].opcode != OpCodes.Ldobj
                || !object.Equals(code[copy].operand, typeof(NetSegment)) || segment < 0 || segment >= locals.Count
                || locals[segment].LocalType != typeof(NetSegment) || Address(code[segmentLoad]) != segment
                || !Field(code[flags], typeof(NetSegment), "m_flags") || !Branch(code[skip].opcode, OpCodes.Brfalse)
                || Skip(code, LabelIndex(code, code[skip].operand)) != increment) return null;
            for (int i = 0; i < code.Count; ++i)
                if (Address(code[i]) == cursor || Address(code[i]) == counter
                    || i != seed && i != store && i != reset && Local(code[i], true) == cursor
                    || i != init && i != counterStore && Local(code[i], true) == counter) return null;
            return new[] { wrap, bound };
        }
        private static bool Constant(CodeInstruction c, int value) { return c.opcode == OpCodes.Ldc_I4 && object.Equals(c.operand, value); }
        private static bool Field(CodeInstruction code, Type type, string name)
        {
            FieldInfo field = code.operand as FieldInfo;
            return code.opcode == OpCodes.Ldfld && field != null && field.Name == name
                && (type == null || field.DeclaringType == type);
        }
        private static bool Call(List<CodeInstruction> code, int index, Type type, string name)
        {
            MethodInfo call = index < 0 ? null : code[index].operand as MethodInfo;
            return call != null && code[index].opcode == OpCodes.Call && call.DeclaringType == type && call.Name == name;
        }
        private static bool Branch(OpCode actual, OpCode op)
        {
            return actual == op || (op == OpCodes.Br && actual == OpCodes.Br_S)
                || (op == OpCodes.Brtrue && actual == OpCodes.Brtrue_S)
                || (op == OpCodes.Brfalse && actual == OpCodes.Brfalse_S) || (op == OpCodes.Bne_Un && actual == OpCodes.Bne_Un_S)
                || (op == OpCodes.Blt && actual == OpCodes.Blt_S) || (op == OpCodes.Ble && actual == OpCodes.Ble_S);
        }
        private static int LabelIndex(List<CodeInstruction> code, object operand)
        {
            if (!(operand is Label)) return -1;
            int result = -1;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains((Label)operand))
            { if (result >= 0) return -1; result = i; }
            return result;
        }
        private static int Skip(List<CodeInstruction> code, int index)
        {
            if (index < 0) return -1;
            while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index;
            return index < code.Count ? index : -1;
        }
        private static int Next(List<CodeInstruction> code, int index) { return index < 0 ? -1 : Skip(code, index + 1); }
        private static int Previous(List<CodeInstruction> code, int index)
        {
            --index;
            while (index >= 0 && code[index].opcode == OpCodes.Nop) --index;
            return index;
        }
        private static int Address(CodeInstruction code)
        {
            return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? LocalOperand(code.operand) : -1;
        }
        private static int Local(CodeInstruction code, bool store)
        {
            OpCode op = code.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            return op == (store ? OpCodes.Stloc : OpCodes.Ldloc) || op == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)
                ? LocalOperand(code.operand) : -1;
        }
        private static int LocalOperand(object operand)
        {
            LocalBuilder builder = operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (operand is int) return (int)operand;
            if (operand is byte) return (byte)operand;
            if (operand is short) return (short)operand;
            return -1;
        }
        private static Type VariableType(CodeInstruction access, int index, IList<LocalVariableInfo> locals)
        {
            LocalBuilder builder = access.operand as LocalBuilder;
            if (builder != null) return builder.LocalType;
            LocalVariableInfo variable = access.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalType;
            return index >= 0 && index < locals.Count ? locals[index].LocalType : null;
        }
    }
}
