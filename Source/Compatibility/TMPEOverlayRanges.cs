using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    // Display caches are append-only, not indexed by network ID. Widen both
    // enumeration and capacity; changing only the loops could overflow Add().
    internal static class TMPEOverlayRanges
    {
        internal enum Shape { Loop, InclusiveRefresh, CacheConstructor }
        internal sealed class Target
        {
            internal readonly string Type, Method, Anchor;
            internal readonly int Parameters, Limit;
            internal readonly Shape Pattern;
            internal Target(string type, string method, int parameters, int limit, string anchor, Shape pattern)
            { Type = type; Method = method; Parameters = parameters; Limit = limit; Anchor = anchor; Pattern = pattern; }
        }
        private const string UI = "TrafficManager.UI.SubTools.";
        internal static readonly Target[] Targets = {
            new Target(UI + "PrioritySigns.PrioritySignsTool", "RefreshCurrentPriorityNodeIds", 0, 32768, "currentPriorityNodeIds", Shape.Loop),
            new Target(UI + "ToggleTrafficLightsTool", "FilterVisibleNodes", 1, 32768, "get_CachedVisibleNodeIds", Shape.Loop),
            new Target(UI + "LaneConnectorTool", "ShowOverlay", 2, 32768, "get_CachedVisibleNodeIds", Shape.Loop),
            new Target(UI + "ParkingRestrictionsTool", "ShowSigns", 1, 36864, "get_CachedVisibleSegmentIds", Shape.Loop),
            new Target(UI + "SpeedLimits.Overlay.SpeedLimitsOverlay", "ShowSigns_RefreshVisibleSegmentsCache", 3, 36864, "cachedVisibleSegmentIds_", Shape.Loop),
            new Target(UI + "JunctionRestrictionsTool", "RefreshCurrentRestrictedNodeIds", 1, 32767, "currentRestrictedNodeIds", Shape.InclusiveRefresh),
            new Target(UI + "LaneConnectorTool", "RefreshCurrentNodeMarkers", 1, 32767, "currentLaneEnds_", Shape.InclusiveRefresh),
            new Target(UI + "VehicleRestrictionsTool", "RefreshCurrentRestrictedSegmentIds", 1, 36863, "currentRestrictedSegmentIds", Shape.InclusiveRefresh),
            new Target(UI + "TTL.TimedTrafficLightsTool", "RefreshCurrentTimedNodeIds", 1, 32767, "currentTimedNodeIds", Shape.InclusiveRefresh),
            new Target(UI + "ToggleTrafficLightsTool", ".ctor", 1, 32768, "<CachedVisibleNodeIds>k__BackingField", Shape.CacheConstructor),
            new Target(UI + "LaneConnectorTool", ".ctor", 1, 32768, "<CachedVisibleNodeIds>k__BackingField", Shape.CacheConstructor),
            new Target(UI + "ParkingRestrictionsTool", ".ctor", 1, 36864, "<CachedVisibleSegmentIds>k__BackingField", Shape.CacheConstructor),
            new Target(UI + "SpeedLimits.Overlay.SpeedLimitsOverlay", ".ctor", 1, 36864, "cachedVisibleSegmentIds_", Shape.CacheConstructor)
        };
        private static bool installed;
        internal static void Reset() { installed = false; }
        // Tools can be created while the network manager still has vanilla
        // arrays. Provision the final supported capacity once at construction.
        internal static int CacheCapacity() { return Capacity.ExpandedNodeCapacity; }
        internal static int NodeMaximum() { return TMPENetworkArrayPatches.NodeCount() - 1; }
        internal static int SegmentMaximum() { return TMPENetworkArrayPatches.SegmentCount() - 1; }

        internal static MethodBase Resolve(Assembly assembly, Target target)
        {
            Type type = assembly.GetType(target.Type, false);
            if (type == null) throw new InvalidOperationException("TM:PE overlay type missing: " + target.Type);
            MethodBase match = null;
            List<MethodBase> candidates = new List<MethodBase>();
            if (target.Pattern == Shape.CacheConstructor)
                candidates.AddRange(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
            else
                candidates.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            foreach (MethodBase method in candidates)
            {
                if (method.Name != target.Method || method.GetParameters().Length != target.Parameters) continue;
                if (target.Pattern == Shape.InclusiveRefresh && method.GetParameters()[0].ParameterType != typeof(ushort)) continue;
                if (match != null) throw new InvalidOperationException("Ambiguous TM:PE overlay method: " + target.Type + "." + target.Method);
                match = method;
            }
            if (match == null) throw new InvalidOperationException("TM:PE overlay method missing: " + target.Type + "." + target.Method);
            return match;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (installed || harmony == null) return;
            List<MethodBase> methods = new List<MethodBase>();
            foreach (Target target in Targets)
            {
                MethodBase method = Resolve(assembly, target);
                ApplyTarget(method, target, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
                methods.Add(method);
            }
            MethodInfo patch = AccessTools.Method(typeof(TMPEOverlayRanges), "Transpiler", null, null);
            List<MethodBase> attempted = new List<MethodBase>();
            try
            {
                foreach (MethodBase method in methods)
                {
                    attempted.Add(method);
                    harmony.Patch(method, null, null, new HarmonyMethod(patch), null);
                }
                installed = true;
                Logging.Info("TM:PE overlay enumeration and cache patterns patched: " + methods.Count + " methods.");
            }
            catch
            {
                foreach (MethodBase method in attempted) harmony.Unpatch(method, patch);
                throw;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            foreach (Target target in Targets)
                if (target.Type == original.DeclaringType.FullName && target.Method == original.Name
                    && target.Parameters == original.GetParameters().Length)
                    return ApplyTarget(original, target, instructions);
            throw new InvalidOperationException("Unexpected TM:PE overlay target.");
        }

        internal static List<CodeInstruction> ApplyTarget(MethodBase original, Target target, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int constant = -1, matches = 0;
            bool anchor = false;
            for (int i = 0; i < code.Count; ++i)
            {
                MemberInfo member = code[i].operand as MemberInfo;
                if (member != null && member.Name == target.Anchor) anchor = true;
                if (NetworkSimulationSlicePatch.Constant(code[i], target.Limit)) { constant = i; ++matches; }
            }
            bool valid = anchor && matches == 1;
            if (valid && target.Pattern == Shape.CacheConstructor)
            {
                valid = constant + 2 < code.Count && code[constant + 1].opcode == OpCodes.Newobj && code[constant + 2].opcode == OpCodes.Stfld;
                ConstructorInfo ctor = valid ? code[constant + 1].operand as ConstructorInfo : null;
                FieldInfo field = valid ? code[constant + 2].operand as FieldInfo : null;
                valid = ctor != null && field != null && field.Name == target.Anchor && field.DeclaringType == original.DeclaringType
                    && field.FieldType == ctor.DeclaringType && ctor.DeclaringType.IsGenericType
                    && ctor.DeclaringType.GetGenericTypeDefinition().FullName == "TrafficManager.Util.Caching.GenericArrayCache`1"
                    && ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType == typeof(int);
            }
            else if (valid && target.Pattern == Shape.Loop)
            {
                valid = constant > 5 && constant + 1 < code.Count && IsLess(code[constant + 1].opcode);
                int local = valid ? NetworkSimulationSlicePatch.Local(code[constant - 1], true) : -1;
                valid = valid && local >= 0 && NetworkSimulationSlicePatch.Local(code[constant - 2], false) == local;
                int add = constant - 3;
                if (valid && code[add].opcode == OpCodes.Conv_U2) --add;
                valid = valid && code[add].opcode == OpCodes.Add && NetworkSimulationSlicePatch.Constant(code[add - 1], 1)
                    && NetworkSimulationSlicePatch.Local(code[add - 2], true) == local;
            }
            else if (valid)
            {
                // Preserve the nonzero argument path: only replace the inclusive
                // upper bound selected when argument 1 is zero (full refresh).
                int comparison = constant + 1;
                if (comparison < code.Count && code[comparison].opcode == OpCodes.Conv_I8) ++comparison;
                valid = constant >= 3 && comparison < code.Count && IsLessEqual(code[comparison].opcode)
                    && IsBranch(code[constant - 1].opcode) && code[constant - 2].opcode == OpCodes.Ldarg_1
                    && IsFalseBranch(code[constant - 3].opcode)
                    && code[constant].labels.Contains((Label)code[constant - 3].operand)
                    && code[constant + 1].labels.Contains((Label)code[constant - 1].operand)
                    && original.GetParameters().Length == 1 && original.GetParameters()[0].ParameterType == typeof(ushort);
                int counter = constant - 4;
                if (valid && code[counter].opcode == OpCodes.Ldarg_1) --counter;
                if (valid && code[counter].opcode == OpCodes.Conv_U8) --counter;
                valid = valid && counter >= 4;
                int local = valid ? NetworkSimulationSlicePatch.Local(code[counter], true) : -1;
                valid = valid && local >= 0 && NetworkSimulationSlicePatch.Local(code[counter - 1], false) == local;
                int add = counter - 2;
                if (valid && code[add].opcode == OpCodes.Conv_U2) --add;
                valid = valid && code[add].opcode == OpCodes.Add && NetworkSimulationSlicePatch.Constant(code[add - 1], 1)
                    && NetworkSimulationSlicePatch.Local(code[add - 2], true) == local;
            }
            if (!valid) throw new InvalidOperationException("TM:PE overlay pattern changed: " + original.DeclaringType.FullName + "." + original.Name);
            string name = target.Pattern == Shape.CacheConstructor ? "CacheCapacity" :
                target.Pattern == Shape.InclusiveRefresh ? (target.Limit == 32767 ? "NodeMaximum" : "SegmentMaximum") :
                (target.Limit == 32768 ? "NodeCount" : "SegmentCount");
            Type helper = target.Pattern == Shape.Loop ? typeof(TMPENetworkArrayPatches) : typeof(TMPEOverlayRanges);
            MethodInfo getter = AccessTools.Method(helper, name, null, null);
            code[constant].opcode = OpCodes.Call; code[constant].operand = getter;
            return code;
        }
        private static bool IsLess(OpCode op) { return op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S; }
        private static bool IsLessEqual(OpCode op) { return op == OpCodes.Ble || op == OpCodes.Ble_S || op == OpCodes.Ble_Un || op == OpCodes.Ble_Un_S; }
        private static bool IsBranch(OpCode op) { return op == OpCodes.Br || op == OpCodes.Br_S; }
        private static bool IsFalseBranch(OpCode op) { return op == OpCodes.Brfalse || op == OpCodes.Brfalse_S; }
    }
}
