using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager), "SimulationStepImpl", new Type[] { typeof(int) })]
    internal static class NetworkSimulationSlicePatch
    {
        // Preserve the game's 256-frame cadence and its complete AI,
        // notification and wetness logic. Both supported capacities divide 256.
        internal static int NodeSlice() { return Capacity.NodeLoopLimit >> 8; }
        internal static int SegmentSlice() { return Capacity.SegmentLoopLimit >> 8; }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int segment = FindPair(list, 144, "m_segments");
            int node = FindPair(list, 128, "m_nodes");
            // Validate both complete range calculations before modifying either.
            Replace(list, segment, AccessTools.Method(typeof(NetworkSimulationSlicePatch), "SegmentSlice", null, null));
            Replace(list, node, AccessTools.Method(typeof(NetworkSimulationSlicePatch), "NodeSlice", null, null));
            Logging.Info("Network AI simulation slices now follow active node/segment capacities (256-frame cycle).");
            return list;
        }

        private static void Replace(List<CodeInstruction> list, int index, MethodInfo method)
        {
            list[index].opcode = list[index + 6].opcode = OpCodes.Call;
            list[index].operand = list[index + 6].operand = method;
        }

        private static int FindPair(List<CodeInstruction> list, int size, string pool)
        {
            int found = -1, count = 0;
            for (int i = 4; i + 13 < list.Count; ++i)
            {
                if (!Constant(list[i], size)) continue;
                ++count;
                if (!Constant(list[i - 4], 255) || list[i - 3].opcode != OpCodes.And
                    || Local(list[i - 2], false) < 0
                    || Local(list[i - 1], true) != Local(list[i - 2], false)
                    || list[i + 1].opcode != OpCodes.Mul || Local(list[i + 2], false) < 0
                    || Local(list[i + 3], true) != Local(list[i - 2], false)
                    || !Constant(list[i + 4], 1) || list[i + 5].opcode != OpCodes.Add
                    || !Constant(list[i + 6], size) || list[i + 7].opcode != OpCodes.Mul
                    || !Constant(list[i + 8], 1) || list[i + 9].opcode != OpCodes.Sub
                    || Local(list[i + 10], false) < 0) continue;
                bool poolFound = false;
                for (int j = i + 11; j < Math.Min(i + 32, list.Count); ++j)
                {
                    FieldInfo field = list[j].operand as FieldInfo;
                    if (list[j].opcode == OpCodes.Ldfld && field != null
                        && field.DeclaringType == typeof(NetManager) && field.Name == pool)
                        poolFound = true;
                }
                if (poolFound) found = found < 0 ? i : -2;
            }
            if (count != 2 || found < 0)
                throw new InvalidOperationException("Network simulation range pattern changed: " + pool);
            return found;
        }

        internal static bool Constant(CodeInstruction instruction, int value)
        {
            if (value == 1 && instruction.opcode == OpCodes.Ldc_I4_1) return true;
            return (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S)
                && instruction.operand != null && Convert.ToInt32(instruction.operand) == value;
        }

        internal static int Local(CodeInstruction instruction, bool load)
        {
            OpCode op = instruction.opcode;
            if (op == (load ? OpCodes.Ldloc_0 : OpCodes.Stloc_0)) return 0;
            if (op == (load ? OpCodes.Ldloc_1 : OpCodes.Stloc_1)) return 1;
            if (op == (load ? OpCodes.Ldloc_2 : OpCodes.Stloc_2)) return 2;
            if (op == (load ? OpCodes.Ldloc_3 : OpCodes.Stloc_3)) return 3;
            if (op != (load ? OpCodes.Ldloc : OpCodes.Stloc)
                && op != (load ? OpCodes.Ldloc_S : OpCodes.Stloc_S)) return -1;
            LocalBuilder builder = instruction.operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo local = instruction.operand as LocalVariableInfo;
            return local != null ? local.LocalIndex : Convert.ToInt32(instruction.operand);
        }
    }
}
