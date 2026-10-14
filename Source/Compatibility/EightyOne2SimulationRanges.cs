using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2SimulationRanges
    {
        private static bool _installed;
        internal static void Reset() { _installed = false; }

        internal static int NodeCount()
        {
            // Follow the same activation gate as the game's node simulation.
            // Main-menu installation must not create manager singletons.
            int count = Capacity.NodeLoopLimit;
            if (count != Capacity.VanillaNodeCapacity && count != Capacity.ExpandedNodeCapacity)
                throw new InvalidOperationException("Unsupported 81 Tiles node simulation capacity: " + count);
            return count;
        }

        internal static MethodInfo[] Resolve(Assembly assembly)
        {
            string[] names = { "ExpandedElectricityManager", "ExpandedWaterManager" };
            int[] parameters = { 9, 18 };
            MethodInfo[] result = new MethodInfo[2];
            for (int i = 0; i < names.Length; ++i)
            {
                Type type = assembly.GetType("EightyOne2.Patches." + names[i], false);
                if (type == null) throw new InvalidOperationException("81 Tiles simulation type missing: " + names[i]);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] args = method.GetParameters();
                    if (method.Name != "SimulationStepImpl" || args.Length != parameters[i]
                        || args[0].ParameterType != (i == 0 ? typeof(ElectricityManager) : typeof(WaterManager))
                        || args[1].ParameterType != typeof(int) || method.ReturnType != typeof(void)) continue;
                    if (result[i] != null) throw new InvalidOperationException("Ambiguous 81 Tiles simulation target.");
                    result[i] = method;
                }
                if (result[i] == null) throw new InvalidOperationException("81 Tiles simulation signature changed: " + names[i]);
            }
            return result;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (_installed || harmony == null) return;
            MethodInfo[] targets = Resolve(assembly);
            MethodInfo patch = AccessTools.Method(typeof(EightyOne2SimulationRanges), "Transpiler", null, null);
            // Check both original bodies before installing either patch.
            foreach (MethodInfo target in targets)
                new List<CodeInstruction>(Transpiler(target, PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null)));
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo target in targets)
                {
                    attempted.Add(target);
                    harmony.Patch(target, null, null, new HarmonyMethod(patch), null);
                }
                _installed = true;
                Logging.Info("81 Tiles electricity/water node ranges follow active capacity; 256-frame phases preserved.");
            }
            catch
            {
                foreach (MethodInfo target in attempted) harmony.Unpatch(target, patch);
                throw;
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            if (original == null || original.Name != "SimulationStepImpl" || original.DeclaringType == null
                || !original.IsStatic || (original.DeclaringType.FullName != "EightyOne2.Patches.ExpandedElectricityManager"
                && original.DeclaringType.FullName != "EightyOne2.Patches.ExpandedWaterManager"))
                throw new InvalidOperationException("Unexpected 81 Tiles simulation target.");
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int phase = -1, phases = 0;
            for (int i = 0; i + 8 < list.Count; ++i)
            {
                if (!Constant(list[i], 255) || list[i + 1].opcode != OpCodes.And
                    || Local(list[i + 2], false) < 0 || Local(list[i + 3], true) != Local(list[i + 2], false)
                    || !Constant(list[i + 4], 128) || list[i + 5].opcode != OpCodes.Clt
                    || Local(list[i + 6], false) < 0 || Local(list[i + 7], true) != Local(list[i + 6], false)
                    || (list[i + 8].opcode != OpCodes.Brfalse && list[i + 8].opcode != OpCodes.Brfalse_S)) continue;
                phase = Local(list[i + 2], false); ++phases;
            }
            int range = -1, ranges = 0, capacities = 0;
            for (int i = 0; i < list.Count; ++i)
            {
                if (!Constant(list[i], Capacity.VanillaNodeCapacity)) continue;
                ++capacities;
                if (i < 1 || i + 14 >= list.Count || Local(list[i - 1], true) != phase
                    || list[i + 1].opcode != OpCodes.Mul || !Constant(list[i + 2], 7) || list[i + 3].opcode != OpCodes.Shr
                    || Local(list[i + 4], false) < 0 || Local(list[i + 5], true) != phase
                    || !Constant(list[i + 6], 1) || list[i + 7].opcode != OpCodes.Add
                    || !Constant(list[i + 8], Capacity.VanillaNodeCapacity) || list[i + 9].opcode != OpCodes.Mul
                    || !Constant(list[i + 10], 7) || list[i + 11].opcode != OpCodes.Shr
                    || !Constant(list[i + 12], 1) || list[i + 13].opcode != OpCodes.Sub
                    || Local(list[i + 14], false) < 0
                    || Local(list[i + 4], false) == Local(list[i + 14], false)) continue;
                range = i; ++ranges;
            }
            bool nodes = false, state = false, update = false;
            bool electricity = original.DeclaringType.Name == "ExpandedElectricityManager";
            foreach (CodeInstruction instruction in list)
            {
                FieldInfo field = instruction.operand as FieldInfo;
                if (instruction.opcode == OpCodes.Ldfld && field != null)
                {
                    nodes |= field.DeclaringType == typeof(NetManager) && field.Name == "m_nodes";
                    state |= field.DeclaringType == (electricity ? typeof(ElectricityManager) : typeof(WaterManager))
                        && field.Name == (electricity ? "m_nodeGroups" : "m_nodeData");
                }
                MethodInfo method = instruction.operand as MethodInfo;
                update |= instruction.opcode == OpCodes.Call && method != null && method.DeclaringType == original.DeclaringType
                    && method.Name == (electricity ? "UpdateNodeElectricity" : "UpdateNodeWater");
            }
            if (phases != 1 || ranges != 1 || capacities != 2 || !nodes || !state || !update)
                throw new InvalidOperationException("81 Tiles node simulation range pattern changed: " + original.DeclaringType.FullName);
            MethodInfo helper = AccessTools.Method(typeof(EightyOne2SimulationRanges), "NodeCount", null, null);
            if (helper == null) throw new InvalidOperationException("81 Tiles node count helper missing.");
            // Change only the two multipliers, preserving labels, EH boundaries,
            // grid slices, pulse queues, the propagation half and all node work.
            list[range].opcode = list[range + 8].opcode = OpCodes.Call;
            list[range].operand = list[range + 8].operand = helper;
            return list;
        }

        private static bool Constant(CodeInstruction instruction, int value)
        {
            if (value == 7 && instruction.opcode == OpCodes.Ldc_I4_7) return true;
            return NetworkSimulationSlicePatch.Constant(instruction, value);
        }
        private static int Local(CodeInstruction instruction, bool load) { return NetworkSimulationSlicePatch.Local(instruction, load); }
    }
}
