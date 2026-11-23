using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Reviewed stop-chain methods can contain unrelated CitizenInstance guards
    // with the same constant. Match cursor provenance and loop control flow.
    internal static partial class TransportLineLoopBounds
    {
        internal static bool TryGetReviewedShape(string method, out int segmentUpdates, out int nextStopUpdates)
        {
            segmentUpdates = 0; nextStopUpdates = 0;
            if (method == "SimulationStep") { segmentUpdates = 2; nextStopUpdates = 1; return true; }
            if (method == "GetDebugString") { segmentUpdates = 1; return true; }
            if (method == "CalculateTargetVehicleCount") { segmentUpdates = 1; return true; }
            if (method == "UpdatePaths") { segmentUpdates = 1; nextStopUpdates = 1; return true; }
            if (method == "CountStops" || method == "GetStop") { nextStopUpdates = 1; return true; }
            return false;
        }

        internal static List<int> FindStopGuards(List<CodeInstruction> code, int expectedSegmentUpdates, int expectedNextStopUpdates, MethodBase original)
        {
            List<int> result = new List<int>();
            int segmentUpdates = 0, nextStopUpdates = 0;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4
                    || !object.Equals(code[i].operand, 32768)) continue;
                int kind = StopGuardKind(code, i, original);
                if (kind == 0) continue;
                result.Add(i);
                if (kind == 1) ++segmentUpdates; else ++nextStopUpdates;
            }
            // An added, removed or changed traversal is not this reviewed layout.
            if (segmentUpdates != expectedSegmentUpdates || nextStopUpdates != expectedNextStopUpdates) result.Clear();
            return result;
        }

        private static int StopGuardKind(List<CodeInstruction> code, int bound, MethodBase original)
        {
            int branch = Next(code, bound);
            if (branch < 0 || (code[branch].opcode != OpCodes.Blt
                && code[branch].opcode != OpCodes.Blt_S)) return 0;
            int condition = LabelIndex(code, code[branch].operand);
            if (condition <= branch) return 0;
            condition = SkipNops(code, condition);
            if (condition < 0) return 0;
            int cursor = Local(code[condition], false);
            int backBranch = Next(code, condition);
            if (cursor < 0 || backBranch < 0
                || (code[backBranch].opcode != OpCodes.Brtrue
                    && code[backBranch].opcode != OpCodes.Brtrue_S)) return 0;
            int start = LabelIndex(code, code[backBranch].operand);
            if (start < 0 || start >= bound) return 0;

            // Require the original int counter increment; do not expand a
            // comparison that has been narrowed to a wrapping ushort counter.
            int store = Previous(code, bound);
            if (store < 0) return 0;
            int counter = Local(code[store], true);
            int dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (counter < 0 || counter == cursor || load < 0 || !IsIntCounter(code[store], original)
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1
                || Local(code[load], false) != counter) return 0;

            // The forward branch skips the invalid-list handler, then tests
            // this same cursor before returning to the loop body.
            bool handler = false;
            for (int i = branch + 1; i < condition; ++i)
                if (code[i].opcode == OpCodes.Ldstr
                    && object.Equals(code[i].operand, "Invalid list detected!\n")) handler = true;
            if (!handler) return 0;

            int cursorStore = LastStore(code, cursor, start, load);
            int update = Previous(code, cursorStore);
            if (update < start) return 0;
            MethodInfo next = code[update].operand as MethodInfo;
            if (code[update].opcode == OpCodes.Call && next != null
                && next.DeclaringType == typeof(TransportLine)
                && next.Name == "GetNextStop" && next.IsStatic
                && next.ReturnType == typeof(ushort))
            {
                ParameterInfo[] parameters = next.GetParameters();
                int input = Previous(code, update);
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(ushort)
                    && input >= start && Local(code[input], false) == cursor) return 2;
                return 0;
            }

            int source = Local(code[update], false);
            if (source < 0 || source == cursor) return 0;
            int sourceStore = LastStore(code, source, start, cursorStore);
            int endNode = Previous(code, sourceStore);
            int element = Previous(code, endNode), segment = Previous(code, element);
            int buffer = Previous(code, segment), pool = Previous(code, buffer);
            if (pool < start || Local(code[segment], false) < 0) return 0;
            FieldInfo endField = code[endNode].operand as FieldInfo;
            FieldInfo bufferField = code[buffer].operand as FieldInfo;
            FieldInfo poolField = code[pool].operand as FieldInfo;
            if (code[endNode].opcode == OpCodes.Ldfld && endField != null
                && endField.DeclaringType == typeof(NetSegment) && endField.Name == "m_endNode"
                && code[element].opcode == OpCodes.Ldelema && object.Equals(code[element].operand, typeof(NetSegment))
                && code[buffer].opcode == OpCodes.Ldfld && bufferField != null
                && bufferField.Name == "m_buffer" && bufferField.FieldType == typeof(NetSegment[])
                && code[pool].opcode == OpCodes.Ldfld && poolField != null
                && poolField.DeclaringType == typeof(NetManager) && poolField.Name == "m_segments") return 1;
            return 0;
        }

        private static int LastStore(List<CodeInstruction> code, int local, int start, int end)
        {
            for (int i = end - 1; i >= start; --i)
                if (Local(code[i], true) == local) return i;
            return -1;
        }

        private static bool IsIntCounter(CodeInstruction store, MethodBase original)
        {
            LocalBuilder builder = store.operand as LocalBuilder;
            if (builder != null) return builder.LocalType == typeof(int);
            LocalVariableInfo variable = store.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalType == typeof(int);
            int index = Local(store, true);
            if (original == null || index < 0) return false;
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            return index < locals.Count && locals[index].LocalType == typeof(int);
        }

        private static int LabelIndex(List<CodeInstruction> code, object operand)
        {
            if (!(operand is Label)) return -1;
            Label label = (Label)operand;
            int result = -1;
            for (int i = 0; i < code.Count; ++i)
                if (code[i].labels.Contains(label))
                {
                    if (result != -1) return -1;
                    result = i;
                }
            return result;
        }

        private static int Next(List<CodeInstruction> code, int index)
        {
            return index < 0 ? -1 : SkipNops(code, index + 1);
        }
        private static int SkipNops(List<CodeInstruction> code, int index)
        {
            while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index;
            return index < code.Count ? index : -1;
        }
        private static int Previous(List<CodeInstruction> code, int index)
        {
            if (index < 0) return -1;
            --index;
            while (index >= 0 && code[index].opcode == OpCodes.Nop) --index;
            return index;
        }

        private static int Local(CodeInstruction code, bool store)
        {
            OpCode op = code.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (op != (store ? OpCodes.Stloc : OpCodes.Ldloc)
                && op != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            LocalBuilder builder = code.operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = code.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (code.operand is int) return (int)code.operand;
            if (code.operand is byte) return (byte)code.operand;
            if (code.operand is short) return (short)code.operand;
            return -1;
        }
    }
}
