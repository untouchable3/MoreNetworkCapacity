using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    internal static partial class TransportLineLoopBounds
    {
        // CopyMissingPaths advances two station cursors via separate temporaries
        // and continues only while both remain nonzero. It is not a one-cursor loop.
        internal static int[] ResolvePairedStops(List<CodeInstruction> code, MethodBase original)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < code.Count; ++i)
                if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, 32768)
                    && IsPairedStopGuard(code, i, original)) result.Add(i);
            return result.Count == 1 ? result.ToArray() : null;
        }

        private static bool IsPairedStopGuard(List<CodeInstruction> code, int bound, MethodBase original)
        {
            int branch = Next(code, bound);
            if (branch < 0 || (code[branch].opcode != OpCodes.Blt && code[branch].opcode != OpCodes.Blt_S)) return false;
            int condition = LabelIndex(code, code[branch].operand);
            if (condition <= branch) return false;
            condition = SkipNops(code, condition);
            int firstExit = Next(code, condition), second = Next(code, firstExit), back = Next(code, second);
            if (condition < 0 || back < 0) return false;
            int source = Local(code[condition], false), destination = Local(code[second], false);
            if (source < 0 || destination < 0 || source == destination
                || (code[firstExit].opcode != OpCodes.Brfalse && code[firstExit].opcode != OpCodes.Brfalse_S)
                || (code[back].opcode != OpCodes.Brtrue && code[back].opcode != OpCodes.Brtrue_S)) return false;
            int exit = LabelIndex(code, code[firstExit].operand), start = LabelIndex(code, code[back].operand);
            if (exit < 0 || SkipNops(code, exit) != Next(code, back) || start < 0 || start >= bound) return false;

            int store = Previous(code, bound), dup = Previous(code, store), add = Previous(code, dup);
            int one = Previous(code, add), load = Previous(code, one);
            if (load < start) return false;
            int counter = Local(code[store], true);
            if (counter < 0 || counter == source || counter == destination || !IsIntCounter(code[store], original) || Local(code[load], false) != counter
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add
                || code[one].opcode != OpCodes.Ldc_I4_1) return false;
            bool handler = false, handlerExits = false;
            for (int i = branch + 1; i < condition; ++i)
            {
                if (code[i].opcode == OpCodes.Ldstr && object.Equals(code[i].operand, "Invalid list detected!\n")) handler = true;
                if ((code[i].opcode == OpCodes.Br || code[i].opcode == OpCodes.Br_S)
                    && LabelIndex(code, code[i].operand) == exit) handlerExits = true;
            }
            if (!handler || !handlerExits) return false;
            if (!PairedCursorUpdate(code, source, start, load) || !PairedCursorUpdate(code, destination, start, load)) return false;
            if (!StationCursorRoot(code, source, start) || !StationCursorRoot(code, destination, start)) return false;
            int nextCalls = 0;
            for (int i = start; i < bound; ++i) if (IsNextStopCall(code[i])) ++nextCalls;
            return nextCalls == 2;
        }

        private static bool IsNextStopCall(CodeInstruction instruction)
        {
            MethodInfo method = instruction.operand as MethodInfo;
            if (instruction.opcode != OpCodes.Call || method == null || method.DeclaringType != typeof(TransportLine)
                || method.Name != "GetNextStop" || !method.IsStatic || method.ReturnType != typeof(ushort)) return false;
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(ushort);
        }

        private static bool PairedCursorUpdate(List<CodeInstruction> code, int cursor, int start, int end)
        {
            int store = LastStore(code, cursor, start, end), load = Previous(code, store);
            if (load < start) return false;
            int temporary = Local(code[load], false);
            if (temporary < 0 || temporary == cursor) return false;
            int tempStore = LastStore(code, temporary, start, store), call = Previous(code, tempStore);
            int argument = Previous(code, call);
            return argument >= start && IsNextStopCall(code[call]) && Local(code[argument], false) == cursor;
        }

        private static bool StationCursorRoot(List<CodeInstruction> code, int cursor, int start)
        {
            int store = LastStore(code, cursor, 0, start), load = Previous(code, store);
            if (load < 0) return false;
            int initial = Local(code[load], false);
            if (initial < 0 || initial == cursor) return false;
            int initialStore = LastStore(code, initial, 0, store), fieldIndex = Previous(code, initialStore);
            if (fieldIndex < 0) return false;
            FieldInfo field = code[fieldIndex].operand as FieldInfo;
            return code[fieldIndex].opcode == OpCodes.Ldfld && field != null
                && field.DeclaringType == typeof(TransportLine) && field.Name == "m_stops" && field.FieldType == typeof(ushort);
        }
    }
}
