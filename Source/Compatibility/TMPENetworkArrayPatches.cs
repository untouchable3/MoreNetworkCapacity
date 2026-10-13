using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    // A reviewed list of network-array allocations and full-pool traversals.
    // Do not replace TM:PE constants globally: many are vehicle/flag values.
    internal static class TMPENetworkArrayPatches
    {
        internal sealed class Target
        {
            internal readonly string Type, Method, Anchor;
            internal readonly int Parameters, Limit;
            internal readonly bool Allocation;
            internal Target(string type, string method, int parameters, int limit, string anchor, bool allocation)
            { Type = type; Method = method; Parameters = parameters; Limit = limit; Anchor = anchor; Allocation = allocation; }
        }
        private const string Manager = "TrafficManager.Manager.Impl.";
        internal static readonly Target[] Targets = {
            new Target(Manager + "ParkingRestrictionsManager", "OnBeforeLoadData", 0, 36864, "parkingAllowed", true),
            new Target(Manager + "VehicleRestrictionsManager", "GetDefaultAllowedVehicleTypes", 5, 36864, "defaultVehicleTypeCache", true),
            new Target(Manager + "CustomSegmentLightsManager", "OnLevelUnloading", 0, 36864, "customSegments_", true),
            new Target(Manager + "TrafficPriorityManager", "SaveData", 1, 36864, "GetPrioritySign", false),
            new Target(Manager + "JunctionRestrictionsManager", "SaveData", 1, 36864, "ToSegment", false),
            new Target("TrafficManager.State.Flags", "GetAllLaneAllowedVehicleTypes", 0, 36864, "laneAllowedVehicleTypesArray", false),
            new Target(Manager + "JunctionRestrictionsManager", "OnLevelLoading", 0, 36864, "get_ExtSegments", false),
            new Target(Manager + "JunctionRestrictionsManager", "UpdateAllDefaults", 0, 36864, "get_ExtSegments", false),
            new Target(Manager + "JunctionRestrictionsManager", "RemoveJunctionRestrictionsIfNecessary", 0, 32768, "RemoveJunctionRestrictionsIfNecessary", false),
            new Target(Manager + "RoutingManager", "RequestFullRecalculation", 0, 36864, "updatedSegmentBuckets", false),
            new Target(Manager + "RoutingManager", "RecalculateAll", 0, 36864, "RecalculateSegment", false),
            new Target(Manager + "GeometryManager", "MarkAllAsUpdated", 0, 36864, "MarkAsUpdated", false),
            new Target(Manager + "TurnOnRedManager", "OnBeforeLoadData", 0, 36864, "get_ExtSegments", false),
            new Target(Manager + "TrafficMeasurementManager", "ResetTrafficStats", 0, 36864, "DestroySegmentStats", false),
            new Target(Manager + "SpeedLimitManager", "UpdateNetinfo", 1, 36864, "CacheSegmentSpeeds", false),
            new Target(Manager + "LaneArrowManager+<>c__DisplayClass17_0", "<UpdateDedicatedTurningLanePolicy>b__0", 0, 36864, "ToSegment", false)
        };
        private static bool _installed;
        internal static void Reset() { _installed = false; }

        internal static int SegmentCount()
        { return Singleton<NetManager>.exists ? Supported(Capacity.GetSegmentBufferLength(), 36864) : 36864; }
        internal static int NodeCount()
        { return Singleton<NetManager>.exists ? Supported(Capacity.GetNodeBufferLength(), 32768) : 32768; }
        private static int Supported(int length, int vanilla)
        {
            if (length != vanilla && length != Capacity.ExpandedSegmentCapacity)
                throw new InvalidOperationException("Unsupported network buffer length for TM:PE: " + length);
            return length;
        }

        internal static MethodInfo Resolve(Assembly assembly, Target target)
        {
            Type type = assembly.GetType(target.Type, false);
            if (type == null) throw new InvalidOperationException("TM:PE array target type missing: " + target.Type);
            MethodInfo result = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // PriorityManager implements SaveData explicitly in the current DLL.
                if ((method.Name != target.Method && !method.Name.EndsWith("." + target.Method, StringComparison.Ordinal))
                    || method.GetParameters().Length != target.Parameters) continue;
                if (target.Type == Manager + "TrafficPriorityManager"
                    && (!method.ReturnType.IsGenericType
                        || method.ReturnType.GetGenericTypeDefinition() != typeof(List<>)
                        || method.ReturnType.GetGenericArguments()[0].Name != "PrioritySegment")) continue;
                if (result != null) throw new InvalidOperationException("Ambiguous TM:PE array target: " + target.Type + "." + target.Method);
                result = method;
            }
            if (result == null) throw new InvalidOperationException("TM:PE array method missing: " + target.Type + "." + target.Method);
            return result;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (_installed || harmony == null) return;
            List<MethodInfo> methods = new List<MethodInfo>();
            foreach (Target target in Targets)
            {
                MethodInfo method = Resolve(assembly, target);
                ApplyTarget(method, target, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
                methods.Add(method);
            }
            MethodInfo transpiler = AccessTools.Method(typeof(TMPENetworkArrayPatches), "Transpiler", null, null);
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo method in methods)
                {
                    attempted.Add(method);
                    harmony.Patch(method, null, null, new HarmonyMethod(transpiler), null);
                }
                _installed = true;
                Logging.Info("TM:PE network allocation/save/refresh patterns patched: " + methods.Count + " methods.");
            }
            catch
            {
                foreach (MethodInfo method in attempted) harmony.Unpatch(method, transpiler);
                throw;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            foreach (Target target in Targets)
                if (original.DeclaringType.FullName == target.Type
                    && (original.Name == target.Method || original.Name.EndsWith("." + target.Method, StringComparison.Ordinal))
                    && original.GetParameters().Length == target.Parameters)
                    return ApplyTarget(original, target, instructions);
            throw new InvalidOperationException("Unexpected TM:PE array patch target.");
        }

        internal static List<CodeInstruction> ApplyTarget(MethodBase original, Target target, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int found = -1, constants = 0;
            bool anchor = false;
            foreach (CodeInstruction instruction in list)
            {
                MemberInfo member = instruction.operand as MemberInfo;
                if (member != null && member.Name == target.Anchor) anchor = true;
            }
            for (int i = 0; i < list.Count; ++i)
            {
                if (!NetworkSimulationSlicePatch.Constant(list[i], target.Limit)) continue;
                ++constants;
                if (target.Allocation)
                {
                    if (i + 2 >= list.Count || list[i + 1].opcode != OpCodes.Newarr
                        || list[i + 2].opcode != OpCodes.Stfld) continue;
                    FieldInfo field = list[i + 2].operand as FieldInfo;
                    if (field == null || field.DeclaringType != original.DeclaringType || !field.FieldType.IsArray
                        || field.Name != target.Anchor || !field.FieldType.GetElementType().Equals(list[i + 1].operand)) continue;
                }
                else
                {
                    if (i < 5 || i + 1 >= list.Count || !IsLessThan(list[i + 1].opcode)) continue;
                    int local = NetworkSimulationSlicePatch.Local(list[i - 1], true);
                    if (local < 0 || NetworkSimulationSlicePatch.Local(list[i - 2], false) != local) continue;
                    int add = i - 3;
                    if (list[add].opcode == OpCodes.Conv_U2) --add;
                    if (list[add].opcode != OpCodes.Add || !NetworkSimulationSlicePatch.Constant(list[add - 1], 1)
                        || NetworkSimulationSlicePatch.Local(list[add - 2], true) != local) continue;
                }
                found = found < 0 ? i : -2;
            }
            if (!anchor || constants != 1 || found < 0)
                throw new InvalidOperationException("TM:PE array pattern changed: " + original.DeclaringType.FullName + "." + original.Name);
            MethodInfo size = AccessTools.Method(typeof(TMPENetworkArrayPatches),
                target.Limit == 32768 ? "NodeCount" : "SegmentCount", null, null);
            // Change in place only after all checks; preserve labels and EH blocks.
            list[found].opcode = OpCodes.Call; list[found].operand = size;
            return list;
        }

        private static bool IsLessThan(OpCode op)
        { return op == OpCodes.Blt || op == OpCodes.Blt_S || op == OpCodes.Blt_Un || op == OpCodes.Blt_Un_S; }
    }
}
