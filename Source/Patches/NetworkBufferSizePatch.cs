using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Validate both serialized pool lengths before changing either one.
    internal static class NetworkBufferSizePatch
    {
        internal static void Apply(
            List<CodeInstruction> instructions,
            FieldInfo nodes, FieldInfo nodeBuffer,
            FieldInfo segments, FieldInfo segmentBuffer,
            MethodInfo nodeSize, MethodInfo segmentSize)
        {
            if (nodes == null || nodeBuffer == null || segments == null
                || segmentBuffer == null || nodeSize == null || segmentSize == null)
            {
                throw new InvalidOperationException("Network serialization fields or size getters are missing.");
            }

            int nodeIndex = FindLength(instructions, nodes, nodeBuffer);
            int segmentIndex = FindLength(instructions, segments, segmentBuffer);
            ReplaceLength(instructions, nodeIndex, nodeSize);
            ReplaceLength(instructions, segmentIndex, segmentSize);
        }

        private static int FindLength(
            List<CodeInstruction> instructions, FieldInfo pool, FieldInfo buffer)
        {
            int result = -1;
            int captures = 0;
            for (int i = 0; i + 2 < instructions.Count; ++i)
            {
                if (instructions[i].opcode != OpCodes.Ldfld
                    || !pool.Equals(instructions[i].operand)
                    || instructions[i + 1].opcode != OpCodes.Ldfld
                    || !buffer.Equals(instructions[i + 1].operand))
                    continue;

                int local = LocalIndex(instructions[i + 2], false);
                if (local < 0)
                    throw new InvalidOperationException("Network buffer capture changed: " + pool.Name);
                ++captures;
                for (int j = i + 3; j < instructions.Count; ++j)
                {
                    if (LocalIndex(instructions[j], false) == local)
                        break;
                    if (LocalIndex(instructions[j], true) != local)
                        continue;
                    if (j + 2 < instructions.Count
                        && instructions[j + 1].opcode == OpCodes.Ldlen
                        && instructions[j + 2].opcode == OpCodes.Conv_I4)
                        result = j;
                    break;
                }
            }
            if (captures != 1 || result < 0)
                throw new InvalidOperationException("Expected one network buffer length capture: " + pool.Name);
            return result;
        }

        private static void ReplaceLength(
            List<CodeInstruction> instructions, int index, MethodInfo size)
        {
            // Mutate in place so incoming labels and exception blocks survive.
            instructions[index].opcode = OpCodes.Nop;
            instructions[index].operand = null;
            instructions[index + 1].opcode = OpCodes.Call;
            instructions[index + 1].operand = size;
        }

        private static int LocalIndex(CodeInstruction instruction, bool load)
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
            LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (instruction.operand is byte || instruction.operand is int
                || instruction.operand is short) return Convert.ToInt32(instruction.operand);
            return -1;
        }
    }
}
