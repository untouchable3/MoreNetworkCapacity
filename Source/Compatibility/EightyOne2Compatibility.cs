using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework.IO;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2Compatibility
    {
        private const string AssemblyName = "EightyOne2";
        private const string ModName = "EightyOne2";
        private const string ExpandedManagerTypeName =
            "EightyOne2.Patches.ExpandedElectricityManager";
        private const string DataContainerTypeName =
            "EightyOne2.Serialization.ElectricityDataContainer";
        private const string ExpandedWaterManagerTypeName =
            "EightyOne2.Patches.ExpandedWaterManager";
        private const string WaterDataPatchesTypeName =
            "EightyOne2.Patches.WaterManagerDataPatches";

        private static readonly string[] WaterStaticArrayFields =
        {
            "s_waterPulseUnits",
            "s_sewagePulseUnits",
            "s_heatingPulseUnits",
        };

        private static readonly HashSet<string> PatchedMethods =
            new HashSet<string>();

        private static Assembly _assembly;
        private static Type _expandedManagerType;
        private static Type _expandedWaterManagerType;

        internal static void Reset()
        {
            PatchedMethods.Clear();
            _assembly = null;
            _expandedManagerType = null;
            _expandedWaterManagerType = null;
        }

        internal static void Apply(Harmony harmony)
        {
            _assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (_assembly == null)
            {
                Logging.Info(
                    "EightyOne2 is not loaded; compatibility is inactive.");
                return;
            }

            _expandedManagerType =
                ThirdPartyCapacityCompatibility.FindType(
                    _assembly,
                    ExpandedManagerTypeName,
                    ModName);
            if (_expandedManagerType == null)
            {
                Logging.Warning(
                    "EightyOne2 is loaded but its expanded electricity "
                    + "manager was not found.");
                return;
            }

            EnsureStaticArrays(Capacity.ExpandedNodeCapacity);

            _expandedWaterManagerType =
                ThirdPartyCapacityCompatibility.FindType(
                    _assembly,
                    ExpandedWaterManagerTypeName,
                    ModName);
            if (_expandedWaterManagerType == null)
            {
                Logging.Warning(
                    "EightyOne2 is loaded but its expanded water manager "
                    + "was not found; water serialization may overflow.");
            }
            else
            {
                EnsureWaterStaticArrays(Capacity.ExpandedNodeCapacity);
            }

            PatchPrefixes(harmony);
            PatchDataContainer(
                harmony,
                "Serialize",
                "DataContainerSerializeTranspiler",
                1);
            PatchDataContainer(
                harmony,
                "Deserialize",
                "DataContainerDeserializeTranspiler",
                2);
            PatchWaterReadValidation(harmony);

            Logging.Info("EightyOne2 compatibility pass completed.");
        }

        public static void CustomSerializePrefix(object[] __args)
        {
            EnsureStaticArraysForArguments(__args, 1);
        }

        public static void CustomDeserializePrefix(object[] __args)
        {
            EnsureStaticArraysForArguments(__args, 2);
        }

        // WaterManagerDataPatches.CustomSerialize
        //   (WaterManager.Cell[] cells,
        //    WaterManager.PulseUnit[] water,
        //    WaterManager.PulseUnit[] sewage,
        //    WaterManager.PulseUnit[] heating)
        public static void WaterCustomSerializePrefix(object[] __args)
        {
            EnsureWaterStaticArraysForArguments(__args, 1);
        }

        // WaterManagerDataPatches.CustomDeserialize
        //   (WaterManager manager,
        //    WaterManager.Cell[] cells,
        //    WaterManager.PulseUnit[] water,
        //    WaterManager.PulseUnit[] sewage,
        //    WaterManager.PulseUnit[] heating)
        public static void WaterCustomDeserializePrefix(object[] __args)
        {
            EnsureWaterStaticArraysForArguments(__args, 2);
        }

        public static IEnumerable<CodeInstruction>
            DataContainerSerializeTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeGroupCapacity(
                instructions,
                1,
                "ElectricityDataContainer.Serialize");
        }

        public static IEnumerable<CodeInstruction>
            DataContainerDeserializeTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeGroupCapacity(
                instructions,
                2,
                "ElectricityDataContainer.Deserialize");
        }

        // WaterDataContainer.ReadPulseGroups / ReadPulseUnits reject and clear
        // any entry whose node id is >= 32768 while reading. On an expanded
        // save that would wipe the water state of every expanded node.
        public static IEnumerable<CodeInstruction>
            NodeLimitValidationTranspiler(
                MethodBase original,
                IEnumerable<CodeInstruction> instructions)
        {
            string label = "WaterDataContainer";
            if (original != null && original.DeclaringType != null)
            {
                label = original.DeclaringType.Name + "." + original.Name;
            }

            return ReplaceSingleNodeLimit(
                instructions,
                label);
        }

        private static void PatchWaterReadValidation(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                _assembly,
                "EightyOne2.Serialization.WaterDataContainer",
                ModName);
            if (type == null)
            {
                return;
            }

            PatchConstantTranspiler(
                harmony,
                type,
                "ReadPulseGroups",
                2);
            PatchConstantTranspiler(
                harmony,
                type,
                "ReadPulseUnits",
                2);
        }

        private static void PatchConstantTranspiler(
            Harmony harmony,
            Type type,
            string methodName,
            int parameterCount)
        {
            string key = type.FullName + "|" + methodName + "|nodelimit";
            if (!PatchedMethods.Add(key))
            {
                return;
            }

            MethodInfo target = null;
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Static
                | BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; ++i)
            {
                if (methods[i].Name == methodName
                    && methods[i].GetParameters().Length
                        == parameterCount)
                {
                    target = methods[i];
                    break;
                }
            }

            if (target == null)
            {
                PatchedMethods.Remove(key);
                Logging.Warning(
                    "Could not find "
                    + type.Name
                    + "."
                    + methodName
                    + " for expanded node id validation.");
                return;
            }

            MethodInfo transpiler = typeof(EightyOne2Compatibility)
                .GetMethod(
                    "NodeLimitValidationTranspiler",
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
            harmony.Patch(
                target,
                null,
                null,
                new HarmonyMethod(transpiler),
                null);
            Logging.Info(
                "Patched "
                + type.Name
                + "."
                + methodName
                + " so expanded node ids are not rejected while reading.");
        }

        private static void PatchPrefixes(Harmony harmony)
        {
            if (harmony == null)
            {
                Logging.Warning(
                    "Harmony is unavailable; EightyOne2 serialization "
                    + "prefixes are inactive.");
                return;
            }

            Type electricityDataPatches =
                ThirdPartyCapacityCompatibility.FindType(
                _assembly,
                "EightyOne2.Patches.ElectricityManagerDataPatches",
                ModName);
            if (electricityDataPatches != null)
            {
                PatchPrefix(
                    harmony,
                    electricityDataPatches,
                    "CustomSerialize",
                    "CustomSerializePrefix",
                    "electricity");
                PatchPrefix(
                    harmony,
                    electricityDataPatches,
                    "CustomDeserialize",
                    "CustomDeserializePrefix",
                    "electricity");
            }

            Type waterDataPatches =
                ThirdPartyCapacityCompatibility.FindType(
                    _assembly,
                    WaterDataPatchesTypeName,
                    ModName);
            if (waterDataPatches != null)
            {
                PatchPrefix(
                    harmony,
                    waterDataPatches,
                    "CustomSerialize",
                    "WaterCustomSerializePrefix",
                    "water");
                PatchPrefix(
                    harmony,
                    waterDataPatches,
                    "CustomDeserialize",
                    "WaterCustomDeserializePrefix",
                    "water");
            }
        }

        private static void PatchPrefix(
            Harmony harmony,
            Type type,
            string methodName,
            string prefixName,
            string label)
        {
            string key = type.FullName + "|" + methodName;
            if (!PatchedMethods.Add(key))
            {
                return;
            }

            MethodInfo target = null;
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Static
                | BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; ++i)
            {
                if (methods[i].Name == methodName)
                {
                    target = methods[i];
                    break;
                }
            }

            if (target == null)
            {
                PatchedMethods.Remove(key);
                Logging.Warning(
                    "Could not find EightyOne2."
                    + type.Name
                    + "."
                    + methodName
                    + " for serialization compatibility.");
                return;
            }

            MethodInfo prefix = typeof(EightyOne2Compatibility).GetMethod(
                prefixName,
                BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            harmony.Patch(
                target,
                new HarmonyMethod(prefix),
                null,
                null,
                null);
            Logging.Info(
                "Patched EightyOne2."
                + type.Name
                + "."
                + methodName
                + " for expanded "
                + label
                + " storage.");
        }

        private static void PatchDataContainer(
            Harmony harmony,
            string methodName,
            string transpilerName,
            int expectedReplacements)
        {
            if (harmony == null)
            {
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                _assembly,
                DataContainerTypeName,
                ModName);
            if (type == null)
            {
                return;
            }

            string key = type.FullName + "|" + methodName + "|container";
            if (!PatchedMethods.Add(key))
            {
                return;
            }

            MethodInfo target = type.GetMethod(
                methodName,
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic,
                null,
                new Type[] { typeof(DataSerializer) },
                null);
            if (target == null)
            {
                PatchedMethods.Remove(key);
                Logging.Warning(
                    "Could not find "
                    + DataContainerTypeName
                    + "."
                    + methodName
                    + " for expanded serialization.");
                return;
            }

            MethodInfo transpiler = typeof(EightyOne2Compatibility)
                .GetMethod(
                    transpilerName,
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
            harmony.Patch(
                target,
                null,
                null,
                new HarmonyMethod(transpiler),
                null);
            Logging.Info(
                "Patched "
                + DataContainerTypeName
                + "."
                + methodName
                + " for "
                + expectedReplacements
                + " expanded electricity length(s).");
        }

        private static void EnsureStaticArraysForArguments(
            object[] arguments,
            int firstPulseArrayIndex)
        {
            int required = Capacity.ExpandedNodeCapacity;
            if (arguments != null)
            {
                for (int i = firstPulseArrayIndex;
                    i < firstPulseArrayIndex + 2
                        && i < arguments.Length;
                    ++i)
                {
                    Array array = arguments[i] as Array;
                    if (array != null && array.Length > required)
                    {
                        required = array.Length;
                    }
                }
            }

            EnsureStaticArrays(required);
        }

        private static void EnsureStaticArrays(int requiredLength)
        {
            if (_expandedManagerType == null)
            {
                return;
            }

            if (requiredLength < Capacity.ExpandedNodeCapacity)
            {
                requiredLength = Capacity.ExpandedNodeCapacity;
            }

            EnsureStaticArray(
                _expandedManagerType,
                "electricity",
                "s_pulseGroups",
                requiredLength);
            EnsureStaticArray(
                _expandedManagerType,
                "electricity",
                "s_pulseUnits",
                requiredLength);
        }

        // The three water pulse arrays are converted one at a time. Each
        // ConvertExpandedPulseUnits call indexes the matching expanded static
        // array using the length of the manager array that was passed in, so
        // every expanded array has to be at least as long as its counterpart.
        private static void EnsureWaterStaticArraysForArguments(
            object[] arguments,
            int firstPulseArrayIndex)
        {
            for (int i = 0; i < WaterStaticArrayFields.Length; ++i)
            {
                int required = Capacity.ExpandedNodeCapacity;
                int argumentIndex = firstPulseArrayIndex + i;
                if (arguments != null && argumentIndex < arguments.Length)
                {
                    Array array = arguments[argumentIndex] as Array;
                    if (array != null && array.Length > required)
                    {
                        required = array.Length;
                    }
                }

                EnsureWaterStaticArray(i, required);
            }
        }

        private static void EnsureWaterStaticArrays(int requiredLength)
        {
            for (int i = 0; i < WaterStaticArrayFields.Length; ++i)
            {
                EnsureWaterStaticArray(i, requiredLength);
            }
        }

        private static void EnsureWaterStaticArray(
            int index,
            int requiredLength)
        {
            if (_expandedWaterManagerType == null)
            {
                return;
            }

            if (requiredLength < Capacity.ExpandedNodeCapacity)
            {
                requiredLength = Capacity.ExpandedNodeCapacity;
            }

            EnsureStaticArray(
                _expandedWaterManagerType,
                "water",
                WaterStaticArrayFields[index],
                requiredLength);
        }

        private static void EnsureStaticArray(
            Type ownerType,
            string label,
            string fieldName,
            int requiredLength)
        {
            FieldInfo field = ThirdPartyCapacityCompatibility.FindField(
                ownerType,
                fieldName);
            if (field == null || !field.IsStatic)
            {
                Logging.Warning(
                    "Could not find EightyOne2 static "
                    + label
                    + " array "
                    + fieldName
                    + ".");
                return;
            }

            try
            {
                Array current = field.GetValue(null) as Array;
                if (current != null && current.Length >= requiredLength)
                {
                    return;
                }

                Type elementType = field.FieldType.GetElementType();
                if (elementType == null)
                {
                    Logging.Warning(
                        "EightyOne2 member is not an array: "
                        + fieldName
                        + ".");
                    return;
                }

                Array replacement =
                    Array.CreateInstance(elementType, requiredLength);
                int previousLength = 0;
                if (current != null)
                {
                    previousLength = current.Length;
                    Array.Copy(current, replacement, previousLength);
                }

                field.SetValue(null, replacement);
                Logging.Info(
                    "Expanded EightyOne2 static "
                    + label
                    + " array "
                    + fieldName
                    + ": "
                    + previousLength
                    + " -> "
                    + requiredLength
                    + ".");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to expand EightyOne2 static "
                    + label
                    + " "
                    + "array "
                    + fieldName
                    + ": "
                    + exception);
            }
        }

        private static IEnumerable<CodeInstruction>
            ReplaceNodeGroupCapacity(
                IEnumerable<CodeInstruction> instructions,
                int expected,
                string label)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo helper = AccessTools.Method(
                typeof(Capacity),
                "GetElectricityNodeGroupLength",
                null,
                null);
            List<int> indexes = new List<int>();
            for (int i = 0; i < list.Count; ++i)
            {
                if (IsInt32Constant(
                        list[i],
                        Capacity.VanillaNodeCapacity))
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count != expected)
            {
                Logging.Error(
                    "EightyOne2 "
                    + label
                    + " length pattern mismatch: expected "
                    + expected
                    + ", found "
                    + indexes.Count
                    + ". Leaving values unchanged.");
                return list;
            }

            for (int i = 0; i < indexes.Count; ++i)
            {
                CodeInstruction instruction = list[indexes[i]];
                instruction.opcode = OpCodes.Call;
                instruction.operand = helper;
            }

            return list;
        }

        private static IEnumerable<CodeInstruction>
            ReplaceSingleNodeLimit(
                IEnumerable<CodeInstruction> instructions,
                string label)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            List<int> indexes = new List<int>();
            for (int i = 0; i < list.Count; ++i)
            {
                if (IsInt32Constant(
                        list[i],
                        Capacity.VanillaNodeCapacity))
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count != 1)
            {
                Logging.Error(
                    "EightyOne2 "
                    + label
                    + " node-limit pattern mismatch: expected 1, found "
                    + indexes.Count
                    + ". Leaving values unchanged.");
                return list;
            }

            MethodInfo helper = typeof(Capacity).GetMethod(
                "GetNodeIdValidationLimit",
                BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (helper == null)
            {
                Logging.Error(
                    "Could not find Capacity.GetNodeIdValidationLimit(); "
                    + "leaving EightyOne2 "
                    + label
                    + " unchanged.");
                return list;
            }

            CodeInstruction instruction = list[indexes[0]];
            instruction.opcode = OpCodes.Call;
            instruction.operand = helper;
            return list;
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
    }
}
