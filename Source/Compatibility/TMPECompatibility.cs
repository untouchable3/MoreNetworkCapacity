using System;
using System.Reflection;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class TMPECompatibility
    {
        private const string AssemblyName = "TrafficManager";
        private const string ModName = "TM:PE";

        private static bool _turnOnRedIndexPatched;
        private static bool _segmentEndIndexPatched;
        private static bool _segmentEndDecodePatched;
        private static bool _trafficMeasurementIndexPatched;
        private static bool _laneConnectionDataPrepared;

        internal static void Reset()
        {
            TMPETrafficLightRanges.Reset();
            TMPENetworkArrayPatches.Reset();
            _turnOnRedIndexPatched = false;
            _segmentEndIndexPatched = false;
            _segmentEndDecodePatched = false;
            _trafficMeasurementIndexPatched = false;
            _laneConnectionDataPrepared = false;
        }

        internal static void Apply(Harmony harmony)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "TM:PE is not loaded; compatibility is inactive.");
                return;
            }

            ExpandExtSegmentManager(assembly);
            ExpandExtNodeManager(assembly);
            ExpandExtSegmentEndManager(assembly);
            ExpandTurnOnRedManager(assembly);
            ExpandSegmentEndManager(assembly);
            ExpandTrafficMeasurementManager(assembly);
            ExpandJunctionRestrictionsManager(assembly);
            ExpandTrafficPriorityManager(assembly);
            ExpandCustomSegmentLightsManager(assembly);
            ExpandVehicleRestrictionsManager(assembly);
            ExpandFlags(assembly);
            ExpandTrafficLightSimulationManager(assembly);
            ExpandParkingRestrictionsManager(assembly);
            ExpandRoutingManager(assembly);
            ExpandGeometryManager(assembly);
            ApplyIndexPatches(harmony, assembly);
            TMPETrafficLightRanges.Apply(harmony, assembly);
            TMPENetworkArrayPatches.Apply(harmony, assembly);

            Logging.Info("TM:PE compatibility pass completed.");
        }

        internal static void ApplyEarly(Harmony harmony)
        {
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly(AssemblyName);
            if (assembly == null) return;
            // Install metadata-validated range patches before TM:PE's load
            // callbacks can recreate arrays. Do not initialize game managers here.
            TMPETrafficLightRanges.Apply(harmony, assembly);
            TMPENetworkArrayPatches.Apply(harmony, assembly);
        }

        internal static void PrepareForNetworkDeserialize(
            Harmony harmony,
            bool expandedSave)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "TM:PE is not loaded; early network-load preparation "
                    + "is inactive.");
                return;
            }

            Apply(harmony);
            if (expandedSave)
            {
                PrepareLaneConnectionData(assembly, true);
            }
        }

        private static void PrepareLaneConnectionData(
            Assembly assembly,
            bool force)
        {
            if (_laneConnectionDataPrepared && !force)
            {
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.LaneConnection."
                    + "LaneConnectionManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "LaneConnectionManager");
            if (instance == null)
            {
                return;
            }

            MethodInfo method = type.GetMethod(
                "OnBeforeLoadData",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
            {
                Logging.Warning(
                    "Could not find TM:PE LaneConnectionManager."
                    + "OnBeforeLoadData.");
                return;
            }

            if (force || !_laneConnectionDataPrepared)
            {
                try
                {
                    method.Invoke(instance, null);
                    _laneConnectionDataPrepared = true;
                    Logging.Info(
                        "Prepared TM:PE LaneConnectionManager before "
                        + "AdaptiveRoads network refresh.");
                }
                catch (Exception exception)
                {
                    Logging.Error(
                        "Failed to prepare TM:PE LaneConnectionManager: "
                        + exception);
                }
            }
        }

        private static void ExpandExtSegmentManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.ExtSegmentManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "ExtSegmentManager");
            FieldInfo field = FindMember(
                type,
                "ExtSegments");
            Type elementType = GetElementType(
                field,
                "TM:PE ExtSegments");
            ArrayCompatibility.ExpandDirect(
                field,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                CreateUshortConstructorFactory(elementType),
                false,
                "TM:PE ExtSegmentManager.ExtSegments");
        }

        private static void ExpandExtNodeManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.ExtNodeManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "ExtNodeManager");
            FieldInfo field = FindMember(
                type,
                "ExtNodes");
            Type elementType = GetElementType(
                field,
                "TM:PE ExtNodes");
            ArrayCompatibility.ExpandDirect(
                field,
                instance,
                Capacity.VanillaNodeCapacity,
                Capacity.ExpandedNodeCapacity,
                CreateUshortConstructorFactory(elementType),
                false,
                "TM:PE ExtNodeManager.ExtNodes");
        }

        private static void ExpandExtSegmentEndManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.ExtSegmentEndManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "ExtSegmentEndManager");
            FieldInfo field = FindMember(
                type,
                "ExtSegmentEnds");
            Type elementType = GetElementType(
                field,
                "TM:PE ExtSegmentEnds");
            ArrayCompatibility.ExpandDirect(
                field,
                instance,
                Capacity.VanillaSegmentCapacity * 2,
                Capacity.ExpandedSegmentCapacity * 2,
                CreateExtSegmentEndFactory(elementType),
                false,
                "TM:PE ExtSegmentEndManager.ExtSegmentEnds");
        }

        private static void ExpandTurnOnRedManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.TurnOnRedManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "TurnOnRedManager");
            FieldInfo field = FindMember(
                type,
                "TurnOnRedSegments");
            ArrayCompatibility.ExpandSplit(
                field,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                "TM:PE TurnOnRedManager.TurnOnRedSegments");
        }

        private static void ExpandSegmentEndManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.SegmentEndManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "SegmentEndManager");
            FieldInfo field = ThirdPartyCapacityCompatibility.FindField(
                type,
                "SegmentEnds");
            ArrayCompatibility.ExpandSplit(
                field,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                "TM:PE SegmentEndManager.SegmentEnds");
        }

        private static void ExpandTrafficMeasurementManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.TrafficMeasurementManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "TrafficMeasurementManager");

            FieldInfo laneField = FindMember(
                type,
                "LaneTrafficData");
            ArrayCompatibility.ExpandDirect(
                laneField,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE TrafficMeasurementManager.LaneTrafficData");

            FieldInfo directionField = FindMember(
                type,
                "SegmentDirTrafficData");
            Type elementType = GetElementType(
                directionField,
                "TM:PE SegmentDirTrafficData");
            ArrayCompatibility.ExpandSplit(
                directionField,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                CreateSegmentDirTrafficFactory(elementType),
                "TM:PE TrafficMeasurementManager.SegmentDirTrafficData");
        }

        private static void ExpandJunctionRestrictionsManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.JunctionRestrictionsManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "JunctionRestrictionsManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "segmentRestrictions"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE JunctionRestrictionsManager.segmentRestrictions");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "invalidSegmentRestrictions"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE JunctionRestrictionsManager.invalidSegmentRestrictions");
        }

        private static void ExpandTrafficPriorityManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.TrafficPriorityManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "TrafficPriorityManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "PrioritySegments"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE TrafficPriorityManager.PrioritySegments");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "invalidPrioritySegments"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE TrafficPriorityManager.invalidPrioritySegments");
        }

        private static void ExpandCustomSegmentLightsManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.CustomSegmentLightsManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "CustomSegmentLightsManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "customSegments_"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE CustomSegmentLightsManager.customSegments_");
        }

        private static void ExpandVehicleRestrictionsManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.VehicleRestrictionsManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "VehicleRestrictionsManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "defaultVehicleTypeCache"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                true,
                "TM:PE VehicleRestrictionsManager.defaultVehicleTypeCache");
        }

        private static void ExpandFlags(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.State.Flags",
                ModName);
            if (type == null)
            {
                return;
            }

            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "laneAllowedVehicleTypesArray"),
                null,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                true,
                "TM:PE Flags.laneAllowedVehicleTypesArray");
        }

        private static void ExpandTrafficLightSimulationManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.TrafficLightSimulationManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "TrafficLightSimulationManager");
            FieldInfo field = FindMember(
                type,
                "TrafficLightSimulations");
            Type elementType = GetElementType(
                field,
                "TM:PE TrafficLightSimulations");
            ArrayCompatibility.ExpandDirect(
                field,
                instance,
                Capacity.VanillaNodeCapacity,
                Capacity.ExpandedNodeCapacity,
                CreateUshortConstructorFactory(elementType),
                false,
                "TM:PE TrafficLightSimulationManager.TrafficLightSimulations");
        }

        private static void ExpandParkingRestrictionsManager(
            Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.ParkingRestrictionsManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "ParkingRestrictionsManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "parkingAllowed"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                CreateParkingEntry,
                false,
                "TM:PE ParkingRestrictionsManager.parkingAllowed");
        }

        private static void ExpandRoutingManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.RoutingManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "RoutingManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "updatedSegmentBuckets"),
                instance,
                576,
                1016,
                null,
                false,
                "TM:PE RoutingManager.updatedSegmentBuckets");
            ArrayCompatibility.ExpandDirect(
                FindMember(type, "SegmentRoutings"),
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "TM:PE RoutingManager.SegmentRoutings");
        }

        private static void ExpandGeometryManager(Assembly assembly)
        {
            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "TrafficManager.Manager.Impl.GeometryManager",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = GetInstance(
                type,
                "Instance",
                "GeometryManager");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "updatedSegmentBuckets"),
                instance,
                576,
                1016,
                null,
                false,
                "TM:PE GeometryManager.updatedSegmentBuckets");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "updatedNodeBuckets"),
                instance,
                512,
                1016,
                null,
                false,
                "TM:PE GeometryManager.updatedNodeBuckets");
        }

        private static void ApplyIndexPatches(
            Harmony harmony,
            Assembly assembly)
        {
            if (harmony == null)
            {
                return;
            }

            if (!_turnOnRedIndexPatched)
            {
                Type type = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "TrafficManager.Manager.Impl.TurnOnRedManager",
                    ModName);
                _turnOnRedIndexPatched = TryPatch(
                    harmony,
                    type,
                    "GetIndex",
                    new Type[] { typeof(ushort), typeof(bool) },
                    "TurnOnRedGetIndexPrefix",
                    "TM:PE TurnOnRedManager.GetIndex");
            }

            if (!_segmentEndIndexPatched)
            {
                Type type = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "TrafficManager.Manager.Impl.SegmentEndManager",
                    ModName);
                _segmentEndIndexPatched = TryPatch(
                    harmony,
                    type,
                    "GetIndex",
                    new Type[] { typeof(ushort), typeof(bool) },
                    "SegmentEndGetIndexPrefix",
                    "TM:PE SegmentEndManager.GetIndex");
            }

            if (!_segmentEndDecodePatched)
            {
                Type type = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "TrafficManager.Manager.Impl.SegmentEndManager",
                    ModName);
                _segmentEndDecodePatched = TryPatch(
                    harmony,
                    type,
                    "GetSegmentAndNodeFromIndex",
                    new Type[]
                    {
                        typeof(int),
                        typeof(ushort).MakeByRefType(),
                        typeof(bool).MakeByRefType()
                    },
                    "SegmentEndGetSegmentAndNodeFromIndexPrefix",
                    "TM:PE SegmentEndManager.GetSegmentAndNodeFromIndex");
            }

            if (!_trafficMeasurementIndexPatched)
            {
                Type type = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "TrafficManager.Manager.Impl.TrafficMeasurementManager",
                    ModName);
                _trafficMeasurementIndexPatched = TryPatch(
                    harmony,
                    type,
                    "GetDirIndex",
                    new Type[]
                    {
                        typeof(ushort),
                        typeof(NetInfo.Direction)
                    },
                    "TrafficMeasurementGetDirIndexPrefix",
                    "TM:PE TrafficMeasurementManager.GetDirIndex");
            }
        }

        private static bool TryPatch(
            Harmony harmony,
            Type type,
            string methodName,
            Type[] arguments,
            string prefixName,
            string label)
        {
            if (type == null)
            {
                return false;
            }

            try
            {
                MethodInfo target = type.GetMethod(
                    methodName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    arguments,
                    null);
                if (target == null)
                {
                    Logging.Warning(
                        "Could not find "
                        + label
                        + " for compatibility patching.");
                    return false;
                }

                MethodInfo prefix = typeof(TMPEIndexPatches).GetMethod(
                    prefixName,
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                if (prefix == null)
                {
                    Logging.Error(
                        "Could not find compatibility prefix "
                        + prefixName
                        + ".");
                    return false;
                }

                harmony.Patch(
                    target,
                    new HarmonyMethod(prefix),
                    null,
                    null,
                    null);
                Logging.Info("Patched " + label + " for expanded offsets.");
                return true;
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to patch "
                    + label
                    + ": "
                    + exception);
                return false;
            }
        }

        private static object GetInstance(
            Type type,
            string memberName,
            string label)
        {
            object instance = ArrayCompatibility.GetStaticInstance(
                type,
                memberName,
                memberName,
                "TM:PE " + label);
            if (instance == null)
            {
                Logging.Warning(
                    "Could not access TM:PE "
                    + label
                    + " instance.");
            }

            return instance;
        }

        private static FieldInfo FindMember(
            Type type,
            string name)
        {
            return ThirdPartyCapacityCompatibility.FindField(type, name)
                ?? ThirdPartyCapacityCompatibility.FindBackingField(
                    type,
                    name);
        }

        private static Type GetElementType(
            FieldInfo field,
            string label)
        {
            if (field == null)
            {
                return null;
            }

            Type elementType = field.FieldType.GetElementType();
            if (elementType == null)
            {
                Logging.Warning(
                    "Compatibility member is not an array: "
                    + label
                    + ".");
            }

            return elementType;
        }

        private static IndexedElementFactory
            CreateUshortConstructorFactory(Type elementType)
        {
            if (elementType == null)
            {
                return null;
            }

            return delegate(int index)
            {
                return Activator.CreateInstance(
                    elementType,
                    new object[] { (ushort)index });
            };
        }

        private static IndexedElementFactory
            CreateExtSegmentEndFactory(Type elementType)
        {
            if (elementType == null)
            {
                return null;
            }

            return delegate(int index)
            {
                ushort segmentId = (ushort)(index / 2);
                bool startNode = (index & 1) == 0;
                return Activator.CreateInstance(
                    elementType,
                    new object[] { segmentId, startNode });
            };
        }

        private static SplitElementFactory
            CreateSegmentDirTrafficFactory(Type elementType)
        {
            if (elementType == null)
            {
                return null;
            }

            FieldInfo meanSpeed = ThirdPartyCapacityCompatibility.FindField(
                elementType,
                "meanSpeed");
            return delegate(int index, bool startNode)
            {
                object value = Activator.CreateInstance(elementType);
                if (meanSpeed != null)
                {
                    meanSpeed.SetValue(value, (ushort)10000);
                }

                return value;
            };
        }

        private static object CreateParkingEntry(int index)
        {
            return new bool[2] { true, true };
        }
    }

    internal static class TMPEIndexPatches
    {
        public static bool TurnOnRedGetIndexPrefix(
            ushort segmentId,
            bool startNode,
            ref int __result)
        {
            __result = segmentId + (startNode
                ? 0
                : Capacity.ExpandedSegmentCapacity);
            return false;
        }

        public static bool SegmentEndGetIndexPrefix(
            ushort segmentId,
            bool startNode,
            ref int __result)
        {
            __result = segmentId + (startNode
                ? 0
                : Capacity.ExpandedSegmentCapacity);
            return false;
        }

        public static bool SegmentEndGetSegmentAndNodeFromIndexPrefix(
            int index,
            ref ushort segmentId,
            ref bool startNode)
        {
            startNode = index < Capacity.ExpandedSegmentCapacity;
            segmentId = (ushort)(index - (startNode
                ? 0
                : Capacity.ExpandedSegmentCapacity));
            return false;
        }

        public static bool TrafficMeasurementGetDirIndexPrefix(
            ushort segmentId,
            NetInfo.Direction dir,
            ref int __result)
        {
            __result = segmentId
                + (dir == NetInfo.Direction.Backward
                    ? Capacity.ExpandedSegmentCapacity
                    : 0);
            return false;
        }
    }
}
