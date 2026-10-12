using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch]
    internal static class NetworkDataSerializationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type electricityData = GetDataType(typeof(ElectricityManager));
            Type waterData = GetDataType(typeof(WaterManager));

            foreach (MethodBase method in GetSerializationMethods(electricityData))
            {
                yield return method;
            }
            foreach (MethodBase method in GetSerializationMethods(waterData))
            {
                yield return method;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo storageSizeMethod = AccessTools.Method(
                typeof(Capacity),
                "GetAuxiliaryNodeStorageSize",
                null,
                null);
            if (storageSizeMethod == null)
            {
                Logging.Error(
                    "Could not find "
                    + "Capacity.GetAuxiliaryNodeStorageSize(); "
                    + "leaving auxiliary node serialization unchanged.");
                return list;
            }

            int replacements = 0;

            for (int i = 0; i + 1 < list.Count; ++i)
            {
                if (!IsInt32Constant(
                        list[i],
                        Capacity.VanillaNodeCapacity)
                    || !ShouldReplace(original, list, i))
                {
                    continue;
                }

                list[i].opcode = OpCodes.Call;
                list[i].operand = storageSizeMethod;
                ++replacements;
            }

            if (replacements > 0)
            {
                Logging.Info(
                    "Auxiliary node serialization patched in "
                    + original.DeclaringType.FullName
                    + "."
                    + original.Name
                    + ": "
                    + replacements);
            }

            return list;
        }

        private static bool ShouldReplace(
            MethodBase original,
            List<CodeInstruction> list,
            int index)
        {
            bool isElectricity =
                original.DeclaringType.FullName
                    == "ElectricityManager+Data";
            bool isWater =
                original.DeclaringType.FullName
                    == "WaterManager+Data";
            bool deserialize = original.Name == "Deserialize";

            if (isElectricity)
            {
                return IsConditionalBranch(list[index + 1].opcode)
                    && HasFieldNear(
                        list,
                        index,
                        "m_nodeGroups",
                        true,
                        32);
            }

            if (!isWater)
            {
                return false;
            }

            if (!deserialize)
            {
                return IsConditionalBranch(list[index + 1].opcode)
                    && HasFieldNear(
                        list,
                        index,
                        "m_nodeData",
                        true,
                        32);
            }

            if (IsConditionalBranch(list[index + 1].opcode)
                && HasFieldNear(
                    list,
                    index,
                    "m_nodeData",
                    true,
                    32))
            {
                return true;
            }

            return IsStoreLocal(list[index + 1])
                && HasFieldNear(
                    list,
                    index,
                    "m_nodeData",
                    false,
                    16);
        }

        private static bool HasFieldNear(
            List<CodeInstruction> list,
            int index,
            string fieldName,
            bool lookBack,
            int distance)
        {
            int start = lookBack
                ? Math.Max(0, index - distance)
                : index;
            int end = lookBack
                ? index - 1
                : Math.Min(list.Count - 1, index + distance);

            for (int i = start; i <= end; ++i)
            {
                if (list[i].operand != null
                    && list[i].operand.ToString().Contains(fieldName))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<MethodBase> GetSerializationMethods(
            Type dataType)
        {
            if (dataType == null)
            {
                Logging.Warning("Could not find a manager Data type.");
                yield break;
            }

            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(dataType))
            {
                if (method.Name == "Serialize"
                    || method.Name == "Deserialize")
                {
                    yield return method;
                }
            }
        }

        private static Type GetDataType(Type managerType)
        {
            return managerType.GetNestedType(
                "Data",
                BindingFlags.Public | BindingFlags.NonPublic);
        }

        private static bool IsInt32Constant(
            CodeInstruction instruction,
            int value)
        {
            return (instruction.opcode == OpCodes.Ldc_I4
                    || instruction.opcode == OpCodes.Ldc_I4_S)
                && instruction.operand is int
                && (int)instruction.operand == value;
        }

        private static bool IsConditionalBranch(OpCode opcode)
        {
            return opcode == OpCodes.Blt
                || opcode == OpCodes.Blt_Un
                || opcode == OpCodes.Ble
                || opcode == OpCodes.Ble_Un
                || opcode == OpCodes.Bgt
                || opcode == OpCodes.Bgt_Un
                || opcode == OpCodes.Bge
                || opcode == OpCodes.Bge_Un
                || opcode == OpCodes.Beq
                || opcode == OpCodes.Beq_S
                || opcode == OpCodes.Bne_Un
                || opcode == OpCodes.Bne_Un_S;
        }

        private static bool IsStoreLocal(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Stloc_0
                || instruction.opcode == OpCodes.Stloc_1
                || instruction.opcode == OpCodes.Stloc_2
                || instruction.opcode == OpCodes.Stloc_3
                || instruction.opcode == OpCodes.Stloc
                || instruction.opcode == OpCodes.Stloc_S;
        }
    }
}
