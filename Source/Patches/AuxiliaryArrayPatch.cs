using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    internal static class AuxiliaryArrayPatchUtility
    {
        public static List<CodeInstruction> ExpandArrays(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions,
            int methodToken,
            int[,] replacements,
            string description)
        {
            ValidateMethod(original, methodToken);
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            List<int> indexes = FindArrayAllocationIndexes(
                list,
                Capacity.VanillaNodeCapacity);

            if (indexes.Count != replacements.GetLength(0))
            {
                Logging.Error(
                    description
                    + " array allocation pattern mismatch: expected "
                    + replacements.GetLength(0)
                    + ", found "
                    + indexes.Count
                    + " in "
                    + list.Count
                    + " instructions. Leaving arrays unchanged.");
                return list;
            }

            for (int i = 0; i < indexes.Count; ++i)
            {
                CodeInstruction instruction = list[indexes[i]];
                instruction.opcode = OpCodes.Ldc_I4;
                instruction.operand = Capacity.ExpandedNodeCapacity;
            }

            Logging.Info(
                description
                + " node-indexed arrays expanded in "
                + indexes.Count
                + " locations.");
            return list;
        }

        public static List<CodeInstruction> ExpandSimulationSlices(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions,
            int methodToken,
            int[,] replacements,
            string description)
        {
            ValidateMethod(original, methodToken);
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            FieldInfo loopLimit = AccessTools.Field(
                typeof(Capacity),
                "NodeLoopLimit");
            List<int> indexes = FindSimulationSliceIndexes(list);

            if (indexes.Count != replacements.GetLength(0))
            {
                Logging.Error(
                    description
                    + " simulation slice pattern mismatch: expected "
                    + replacements.GetLength(0)
                    + ", found "
                    + indexes.Count
                    + " in "
                    + list.Count
                    + " instructions. Leaving slices unchanged.");
                return list;
            }

            for (int i = 0; i < indexes.Count; ++i)
            {
                CodeInstruction instruction = list[indexes[i]];
                instruction.opcode = OpCodes.Ldsfld;
                instruction.operand = loopLimit;
            }

            Logging.Info(
                description
                + " simulation slices patched in "
                + indexes.Count
                + " locations.");
            return list;
        }

        private static void ValidateMethod(
            MethodBase original,
            int methodToken)
        {
            if (original.MetadataToken != methodToken)
            {
                Logging.Warning(
                    "Runtime method token differs for "
                    + original.DeclaringType.FullName
                    + "."
                    + original.Name
                    + " (expected "
                    + methodToken
                    + ", found "
                    + original.MetadataToken
                    + "). Matching by method identity.");
            }
        }

        private static List<int> FindArrayAllocationIndexes(
            List<CodeInstruction> instructions,
            int capacity)
        {
            List<int> result = new List<int>();
            for (int i = 0; i + 2 < instructions.Count; ++i)
            {
                if (!IsInt32Constant(instructions[i], capacity)
                    || instructions[i + 1].opcode != OpCodes.Newarr
                    || instructions[i + 2].opcode != OpCodes.Stfld)
                {
                    continue;
                }

                result.Add(i);
            }

            return result;
        }

        private static List<int> FindSimulationSliceIndexes(
            List<CodeInstruction> instructions)
        {
            List<int> result = new List<int>();
            for (int i = 1; i + 3 < instructions.Count; ++i)
            {
                if (!IsInt32Constant(
                        instructions[i],
                        Capacity.VanillaNodeCapacity)
                    || instructions[i + 1].opcode != OpCodes.Mul
                    || !IsInt32Constant(instructions[i + 2], 7)
                    || instructions[i + 3].opcode != OpCodes.Shr
                    || (!IsLoadLocal(instructions[i - 1])
                        && (i < 3
                            || !IsLoadLocal(instructions[i - 3])
                            || !IsInt32Constant(
                                instructions[i - 2],
                                1)
                            || instructions[i - 1].opcode
                                != OpCodes.Add)))
                {
                    continue;
                }

                result.Add(i);
            }

            return result;
        }

        private static bool IsLoadLocal(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldloc_0
                || instruction.opcode == OpCodes.Ldloc_1
                || instruction.opcode == OpCodes.Ldloc_2
                || instruction.opcode == OpCodes.Ldloc_3
                || instruction.opcode == OpCodes.Ldloc
                || instruction.opcode == OpCodes.Ldloc_S;
        }

        private static bool IsInt32Constant(
            CodeInstruction instruction,
            int value)
        {
            if (instruction.opcode == OpCodes.Ldc_I4_M1)
            {
                return value == -1;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_0)
            {
                return value == 0;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_1)
            {
                return value == 1;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_2)
            {
                return value == 2;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_3)
            {
                return value == 3;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_4)
            {
                return value == 4;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_5)
            {
                return value == 5;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_6)
            {
                return value == 6;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_7)
            {
                return value == 7;
            }

            if (instruction.opcode == OpCodes.Ldc_I4_8)
            {
                return value == 8;
            }

            if (instruction.opcode == OpCodes.Ldc_I4
                || instruction.opcode == OpCodes.Ldc_I4_S)
            {
                if (instruction.operand is int)
                {
                    return (int)instruction.operand == value;
                }

                if (instruction.operand is sbyte)
                {
                    return (sbyte)instruction.operand == value;
                }

                if (instruction.operand is byte)
                {
                    return (byte)instruction.operand == value;
                }

                if (instruction.operand is short)
                {
                    return (short)instruction.operand == value;
                }
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(CoverageManager), "Awake")]
    internal static class CoverageManagerAwakePatch
    {
        private static readonly int[,] Replacements =
        {
            { 6, 0x0012 },
            { 10, 0x0022 },
        };

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return AuxiliaryArrayPatchUtility.ExpandArrays(
                original,
                instructions,
                100675981,
                Replacements,
                "Coverage");
        }
    }

    [HarmonyPatch(typeof(ElectricityManager), "Awake")]
    internal static class ElectricityManagerAwakePatch
    {
        private static readonly int[,] Replacements =
        {
            { 11, 0x0027 },
            { 15, 0x0037 },
        };

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return AuxiliaryArrayPatchUtility.ExpandArrays(
                original,
                instructions,
                100676087,
                Replacements,
                "Electricity");
        }
    }

    [HarmonyPatch(typeof(WaterManager), "Awake")]
    internal static class WaterManagerAwakePatch
    {
        private static readonly int[,] Replacements =
        {
            { 19, 0x0047 },
            { 23, 0x0057 },
            { 27, 0x0067 },
            { 31, 0x0077 },
        };

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return AuxiliaryArrayPatchUtility.ExpandArrays(
                original,
                instructions,
                100676587,
                Replacements,
                "Water");
        }
    }

    [HarmonyPatch(typeof(ElectricityManager), "SimulationStepImpl")]
    internal static class ElectricityManagerSimulationStepPatch
    {
        private static readonly int[,] Replacements =
        {
            { 38, 0x0066 },
            { 46, 0x0072 },
        };

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return AuxiliaryArrayPatchUtility.ExpandSimulationSlices(
                original,
                instructions,
                100676105,
                Replacements,
                "Electricity");
        }
    }

    [HarmonyPatch(typeof(WaterManager), "SimulationStepImpl")]
    internal static class WaterManagerSimulationStepPatch
    {
        private static readonly int[,] Replacements =
        {
            { 56, 0x0090 },
            { 64, 0x009C },
        };

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return AuxiliaryArrayPatchUtility.ExpandSimulationSlices(
                original,
                instructions,
                100676625,
                Replacements,
                "Water");
        }
    }
}
