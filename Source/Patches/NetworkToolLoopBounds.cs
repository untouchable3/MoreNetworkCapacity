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
    internal static class NetworkToolLoopBounds
    {
        internal static bool Handles(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            if (method == null || method.ReturnType != typeof(void)) return false;
            ParameterInfo[] p = method.GetParameters();
            if (method.DeclaringType == typeof(BuildingTool) && method.Name == "GetAllZoneBlocks")
                return !method.IsStatic && p.Length == 2 && p[0].ParameterType == typeof(ushort)
                    && p[1].ParameterType == typeof(HashSet<ushort>);
            if (method.DeclaringType == typeof(NetAdjust) && method.Name == "CalculatePath")
                return !method.IsStatic && p.Length == 5 && p[0].ParameterType == typeof(ushort)
                    && p[1].ParameterType == typeof(ushort) && p[2].ParameterType == typeof(ushort)
                    && p[3].ParameterType == typeof(ushort) && p[4].ParameterType == typeof(FastList<ushort>);
            if (method.DeclaringType != typeof(NetTool) || method.Name != "RefreshHelperLines" || !method.IsStatic || p.Length != 7)
                return false;
            Type line = typeof(NetTool).GetNestedType("HelperLine", BindingFlags.Public | BindingFlags.NonPublic);
            Type point = typeof(NetTool).GetNestedType("ControlPoint", BindingFlags.Public | BindingFlags.NonPublic);
            return line != null && point != null && p[0].ParameterType == typeof(FastList<>).MakeGenericType(line)
                && p[1].ParameterType == point && p[2].ParameterType == point
                && p[3].ParameterType == typeof(int) && p[4].ParameterType == typeof(bool)
                && p[5].ParameterType == typeof(UnityEngine.Vector3).MakeByRefType()
                && p[6].ParameterType == typeof(UnityEngine.Vector3).MakeByRefType();
        }

        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null) return null;
            bool dictionary = original.DeclaringType == typeof(NetAdjust);
            int constant = dictionary ? 36864 : 32768;
            OpCode predicate = original.DeclaringType == typeof(NetTool) ? OpCodes.Blt : OpCodes.Ble;
            string next = original.DeclaringType == typeof(BuildingTool) ? "m_nextBuildingNode" : "m_nextGridNode";
            int found = -1;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, constant)) continue;
                if (!Guard(original, code, i, predicate, dictionary, next)) continue;
                if (found >= 0) return null;
                found = i;
            }
            return found < 0 ? null : new int[] { found };
        }

        private static bool Guard(MethodBase original, List<CodeInstruction> code, int bound,
            OpCode predicate, bool dictionary, string next)
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
            return dictionary ? DictionaryUpdate(code, updateStore, cursor, start, locals)
                : NodeUpdate(code, updateStore, cursor, start, next, locals);
        }

        private static bool NodeUpdate(List<CodeInstruction> code, int store, int cursor, int start, string next,
            IList<LocalVariableInfo> locals)
        {
            int field = Previous(code, store), element = Previous(code, field), index = Previous(code, element);
            int buffer = Previous(code, index), pool = Previous(code, buffer), manager = Previous(code, pool);
            if (manager < start) return false;
            int source = Local(code[manager], false);
            return source >= 0 && source < locals.Count && locals[source].LocalType == typeof(NetManager)
                && Field(code[pool], typeof(NetManager), "m_nodes")
                && Field(code[buffer], typeof(Array16<NetNode>), "m_buffer")
                && ((FieldInfo)code[buffer].operand).FieldType == typeof(NetNode[])
                && Local(code[index], false) == cursor && code[element].opcode == OpCodes.Ldelema
                && object.Equals(code[element].operand, typeof(NetNode)) && Field(code[field], typeof(NetNode), next);
        }

        private static bool DictionaryUpdate(List<CodeInstruction> code, int store, int cursor, int start,
            IList<LocalVariableInfo> locals)
        {
            int source = Previous(code, store), address = Previous(code, source), valueStore = Previous(code, address);
            int get = Previous(code, valueStore), index = Previous(code, get), map = Previous(code, index);
            int self = Previous(code, map);
            if (self < start || code[self].opcode != OpCodes.Ldarg_0 || !Field(code[map], typeof(NetAdjust), "m_segmentData")
                || Local(code[index], false) != cursor || code[get].opcode != OpCodes.Callvirt) return false;
            FieldInfo dictionary = (FieldInfo)code[map].operand;
            if (!dictionary.FieldType.IsGenericType || dictionary.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)) return false;
            Type[] args = dictionary.FieldType.GetGenericArguments();
            Type value = args[1];
            if (args[0] != typeof(ushort) || value.DeclaringType != typeof(NetAdjust) || value.Name != "SegmentData"
                || !Field(code[source], value, "m_source") || ((FieldInfo)code[source].operand).FieldType != typeof(ushort)) return false;
            MethodInfo getter = code[get].operand as MethodInfo;
            if (getter == null || getter.DeclaringType != dictionary.FieldType || getter.Name != "get_Item"
                || getter.ReturnType != value || getter.GetParameters().Length != 1
                || getter.GetParameters()[0].ParameterType != typeof(ushort)) return false;
            int local = Local(code[valueStore], true);
            return local >= 0 && local < locals.Count && locals[local].LocalType == value && Address(code[address]) == local;
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
