using System;
using System.Reflection;
using ColossalFramework;

namespace MoreNetworkCapacity
{
    internal static class NetManagerCustomizer
    {
        private static bool _loopLimitActivationPending;

        internal static bool LoopLimitActivationPending
        {
            get { return _loopLimitActivationPending; }
        }

        private static readonly FieldInfo NodesField =
            typeof(NetManager).GetField("m_nodes");

        private static readonly FieldInfo SegmentsField =
            typeof(NetManager).GetField("m_segments");

        private static readonly FieldInfo UpdatedNodesField =
            typeof(NetManager).GetField("m_updatedNodes");

        private static readonly FieldInfo UpdatedSegmentsField =
            typeof(NetManager).GetField("m_updatedSegments");

        private static readonly FieldInfo AdjustedSegmentsField =
            typeof(NetManager).GetField("m_adjustedSegments");

        private static readonly FieldInfo NodesUpdatedField =
            typeof(NetManager).GetField("m_nodesUpdated");

        private static readonly FieldInfo SegmentsUpdatedField =
            typeof(NetManager).GetField("m_segmentsUpdated");

        private static readonly FieldInfo CoverageNodesField =
            typeof(CoverageManager).GetField("m_nodes");

        private static readonly FieldInfo CoverageDistancesField =
            typeof(CoverageManager).GetField("m_distances");

        private static readonly FieldInfo ElectricityNodeGroupsField =
            typeof(ElectricityManager).GetField("m_nodeGroups");

        private static readonly FieldInfo ElectricityPulseUnitsField =
            typeof(ElectricityManager).GetField("m_pulseUnits");

        private static readonly FieldInfo WaterWaterPulseUnitsField =
            typeof(WaterManager).GetField("m_waterPulseUnits");

        private static readonly FieldInfo WaterSewagePulseUnitsField =
            typeof(WaterManager).GetField("m_sewagePulseUnits");

        private static readonly FieldInfo WaterHeatingPulseUnitsField =
            typeof(WaterManager).GetField("m_heatingPulseUnits");

        private static readonly FieldInfo WaterNodeDataField =
            typeof(WaterManager).GetField("m_nodeData");

        public static bool IsGameplayMode()
        {
            ToolManager toolManager = Singleton<ToolManager>.instance;
            if (toolManager == null || toolManager.m_properties == null)
            {
                return false;
            }

            ItemClass.Availability mode = toolManager.m_properties.m_mode;
            bool mapEditor =
                (mode & ItemClass.Availability.MapEditor) != 0;
            bool assetEditor =
                (mode & ItemClass.Availability.AssetEditor) != 0;

            return !mapEditor && !assetEditor;
        }

        public static bool IsGameplayLoadMode()
        {
            SimulationManager simulation =
                Singleton<SimulationManager>.instance;
            if (simulation != null && simulation.m_metaData != null)
            {
                switch (simulation.m_metaData.m_updateMode)
                {
                    case SimulationManager.UpdateMode.NewMap:
                    case SimulationManager.UpdateMode.LoadMap:
                    case SimulationManager.UpdateMode.NewAsset:
                    case SimulationManager.UpdateMode.LoadAsset:
                    case SimulationManager.UpdateMode.NewTheme:
                    case SimulationManager.UpdateMode.LoadTheme:
                        return false;

                    case SimulationManager.UpdateMode.NewGameFromMap:
                    case SimulationManager.UpdateMode.LoadGame:
                    case SimulationManager.UpdateMode.NewScenarioFromGame:
                    case SimulationManager.UpdateMode.LoadScenario:
                    case SimulationManager.UpdateMode.NewGameFromScenario:
                    case SimulationManager.UpdateMode.UpdateScenarioFromGame:
                    case SimulationManager.UpdateMode.NewScenarioFromMap:
                    case SimulationManager.UpdateMode.UpdateScenarioFromMap:
                        return true;
                }
            }

            return IsGameplayMode();
        }

        public static bool PrepareForLoad(NetManager manager)
        {
            if (!PatchState.ExpansionEnabled || manager == null)
            {
                return false;
            }

            if (!IsGameplayMode())
            {
                UpdateLoopLimits(manager);
                return IsExpanded(manager);
            }

            return EnsureExpanded(manager);
        }

        public static bool PrepareExpandedSaveForLoad(NetManager manager)
        {
            if (!PatchState.ExpansionEnabled || manager == null)
            {
                return false;
            }

            if (!EnsureExpanded(manager))
            {
                return false;
            }

            ResetForGameplayLoad(manager);
            return IsExpanded(manager);
        }

        public static bool PrepareVanillaSaveForLoad(NetManager manager)
        {
            if (manager == null)
            {
                return false;
            }

            // Expand before Data.Deserialize captures its local buffer
            // references. The read-size getters still stop at the vanilla
            // limits for an unmarked save, so the serialized stream remains
            // fully compatible.
            if (!EnsureExpanded(manager))
            {
                return false;
            }

            ResetForGameplayLoad(manager);
            SetVanillaLoopLimits();
            return IsExpanded(manager);
        }

        public static bool ExpandPreservingLoadedData(NetManager manager)
        {
            if (manager == null)
            {
                return false;
            }

            if (IsExpanded(manager))
            {
                UpdateLoopLimits(manager);
                Logging.Info(
                    "Network arrays were already expanded for load; "
                    + "skipping post-read replacement.");
                return true;
            }

            int oldNodeCapacity = GetBufferLength(manager.m_nodes);
            int oldSegmentCapacity = GetBufferLength(manager.m_segments);
            if (oldNodeCapacity != Capacity.VanillaNodeCapacity
                || oldSegmentCapacity != Capacity.VanillaSegmentCapacity)
            {
                Logging.Error(
                    "Cannot preserve loaded network data while expanding: "
                    + "nodes="
                    + oldNodeCapacity
                    + ", segments="
                    + oldSegmentCapacity
                    + ".");
                return false;
            }

            Array16<NetNode> nodes = CreateOrderedNodeArray(
                Capacity.ExpandedNodeCapacity,
                manager.m_nodes);
            Array16<NetSegment> segments = CreateOrderedSegmentArray(
                Capacity.ExpandedSegmentCapacity,
                manager.m_segments);
            if (nodes == null || segments == null)
            {
                return false;
            }

            manager.m_nodes = nodes;
            manager.m_segments = segments;
            SetAuxiliaryBuffersPreserving(
                manager,
                Capacity.ExpandedNodeCapacity,
                Capacity.ExpandedSegmentCapacity);
            UpdateLoopLimits(manager);

            Logging.Info(
                "Expanded NetManager arrays after vanilla-size load: nodes="
                + nodes.m_buffer.Length
                + ", segments="
                + segments.m_buffer.Length
                + ".");
            return true;
        }

        public static void UpdateLoopLimits(NetManager manager)
        {
            if (manager == null)
            {
                Capacity.NodeLoopLimit = Capacity.VanillaNodeCapacity;
                Capacity.SegmentLoopLimit = Capacity.VanillaSegmentCapacity;
                return;
            }

            ApplyExpandedArraySizes(manager);

            if (GetBufferLength(manager.m_nodes) == Capacity.ExpandedNodeCapacity)
            {
                Capacity.NodeLoopLimit = Capacity.ExpandedNodeCapacity;
            }
            else
            {
                Capacity.NodeLoopLimit = Capacity.VanillaNodeCapacity;
            }

            if (GetBufferLength(manager.m_segments) == Capacity.ExpandedSegmentCapacity)
            {
                Capacity.SegmentLoopLimit = Capacity.ExpandedSegmentCapacity;
            }
            else
            {
                Capacity.SegmentLoopLimit = Capacity.VanillaSegmentCapacity;
            }
        }

        private static void ApplyExpandedArraySizes(NetManager manager)
        {
            if (manager.m_nodes != null
                && GetBufferLength(manager.m_nodes)
                    == Capacity.ExpandedNodeCapacity
                && manager.m_nodes.m_size
                    != (uint)Capacity.ExpandedNodeSize)
            {
                manager.m_nodes.m_size =
                    (uint)Capacity.ExpandedNodeSize;
            }

            if (manager.m_segments != null
                && GetBufferLength(manager.m_segments)
                    == Capacity.ExpandedSegmentCapacity
                && manager.m_segments.m_size
                    != (uint)Capacity.ExpandedSegmentSize)
            {
                manager.m_segments.m_size =
                    (uint)Capacity.ExpandedSegmentSize;
            }
        }

        private static void ApplyLoadedDataArraySizes(
            NetManager manager,
            bool hasHighNodes,
            bool hasHighSegments)
        {
            if (manager.m_nodes != null
                && GetBufferLength(manager.m_nodes)
                    == Capacity.ExpandedNodeCapacity)
            {
                uint size = (uint)Capacity.ExpandedNodeSize;
                if (manager.m_nodes.m_size != size)
                {
                    manager.m_nodes.m_size = size;
                }
            }

            if (manager.m_segments != null
                && GetBufferLength(manager.m_segments)
                    == Capacity.ExpandedSegmentCapacity)
            {
                uint size = (uint)Capacity.ExpandedSegmentSize;
                if (manager.m_segments.m_size != size)
                {
                    manager.m_segments.m_size = size;
                }
            }
        }

        public static void UpdateLoopLimitsForLoadedData(NetManager manager)
        {
            if (manager == null)
            {
                SetVanillaLoopLimits();
                return;
            }

            bool highNodes = HasCreatedNodeAboveVanillaLimit(manager);
            bool highSegments =
                HasCreatedSegmentAboveVanillaLimit(manager);

            Capacity.NodeLoopLimit =
                highNodes
                && GetBufferLength(manager.m_nodes)
                    == Capacity.ExpandedNodeCapacity
                    ? Capacity.ExpandedNodeCapacity
                    : Capacity.VanillaNodeCapacity;

            Capacity.SegmentLoopLimit =
                highSegments
                && GetBufferLength(manager.m_segments)
                    == Capacity.ExpandedSegmentCapacity
                    ? Capacity.ExpandedSegmentCapacity
                : Capacity.VanillaSegmentCapacity;

            ApplyLoadedDataArraySizes(
                manager,
                highNodes,
                highSegments);
        }

        public static void DeferLoopLimitActivation(NetManager manager)
        {
            if (!IsExpanded(manager))
            {
                _loopLimitActivationPending = false;
                return;
            }

            _loopLimitActivationPending = true;
            Logging.Warning(
                "Expanded loop limits are deferred until the first "
                + "gameplay frame completes: current="
                + Capacity.NodeLoopLimit
                + "/"
                + Capacity.SegmentLoopLimit
                + ".");
        }

        public static void ActivateDeferredLoopLimits()
        {
            if (!_loopLimitActivationPending)
            {
                return;
            }

            _loopLimitActivationPending = false;
            NetManager manager = Singleton<NetManager>.instance;
            if (!IsExpanded(manager))
            {
                Logging.Error(
                    "Could not activate deferred expanded loop limits: "
                    + "NetManager is no longer expanded.");
                return;
            }

            UpdateLoopLimits(manager);
            Patches.NetManagerColorUpdatePatch.RefreshExpandedColors();
            Logging.Warning(
                "Activated expanded loop limits after the first gameplay "
                + "frame: "
                + Capacity.NodeLoopLimit
                + "/"
                + Capacity.SegmentLoopLimit
                + ".");
        }

        public static void CancelDeferredLoopLimitActivation()
        {
            _loopLimitActivationPending = false;
        }

        public static bool EnsureExpanded(NetManager manager)
        {
            if (manager == null)
            {
                return false;
            }

            if (IsExpanded(manager))
            {
                UpdateLoopLimits(manager);
                return true;
            }

            int nodeLength = GetBufferLength(manager.m_nodes);
            int segmentLength = GetBufferLength(manager.m_segments);

            if (nodeLength != Capacity.VanillaNodeCapacity
                || segmentLength != Capacity.VanillaSegmentCapacity)
            {
                UpdateLoopLimits(manager);
                Logging.Error(
                    "Network arrays have an unexpected size. "
                    + "Another mod may already control NetManager.");
                return false;
            }

            Array16<NetNode> nodes = CreateOrderedNodeArray(
                Capacity.ExpandedNodeCapacity);
            Array16<NetSegment> segments = CreateOrderedSegmentArray(
                Capacity.ExpandedSegmentCapacity);

            if (nodes == null || segments == null)
            {
                UpdateLoopLimits(manager);
                return false;
            }

            NodesField.SetValue(manager, nodes);
            SegmentsField.SetValue(manager, segments);
            SetAuxiliaryBuffers(
                manager,
                Capacity.ExpandedNodeCapacity,
                Capacity.ExpandedSegmentCapacity);
            UpdateLoopLimits(manager);

            Logging.Info(
                "Expanded NetManager arrays: nodes="
                + nodes.m_buffer.Length
                + ", segments="
                + segments.m_buffer.Length);
            return true;
        }

        public static bool IsExpanded(NetManager manager)
        {
            return manager != null
                && GetBufferLength(manager.m_nodes)
                    == Capacity.ExpandedNodeCapacity
                && GetBufferLength(manager.m_segments)
                    == Capacity.ExpandedSegmentCapacity;
        }

        public static void RebuildFreeLists(NetManager manager)
        {
            if (manager == null)
            {
                return;
            }

            RebuildNodeFreeList(manager.m_nodes);
            RebuildSegmentFreeList(manager.m_segments);
        }

        public static void ResetForGameplayLoad(NetManager manager)
        {
            if (manager == null)
            {
                return;
            }

            int nodeCapacity = GetBufferLength(manager.m_nodes);
            int segmentCapacity = GetBufferLength(manager.m_segments);

            ClearArray(manager.m_nodes == null
                ? null
                : manager.m_nodes.m_buffer);
            ClearArray(manager.m_segments == null
                ? null
                : manager.m_segments.m_buffer);

            if (manager.m_nodes != null)
            {
                manager.m_nodes.ClearUnused();
            }
            if (manager.m_segments != null)
            {
                manager.m_segments.ClearUnused();
            }

            SetAuxiliaryBuffers(manager, nodeCapacity, segmentCapacity);

            Logging.Info(
                "Reset network and node-indexed buffers for load: nodes="
                + nodeCapacity
                + ", segments="
                + segmentCapacity
                + ".");
        }

        public static void ResetPendingUpdatesAfterLoad(NetManager manager)
        {
            // NetManager.Data.AfterDeserialize already rebuilds every loaded
            // node and segment: bounds, tags, lane connections, flags,
            // renderers, and the full color range. Much of the pending work
            // duplicates this rebuild. Third-party load callbacks can however
            // enqueue fresh geometry changes AFTER AfterDeserialize.
            // The first simulation frame replays it one item at a time, and on an
            // expanded pool that is tens of thousands of entries, which shows
            // up as a load that never finishes. Clear the bulk replay, then
            // requeue Node Controller's captured initial-update nodes through
            // the game's own update mechanism, including their neighborhood.
            if (!PatchState.ExpansionEnabled
                || manager == null
                || !IsExpanded(manager))
            {
                return;
            }

            int highNodes = CountHighNodeIds(manager);
            int highSegments = CountHighSegmentIds(manager);

            ulong[] updatedNodes =
                UpdatedNodesField.GetValue(manager) as ulong[];
            ulong[] updatedSegments =
                UpdatedSegmentsField.GetValue(manager) as ulong[];
            ulong[] adjustedSegments =
                AdjustedSegmentsField.GetValue(manager) as ulong[];

            int updatedNodeBits = CountSetBits(updatedNodes);
            int updatedSegmentBits = CountSetBits(updatedSegments);
            int adjustedSegmentBits = CountSetBits(adjustedSegments);
            bool nodesUpdated =
                NodesUpdatedField != null
                && (bool)NodesUpdatedField.GetValue(manager);
            bool segmentsUpdated =
                SegmentsUpdatedField != null
                && (bool)SegmentsUpdatedField.GetValue(manager);

            ClearArray(updatedNodes);
            ClearArray(updatedSegments);
            if (NodesUpdatedField != null)
            {
                NodesUpdatedField.SetValue(manager, false);
            }
            if (SegmentsUpdatedField != null)
            {
                SegmentsUpdatedField.SetValue(manager, false);
            }

            int restoredNodeControllerNodes =
                Compatibility.NodeControllerLoadUpdates.Replay(manager);

            Logging.Warning(
                "Cleared pending network update work before simulation "
                + "warm-up: nodes="
                + updatedNodeBits
                + " bits (flag="
                + nodesUpdated
                + "), segments="
                + updatedSegmentBits
                + " bits (flag="
                + segmentsUpdated
                + "), adjusted segments left intact="
                + adjustedSegmentBits
                + " bits, expanded-save high ids="
                + highNodes
                + "/"
                + highSegments
                + ", requeued Node Controller initial nodes="
                + restoredNodeControllerNodes
                + " (loading expanded="
                + NetManagerDeserializeState.LoadingExpanded
                + ").");
        }

        public static int CountHighNodeIds(NetManager manager)
        {
            if (manager == null
                || manager.m_nodes == null
                || manager.m_nodes.m_buffer == null)
            {
                return 0;
            }

            int count = 0;
            NetNode[] buffer = manager.m_nodes.m_buffer;
            for (int i = Capacity.VanillaNodeCapacity; i < buffer.Length; ++i)
            {
                if ((buffer[i].m_flags & NetNode.Flags.Created) != 0)
                {
                    ++count;
                }
            }

            return count;
        }

        public static int CountHighSegmentIds(NetManager manager)
        {
            if (manager == null
                || manager.m_segments == null
                || manager.m_segments.m_buffer == null)
            {
                return 0;
            }

            int count = 0;
            NetSegment[] buffer = manager.m_segments.m_buffer;
            for (int i = Capacity.VanillaSegmentCapacity; i < buffer.Length; ++i)
            {
                if ((buffer[i].m_flags & NetSegment.Flags.Created) != 0)
                {
                    ++count;
                }
            }

            return count;
        }

        public static int CountPendingNodeUpdates()
        {
            NetManager manager = Singleton<NetManager>.instance;
            return manager == null
                ? 0
                : CountSetBits(
                    UpdatedNodesField.GetValue(manager) as ulong[]);
        }

        public static int CountPendingSegmentUpdates()
        {
            NetManager manager = Singleton<NetManager>.instance;
            return manager == null
                ? 0
                : CountSetBits(
                    UpdatedSegmentsField.GetValue(manager) as ulong[]);
        }

        private static bool HasCreatedNodeAboveVanillaLimit(NetManager manager)
        {
            if (manager == null
                || manager.m_nodes == null
                || manager.m_nodes.m_buffer == null)
            {
                return false;
            }

            NetNode[] buffer = manager.m_nodes.m_buffer;
            int end = Math.Min(buffer.Length, Capacity.ExpandedNodeCapacity);
            for (int i = Capacity.VanillaNodeCapacity; i < end; ++i)
            {
                if ((buffer[i].m_flags & NetNode.Flags.Created) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasCreatedSegmentAboveVanillaLimit(
            NetManager manager)
        {
            if (manager == null
                || manager.m_segments == null
                || manager.m_segments.m_buffer == null)
            {
                return false;
            }

            NetSegment[] buffer = manager.m_segments.m_buffer;
            int end = Math.Min(
                buffer.Length,
                Capacity.ExpandedSegmentCapacity);
            for (int i = Capacity.VanillaSegmentCapacity; i < end; ++i)
            {
                if ((buffer[i].m_flags & NetSegment.Flags.Created) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetVanillaLoopLimits()
        {
            Capacity.NodeLoopLimit = Capacity.VanillaNodeCapacity;
            Capacity.SegmentLoopLimit = Capacity.VanillaSegmentCapacity;

            NetManager manager = Singleton<NetManager>.instance;
            if (manager != null)
            {
                ApplyLoadedDataArraySizes(manager, false, false);
            }
        }

        private static Array16<NetNode> CreateOrderedNodeArray(int capacity)
        {
            return CreateOrderedNodeArray(capacity, null);
        }

        private static Array16<NetNode> CreateOrderedNodeArray(
            int capacity,
            Array16<NetNode> source)
        {
            Array16<NetNode> array =
                new Array16<NetNode>((uint)capacity);
            ushort reserved;
            if (!array.CreateItem(out reserved) || reserved != 0)
            {
                Logging.Error("Failed to reserve node id 0.");
                return null;
            }

            if (source != null && source.m_buffer != null)
            {
                Array.Copy(
                    source.m_buffer,
                    array.m_buffer,
                    Math.Min(source.m_buffer.Length, array.m_buffer.Length));
            }

            RebuildNodeFreeList(array);
            return array;
        }

        private static Array16<NetSegment> CreateOrderedSegmentArray(int capacity)
        {
            return CreateOrderedSegmentArray(capacity, null);
        }

        private static Array16<NetSegment> CreateOrderedSegmentArray(
            int capacity,
            Array16<NetSegment> source)
        {
            Array16<NetSegment> array =
                new Array16<NetSegment>((uint)capacity);
            ushort reserved;
            if (!array.CreateItem(out reserved) || reserved != 0)
            {
                Logging.Error("Failed to reserve segment id 0.");
                return null;
            }

            if (source != null && source.m_buffer != null)
            {
                Array.Copy(
                    source.m_buffer,
                    array.m_buffer,
                    Math.Min(source.m_buffer.Length, array.m_buffer.Length));
            }

            RebuildSegmentFreeList(array);
            return array;
        }

        private static void SetAuxiliaryBuffersPreserving(
            NetManager manager,
            int nodeCapacity,
            int segmentCapacity)
        {
            UpdatedNodesField.SetValue(
                manager,
                ResizeArray(
                    UpdatedNodesField.GetValue(manager) as ulong[],
                    nodeCapacity >> 6));
            UpdatedSegmentsField.SetValue(
                manager,
                ResizeArray(
                    UpdatedSegmentsField.GetValue(manager) as ulong[],
                    segmentCapacity >> 6));
            AdjustedSegmentsField.SetValue(
                manager,
                ResizeArray(
                    AdjustedSegmentsField.GetValue(manager) as ulong[],
                    segmentCapacity >> 6));

            ResizeNodeIndexedArray(
                Singleton<CoverageManager>.instance,
                CoverageNodesField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<CoverageManager>.instance,
                CoverageDistancesField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<ElectricityManager>.instance,
                ElectricityPulseUnitsField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<ElectricityManager>.instance,
                ElectricityNodeGroupsField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterWaterPulseUnitsField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterSewagePulseUnitsField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterHeatingPulseUnitsField,
                nodeCapacity);
            ResizeNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterNodeDataField,
                nodeCapacity);
        }

        private static ulong[] ResizeArray(ulong[] source, int length)
        {
            ulong[] replacement = new ulong[length];
            if (source != null)
            {
                Array.Copy(
                    source,
                    replacement,
                    Math.Min(source.Length, replacement.Length));
            }

            return replacement;
        }

        private static void ResizeNodeIndexedArray(
            object manager,
            FieldInfo field,
            int capacity)
        {
            if (manager == null || field == null || capacity <= 0)
            {
                return;
            }

            Type elementType = field.FieldType.GetElementType();
            if (elementType == null)
            {
                Logging.Warning(
                    "Could not resize non-array field "
                    + field.DeclaringType.Name
                    + "."
                    + field.Name
                    + ".");
                return;
            }

            Array previous = field.GetValue(manager) as Array;
            Array replacement = Array.CreateInstance(elementType, capacity);
            if (previous != null)
            {
                Array.Copy(
                    previous,
                    replacement,
                    Math.Min(previous.Length, replacement.Length));
            }

            field.SetValue(manager, replacement);
        }

        private static void SetAuxiliaryBuffers(
            NetManager manager,
            int nodeCapacity,
            int segmentCapacity)
        {
            UpdatedNodesField.SetValue(manager, new ulong[nodeCapacity >> 6]);
            UpdatedSegmentsField.SetValue(
                manager,
                new ulong[segmentCapacity >> 6]);
            AdjustedSegmentsField.SetValue(
                manager,
                new ulong[segmentCapacity >> 6]);

            ResetNodeIndexedArray(
                Singleton<CoverageManager>.instance,
                CoverageNodesField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<CoverageManager>.instance,
                CoverageDistancesField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<ElectricityManager>.instance,
                ElectricityPulseUnitsField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<ElectricityManager>.instance,
                ElectricityNodeGroupsField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterWaterPulseUnitsField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterSewagePulseUnitsField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterHeatingPulseUnitsField,
                nodeCapacity);
            ResetNodeIndexedArray(
                Singleton<WaterManager>.instance,
                WaterNodeDataField,
                nodeCapacity);
        }

        private static void RebuildNodeFreeList(Array16<NetNode> array)
        {
            if (array == null)
            {
                return;
            }

            array.ClearUnused();
            NetNode[] buffer = array.m_buffer;
            int logicalLength = Math.Min(
                buffer.Length,
                (int)array.m_size);
            for (int i = logicalLength - 1; i >= 1; --i)
            {
                if ((buffer[i].m_flags & NetNode.Flags.Created) == 0)
                {
                    array.ReleaseItem((ushort)i);
                }
            }

            Logging.Info(
                "Rebuilt node free list: buffer="
                + buffer.Length
                + ", size="
                + array.m_size
                + ".");
        }

        private static void RebuildSegmentFreeList(Array16<NetSegment> array)
        {
            if (array == null)
            {
                return;
            }

            array.ClearUnused();
            NetSegment[] buffer = array.m_buffer;
            int logicalLength = Math.Min(
                buffer.Length,
                (int)array.m_size);
            for (int i = logicalLength - 1; i >= 1; --i)
            {
                if ((buffer[i].m_flags & NetSegment.Flags.Created) == 0)
                {
                    array.ReleaseItem((ushort)i);
                }
            }

            Logging.Info(
                "Rebuilt segment free list: buffer="
                + buffer.Length
                + ", size="
                + array.m_size
                + ".");
        }

        private static void ClearArray(Array array)
        {
            if (array != null)
            {
                Array.Clear(array, 0, array.Length);
            }
        }

        private static int CountSetBits(ulong[] bitmap)
        {
            if (bitmap == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < bitmap.Length; ++i)
            {
                ulong value = bitmap[i];
                while (value != 0)
                {
                    value &= value - 1;
                    ++count;
                }
            }

            return count;
        }

        private static void ResetNodeIndexedArray(
            object manager,
            FieldInfo field,
            int capacity)
        {
            if (manager == null || field == null || capacity <= 0)
            {
                return;
            }

            Type elementType = field.FieldType.GetElementType();
            if (elementType == null)
            {
                Logging.Warning(
                    "Could not reset non-array field "
                    + field.DeclaringType.Name
                    + "."
                    + field.Name
                    + ".");
                return;
            }

            field.SetValue(
                manager,
                Array.CreateInstance(elementType, capacity));
        }

        private static int GetBufferLength<T>(Array16<T> array)
        {
            return array == null || array.m_buffer == null
                ? 0
                : array.m_buffer.Length;
        }
    }
}
