using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    // Asset export enumerates segment IDs. Grid searches instead count links
    // to detect corrupt chains: widen their cap without removing the guard.
    internal static class TMPEAdditionalRanges
    {
        internal sealed class Target
        {
            internal readonly string Type, Method, Anchor;
            internal readonly int Parameters;
            internal readonly bool Guard;
            internal Target(string type, string method, int parameters, string anchor, bool guard)
            { Type = type; Method = method; Parameters = parameters; Anchor = anchor; Guard = guard; }
        }
        internal static readonly Target[] Targets = {
            new Target("TrafficManager.State.Asset.AssetData", "RecordAll", 0, "AddCompleteSegment", false),
            new Target("TrafficManager.State.Asset.AssetData", "GetPathsNetworkIDs", 1, "ToSegment", false),
            new Target("TrafficManager.Manager.Impl.AdvancedParkingManager", "<FindParkingSpaceAtRoadSide>g__LoopHandler|32_0", 3, "m_nextGridSegment", true),
            new Target("TrafficManager.Manager.Impl.ExtPathManager", "<FindPathPositionWithSpiralLoop>g__FindHelper|12_0", 3, "m_nextGridSegment", true)
        };
        private static bool installed;
        internal static void Reset() { installed = false; }
        internal static MethodInfo Resolve(Assembly assembly, Target target)
        {
            Type type = assembly.GetType(target.Type, false);
            if (type == null) throw new InvalidOperationException("TM:PE additional type missing: " + target.Type);
            MethodInfo result = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != target.Method || method.GetParameters().Length != target.Parameters) continue;
                if (result != null) throw new InvalidOperationException("Ambiguous TM:PE additional method: " + target.Type + "." + target.Method);
                result = method;
            }
            if (result == null) throw new InvalidOperationException("TM:PE additional method missing: " + target.Type + "." + target.Method);
            return result;
        }
        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (installed || harmony == null) return;
            List<MethodInfo> methods = new List<MethodInfo>();
            foreach (Target target in Targets)
            {
                MethodInfo method = Resolve(assembly, target);
                ApplyTarget(method, target, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
                methods.Add(method);
            }
            MethodInfo patch = AccessTools.Method(typeof(TMPEAdditionalRanges), "Transpiler", null, null);
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo method in methods)
                {
                    attempted.Add(method);
                    harmony.Patch(method, null, null, new HarmonyMethod(patch), null);
                }
                installed = true;
                Logging.Info("TM:PE asset enumeration and grid search guards patched: " + methods.Count + " methods.");
            }
            catch
            {
                foreach (MethodInfo method in attempted) harmony.Unpatch(method, patch);
                throw;
            }
        }
        private static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            foreach (Target target in Targets)
                if (target.Type == original.DeclaringType.FullName && target.Method == original.Name
                    && target.Parameters == original.GetParameters().Length)
                    return ApplyTarget(original, target, instructions);
            throw new InvalidOperationException("Unexpected TM:PE additional range target.");
        }
        internal static List<CodeInstruction> ApplyTarget(MethodBase original, Target target, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int constant = -1, matches = 0;
            bool anchor = false, grid = false, corruptionLog = false;
            foreach (CodeInstruction instruction in code)
            {
                MemberInfo member = instruction.operand as MemberInfo;
                if (member != null && member.Name == target.Anchor) anchor = true;
                if (member != null && member.Name == "m_segmentGrid") grid = true;
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string
                    && ((string)instruction.operand).StartsWith("Invalid list detected!", StringComparison.Ordinal)) corruptionLog = true;
            }
            for (int i = 0; i < code.Count; ++i)
                if (NetworkSimulationSlicePatch.Constant(code[i], 36864)) { constant = i; ++matches; }
            bool valid = anchor && matches == 1 && constant >= 6 && constant + 1 < code.Count && IsLess(code[constant + 1].opcode);
            if (valid && target.Guard)
            {
                int local = NetworkSimulationSlicePatch.Local(code[constant - 1], false);
                valid = grid && corruptionLog && local >= 0 && code[constant - 2].opcode == OpCodes.Dup
                    && code[constant - 3].opcode == OpCodes.Add && NetworkSimulationSlicePatch.Constant(code[constant - 4], 1)
                    && NetworkSimulationSlicePatch.Local(code[constant - 5], true) == local;
                FieldInfo next = null;
                for (int i = Math.Max(0, constant - 10); i < constant; ++i)
                    if (code[i].opcode == OpCodes.Ldfld && code[i].operand is FieldInfo
                        && ((FieldInfo)code[i].operand).Name == target.Anchor) next = (FieldInfo)code[i].operand;
                valid = valid && next != null && next.FieldType == typeof(ushort);
            }
            else if (valid)
            {
                int local = NetworkSimulationSlicePatch.Local(code[constant - 1], true);
                valid = local >= 0 && NetworkSimulationSlicePatch.Local(code[constant - 2], false) == local;
                int add = constant - 3;
                if (valid && code[add].opcode == OpCodes.Conv_U2) --add;
                valid = valid && code[add].opcode == OpCodes.Add && NetworkSimulationSlicePatch.Constant(code[add - 1], 1)
                    && NetworkSimulationSlicePatch.Local(code[add - 2], true) == local;
            }
            if (!valid) throw new InvalidOperationException("TM:PE additional range pattern changed: " + original.DeclaringType.FullName + "." + original.Name);
            code[constant].opcode = OpCodes.Call;
            code[constant].operand = AccessTools.Method(typeof(TMPENetworkArrayPatches), "SegmentCount", null, null);
            return code;
        }
        private static bool IsLess(OpCode op) { return op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S; }
    }
}
