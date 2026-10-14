using System;
using System.Reflection;

namespace MoreNetworkCapacity.Compatibility
{
    // Setup/load-time postconditions only. Shared optional-mod expansion helpers
    // log failures; TM:PE must not continue with expanded offsets and small arrays.
    internal static class TMPEBufferValidation
    {
        internal sealed class Target
        {
            internal readonly string Type, Member;
            internal readonly int Length;
            internal readonly bool Static;
            internal Target(string type, string member, int length, bool isStatic)
            { Type = type; Member = member; Length = length; Static = isStatic; }
        }
        private const string Manager = "TrafficManager.Manager.Impl.";
        internal static readonly Target[] Targets = {
            new Target(Manager + "ExtNodeManager", "ExtNodes", Capacity.ExpandedNodeCapacity, false),
            new Target(Manager + "ExtSegmentManager", "ExtSegments", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "ExtSegmentEndManager", "ExtSegmentEnds", Capacity.ExpandedSegmentCapacity * 2, false),
            new Target(Manager + "TurnOnRedManager", "TurnOnRedSegments", Capacity.ExpandedSegmentCapacity * 2, false),
            new Target(Manager + "SegmentEndManager", "SegmentEnds", Capacity.ExpandedSegmentCapacity * 2, false),
            new Target(Manager + "TrafficMeasurementManager", "LaneTrafficData", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "TrafficMeasurementManager", "SegmentDirTrafficData", Capacity.ExpandedSegmentCapacity * 2, false),
            new Target(Manager + "JunctionRestrictionsManager", "segmentRestrictions", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "JunctionRestrictionsManager", "invalidSegmentRestrictions", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "TrafficPriorityManager", "PrioritySegments", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "TrafficPriorityManager", "invalidPrioritySegments", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "CustomSegmentLightsManager", "customSegments_", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "VehicleRestrictionsManager", "defaultVehicleTypeCache", Capacity.ExpandedSegmentCapacity, false),
            new Target("TrafficManager.State.Flags", "laneAllowedVehicleTypesArray", Capacity.ExpandedSegmentCapacity, true),
            new Target(Manager + "TrafficLightSimulationManager", "TrafficLightSimulations", Capacity.ExpandedNodeCapacity, false),
            new Target(Manager + "ParkingRestrictionsManager", "parkingAllowed", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "RoutingManager", "updatedSegmentBuckets", Capacity.ExpandedSegmentCapacity >> 6, false),
            new Target(Manager + "RoutingManager", "SegmentRoutings", Capacity.ExpandedSegmentCapacity, false),
            new Target(Manager + "GeometryManager", "updatedSegmentBuckets", Capacity.ExpandedSegmentCapacity >> 6, false),
            new Target(Manager + "GeometryManager", "updatedNodeBuckets", Capacity.ExpandedNodeCapacity >> 6, false)
        };

        internal static FieldInfo Resolve(Assembly assembly, Target target)
        {
            Type type = assembly.GetType(target.Type, false);
            FieldInfo field = ArrayCompatibility.FindField(type, target.Member)
                ?? ArrayCompatibility.FindBackingField(type, target.Member);
            if (field == null || !field.FieldType.IsArray || field.FieldType.GetArrayRank() != 1
                || field.IsStatic != target.Static)
                throw new InvalidOperationException("Required TM:PE buffer member changed: "
                    + target.Type + "." + target.Member);
            return field;
        }

        internal static void Validate(Assembly assembly)
        {
            foreach (Target target in Targets)
            {
                FieldInfo field = Resolve(assembly, target);
                object instance = target.Static ? null : ArrayCompatibility.GetStaticInstance(
                    field.DeclaringType, "Instance", "Instance", target.Type);
                if (!target.Static && instance == null)
                    throw new InvalidOperationException("Required TM:PE manager unavailable: " + target.Type);
                Array array = field.GetValue(instance) as Array;
                if (array == null || array.Length != target.Length)
                    throw new InvalidOperationException("Required TM:PE buffer preparation failed: "
                        + target.Type + "." + target.Member + " length="
                        + (array == null ? "null" : array.Length.ToString())
                        + ", expected=" + target.Length);
            }
        }

        internal static void ValidateIndexPatches(bool turnOnRed, bool segmentEnd, bool segmentEndDecode, bool traffic)
        {
            if (!turnOnRed || !segmentEnd || !segmentEndDecode || !traffic)
                throw new InvalidOperationException("Required TM:PE split-index patches are incomplete: "
                    + "turnOnRed=" + turnOnRed + ", segmentEnd=" + segmentEnd
                    + ", segmentEndDecode=" + segmentEndDecode + ", traffic=" + traffic);
        }

        internal static void ValidateRuntimeReferences(Assembly assembly, Array nodes, Array segments, Array lanes)
        {
            ValidateReference(assembly, "NetNodeExtensions", "_nodeBuffer", nodes);
            ValidateReference(assembly, "NetSegmentExtensions", "_segBuffer", segments);
            ValidateReference(assembly, "NetLaneExtensions", "_laneBuffer", lanes);
        }

        internal static void ValidateReference(Assembly assembly, string typeName, string fieldName, Array expected)
        {
            Type type = assembly.GetType("TrafficManager.Util.Extensions." + typeName, false);
            FieldInfo field = ArrayCompatibility.FindField(type, fieldName);
            if (field == null || !field.IsStatic || expected == null
                || !field.FieldType.IsInstanceOfType(expected)
                || !Object.ReferenceEquals(field.GetValue(null), expected))
                throw new InvalidOperationException("Required TM:PE network buffer reference is stale or missing: "
                    + typeName + "." + fieldName);
        }
    }
}
