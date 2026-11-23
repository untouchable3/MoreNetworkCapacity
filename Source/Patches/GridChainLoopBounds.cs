using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // These are traversal counters, not comparisons against the numeric ID.
    // Keep each method's reviewed predicate and provenance; return indexes only
    // after the entire shape has been checked. No input instruction is changed.
    internal static class GridChainLoopBounds
    {
        internal static bool Handles(MethodBase original)
        {
            if (original == null || original.IsStatic) return false;
            MethodInfo m = original as MethodInfo;
            if (m == null) return false;
            ParameterInfo[] p = m.GetParameters();
            if (m.DeclaringType == typeof(DistrictManager))
                return (m.Name == "MoveParkNodes" || m.Name == "MoveParkSegments") && m.ReturnType == typeof(void)
                    && p.Length == 4 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(int)
                    && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte);
            if (m.DeclaringType == typeof(DamPowerHouseAI))
                return m.Name == "GetRefundAmount" && m.ReturnType == typeof(int) && p.Length == 2
                    && p[0].ParameterType == typeof(ushort) && p[1].ParameterType == typeof(Building).MakeByRefType();
            return m.DeclaringType == typeof(ElectricityManager) && m.Name == "ConductToNodes" && m.ReturnType == typeof(void)
                && p.Length == 3 && p[0].ParameterType == typeof(ushort)
                && p[1].ParameterType == typeof(int) && p[2].ParameterType == typeof(int);
        }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null) return null;
            bool segment = original.Name == "MoveParkSegments";
            int constant = original.DeclaringType == typeof(DistrictManager) ? 36864 : 32768;
            OpCode predicate = original.DeclaringType == typeof(DamPowerHouseAI) ? OpCodes.Ble : OpCodes.Blt;
            string next = original.DeclaringType == typeof(DamPowerHouseAI) ? "m_nextBuildingNode" : (segment ? "m_nextGridSegment" : "m_nextGridNode");
            int found = -1;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, constant)) continue;
                if (!Guard(original, code, i, predicate, segment, next)) continue;
                if (found >= 0) return null;
                found = i;
            }
            return found < 0 ? null : new int[] { found };
        }

        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound,
            OpCode predicate, bool segment, string next)
        {
            int branch = Next(code, bound);
            if (branch < 0 || !Branch(code[branch].opcode, predicate)) return false;
            int condition = LabelIndex(code, code[branch].operand);
            if (condition <= branch) return false;
            condition = Skip(code, condition);
            if (condition < 0) return false;
            int cursor = Local(code[condition], false);
            int back = Next(code, condition);
            if (cursor < 0 || back < 0 || !Branch(code[back].opcode, OpCodes.Brtrue)) return false;
            int start = LabelIndex(code, code[back].operand);
            if (start < 0 || start >= bound) return false;
            start = Skip(code, start);
            if (start < 0 || start >= bound) return false;
            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (load < start || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            int counter = Local(code[store], true);
            if (counter < 0 || counter == cursor || Local(code[load], false) != counter) return false;
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            if (counter >= locals.Count || cursor >= locals.Count || VariableType(code[store], counter, locals) != typeof(int)
                || VariableType(code[condition], cursor, locals) != typeof(ushort)) return false;

            // Initial zero counter and jump to exactly the same cursor test.
            int initialJump = Previous(code, start), initialStore = Previous(code, initialJump);
            int zero = Previous(code, initialStore);
            if (zero < 0 || !Branch(code[initialJump].opcode, OpCodes.Br)
                || Skip(code, LabelIndex(code, code[initialJump].operand)) != condition
                || Local(code[initialStore], true) != counter || code[zero].opcode != OpCodes.Ldc_I4_0) return false;

            if (!Seed(original, code, zero, cursor, segment, locals)) return false;
            for (int i = start; i <= back; ++i)
                if (Address(code[i]) == cursor || Address(code[i]) == counter) return false;

            // Require the reviewed invalid-list handler and its exit. Unknown
            // intervening control flow is not silently treated as a guard.
            int h = Next(code, branch);
            if (h < 0 || code[h].opcode != OpCodes.Ldc_I4_1) return false;
            h = Next(code, h);
            if (h < 0 || code[h].opcode != OpCodes.Ldstr || !object.Equals(code[h].operand, "Invalid list detected!\n")) return false;
            h = Next(code, h);
            if (!Call(code, h, typeof(Environment), "get_StackTrace")) return false;
            h = Next(code, h);
            MethodInfo concat = h < 0 ? null : code[h].operand as MethodInfo;
            if (!Call(code, h, typeof(string), "Concat") || concat.ReturnType != typeof(string)
                || concat.GetParameters().Length != 2) return false;
            h = Next(code, h);
            MethodInfo error = h < 0 ? null : code[h].operand as MethodInfo;
            if (h < 0 || code[h].opcode != OpCodes.Call || error == null || error.Name != "Error"
                || error.DeclaringType == null || !error.DeclaringType.IsGenericType
                || error.DeclaringType.GetGenericTypeDefinition().FullName != "ColossalFramework.CODebugBase`1") return false;
            h = Next(code, h);
            if (h < 0 || !Branch(code[h].opcode, OpCodes.Br) || Next(code, h) != condition
                || Skip(code, LabelIndex(code, code[h].operand)) != Next(code, back)) return false;

            int updateStore = Previous(code, load);
            if (updateStore < start || Local(code[updateStore], true) != cursor) return false;
            for (int i = start; i < updateStore; ++i)
                if (Local(code[i], true) == cursor || Local(code[i], true) == counter) return false;
            return ChainUpdate(code, updateStore, cursor, start, next, locals, segment);
        }

        private static bool Seed(MethodBase original, List<CodeInstruction> code, int zero, int cursor, bool segment,
            IList<LocalVariableInfo> locals)
        {
            int store = Previous(code, zero), value = Previous(code, store);
            if (value < 0 || Local(code[store], true) != cursor) return false;
            if (original.DeclaringType == typeof(DamPowerHouseAI))
            {
                int arg = Previous(code, value);
                return arg >= 0 && code[arg].opcode == OpCodes.Ldarg_2 && Field(code[value], typeof(Building), "m_netNode");
            }
            if (code[value].opcode != OpCodes.Ldelem_U2) return false;
            int add = Previous(code, value), x = Previous(code, add), mul = Previous(code, x);
            int width = Previous(code, mul), z = Previous(code, width), grid = Previous(code, z);
            if (grid < 0 || code[add].opcode != OpCodes.Add || Local(code[x], false) < 0
                || code[mul].opcode != OpCodes.Mul || code[width].opcode != OpCodes.Ldc_I4
                || !object.Equals(code[width].operand, 270) || Local(code[z], false) < 0) return false;
            string expected = segment ? "m_segmentGrid" : "m_nodeGrid";
            if (Field(code[grid], typeof(NetManager), expected))
            {
                int receiver = Previous(code, grid);
                return receiver >= 0 && Manager(code, receiver, store, locals);
            }
            int local = Local(code[grid], false);
            if (local < 0 || VariableType(code[grid], local, locals) != typeof(ushort[])) return false;
            int init = LastStore(code, local, grid), field = Previous(code, init), call = Previous(code, field);
            return call >= 0 && Field(code[field], typeof(NetManager), expected) && Singleton(code[call]);
        }
        private static int LastStore(List<CodeInstruction> code, int local, int end)
        {
            for (int i = end - 1; i >= 0; --i) if (Local(code[i], true) == local) return i;
            return -1;
        }
        private static bool Singleton(CodeInstruction code)
        {
            MethodInfo m = code.operand as MethodInfo;
            return code.opcode == OpCodes.Call && m != null && m.IsStatic && m.Name == "get_instance"
                && m.ReturnType == typeof(NetManager) && m.GetParameters().Length == 0
                && m.DeclaringType == typeof(ColossalFramework.Singleton<NetManager>);
        }
        private static bool Manager(List<CodeInstruction> code, int receiver, int end, IList<LocalVariableInfo> locals)
        {
            int local = Local(code[receiver], false);
            if (local < 0 || VariableType(code[receiver], local, locals) != typeof(NetManager)) return false;
            int init = LastStore(code, local, receiver), call = Previous(code, init);
            if (call < 0 || !Singleton(code[call])) return false;
            for (int i = init + 1; i < end; ++i)
                if (Local(code[i], true) == local || Address(code[i]) == local) return false;
            return true;
        }
        private static bool ChainUpdate(List<CodeInstruction> code, int store, int cursor, int start, string next,
            IList<LocalVariableInfo> locals, bool segment)
        {
            Type item = segment ? typeof(NetSegment) : typeof(NetNode);
            Type poolType = typeof(Array16<>).MakeGenericType(item);
            int field = Previous(code, store), element = Previous(code, field), index = Previous(code, element);
            int buffer = Previous(code, index), source = Previous(code, buffer);
            if (source < start || !Field(code[field], item, next)
                || code[element].opcode != OpCodes.Ldelema || !object.Equals(code[element].operand, item)
                || Local(code[index], false) != cursor || !Field(code[buffer], poolType, "m_buffer")) return false;
            int local = Local(code[source], false);
            if (local >= 0 && local < locals.Count && VariableType(code[source], local, locals) == poolType)
            {
                // Cached pools must be obtained from the corresponding NetManager field.
                int init = -1;
                for (int i = 0; i < start; ++i) if (Local(code[i], true) == local) init = i;
                int pool = Previous(code, init);
                int call = Previous(code, pool);
                if (call < 0 || !Singleton(code[call])
                    || !Field(code[pool], typeof(NetManager), segment ? "m_segments" : "m_nodes")) return false;
                for (int i = init + 1; i < store; ++i)
                    if (Local(code[i], true) == local || Address(code[i]) == local) return false;
                return true;
            }
            int manager = Previous(code, source);
            local = manager < 0 ? -1 : Local(code[manager], false);
            return local >= 0 && local < locals.Count && Manager(code, manager, store, locals)
                && Field(code[source], typeof(NetManager), segment ? "m_segments" : "m_nodes");
        }
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
