using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class TMPETrafficLightRanges
    {
        private static bool _installed;
        internal static void Reset() { _installed = false; }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (_installed || harmony == null) return;
            Type type = assembly.GetType("TrafficManager.Manager.Impl.TrafficLightSimulationManager", false);
            if (type == null) throw new InvalidOperationException("TM:PE traffic-light manager type missing.");
            MethodInfo getter = type.GetMethod("get_TrafficLightSimulations");
            if (getter == null || !getter.ReturnType.IsArray)
                throw new InvalidOperationException("TM:PE traffic-light simulation array getter missing.");
            MethodInfo[] targets = {
                AccessTools.Method(type, "SimulationStep", Type.EmptyTypes, null),
                AccessTools.Method(type, "SaveData", new Type[] { typeof(bool).MakeByRefType() }, null),
                AccessTools.Method(type, "OnLevelUnloading", Type.EmptyTypes, null)
            };
            MethodInfo patch = AccessTools.Method(typeof(TMPETrafficLightRanges), "Transpiler", null, null);
            // Validate original bodies for all three methods before installing.
            foreach (MethodInfo target in targets)
            {
                if (target == null || target.DeclaringType != type)
                    throw new InvalidOperationException("TM:PE traffic-light range target missing.");
                new List<CodeInstruction>(Transpiler(target, PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null)));
            }
            List<MethodInfo> installed = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo target in targets)
                {
                    installed.Add(target);
                    harmony.Patch(target, null, null, new HarmonyMethod(patch), null);
                }
                _installed = true;
                Logging.Info("TM:PE traffic-light simulation/save/unload ranges follow actual array length.");
            }
            catch
            {
                foreach (MethodInfo target in installed) harmony.Unpatch(target, patch);
                throw;
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            MethodInfo getter = original.DeclaringType.GetMethod("get_TrafficLightSimulations");
            if (getter == null || getter.IsStatic || !getter.ReturnType.IsArray)
                throw new InvalidOperationException("TM:PE simulation array getter changed.");
            List<int> matches = new List<int>();
            if (original.Name == "SimulationStep")
            {
                for (int i = 4; i + 9 < list.Count; ++i)
                {
                    if (!NetworkSimulationSlicePatch.Constant(list[i], 512)) continue;
                    if (!NetworkSimulationSlicePatch.Constant(list[i - 3], 63)
                        || list[i - 2].opcode != OpCodes.And || list[i - 1].opcode != OpCodes.Dup
                        || list[i + 1].opcode != OpCodes.Mul
                        || !NetworkSimulationSlicePatch.Constant(list[i + 3], 1)
                        || list[i + 4].opcode != OpCodes.Add
                        || !NetworkSimulationSlicePatch.Constant(list[i + 5], 512)
                        || list[i + 6].opcode != OpCodes.Mul
                        || !NetworkSimulationSlicePatch.Constant(list[i + 7], 1)
                        || list[i + 8].opcode != OpCodes.Sub) continue;
                    matches.Add(i); matches.Add(i + 5);
                }
                if (matches.Count != 2 || Count(list, 512) != 2)
                    throw new InvalidOperationException("TM:PE 64-frame simulation range pattern changed.");
            }
            else if (original.Name == "SaveData" || original.Name == "OnLevelUnloading")
            {
                bool usesArray = false;
                foreach (CodeInstruction instruction in list)
                    if (getter.Equals(instruction.operand)) usesArray = true;
                for (int i = 1; i + 1 < list.Count; ++i)
                    if (NetworkSimulationSlicePatch.Constant(list[i], 32768)
                        && list[i - 1].opcode.Name.StartsWith("ldloc", StringComparison.Ordinal)
                        && (list[i + 1].opcode == OpCodes.Blt_Un || list[i + 1].opcode == OpCodes.Blt_Un_S))
                        matches.Add(i);
                if (!usesArray || matches.Count != 1 || Count(list, 32768) != 1)
                    throw new InvalidOperationException("TM:PE array traversal pattern changed: " + original.Name);
            }
            else throw new InvalidOperationException("Unexpected TM:PE range target.");

            for (int m = matches.Count - 1; m >= 0; --m)
            {
                int index = matches[m];
                // Keep incoming labels and exception boundaries on the original instruction.
                list[index].opcode = OpCodes.Ldarg_0; list[index].operand = null;
                List<CodeInstruction> tail = new List<CodeInstruction> {
                    new CodeInstruction(OpCodes.Call, getter), new CodeInstruction(OpCodes.Ldlen, null),
                    new CodeInstruction(OpCodes.Conv_I4, null)
                };
                if (original.Name == "SimulationStep")
                {
                    tail.Add(new CodeInstruction(OpCodes.Ldc_I4_6, null));
                    tail.Add(new CodeInstruction(OpCodes.Shr, null));
                }
                list.InsertRange(index + 1, tail);
            }
            return list;
        }

        private static int Count(List<CodeInstruction> list, int value)
        {
            int count = 0;
            foreach (CodeInstruction instruction in list)
                if (NetworkSimulationSlicePatch.Constant(instruction, value)) ++count;
            return count;
        }
    }
}
