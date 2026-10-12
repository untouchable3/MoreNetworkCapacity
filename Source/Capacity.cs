using ColossalFramework;

namespace MoreNetworkCapacity
{
    public static class Capacity
    {
        public const int VanillaNodeCapacity = 32768;
        public const int VanillaSegmentCapacity = 36864;
        public const int VanillaNodeSize = 32768;
        public const int VanillaSegmentSize = 36864;

        // Deliberately kept below 2^16. Widely used mods iterate the
        // NetManager buffers with a UInt16 index counter (Repaint,
        // MoveIt, WatchIt, Zoning Adjuster, RON, Transport Lines Manager,
        // CSL Modern Map, Network Anarchy, Precision Engineering). A
        // buffer of exactly 65536 entries makes those counters wrap from
        // 65535 back to 0 and hang the main thread (observed with
        // Repaint 1.7.4 during level loading). 65024 is a multiple of 64
        // so the pending-update bitmaps stay aligned, and it preserves
        // the 512 reserved slots used by the vanilla layout.
        public const int ExpandedNodeCapacity = 65024;
        public const int ExpandedSegmentCapacity = 65024;

        public const int ExpandedNodeStop = 64512;
        public const int ExpandedSegmentStop = 64512;
        public const int ExpandedNodeSize = ExpandedNodeStop + 1;
        public const int ExpandedSegmentSize = ExpandedSegmentStop + 1;

        public const int VanillaColorIndexCapacity = 119042;
        public const int SegmentColorIndexBase = 49152;
        public const int VanillaNodeColorIndexBase = 86016;
        public const int VanillaEventColorIndexBase = 118784;
        public const int EventColorIndexCount = 256;

        // The vanilla color map is packed as buildings, segments, nodes,
        // then events. Expanding segments moves the node base, and expanding
        // nodes moves the event base. Keep enough room for the event range
        // while remaining inside the renderer's 512x512 color texture.
        public const int ExpandedColorIndexCapacity = 180224;

        public static int NodeLoopLimit = VanillaNodeCapacity;
        public static int SegmentLoopLimit = VanillaSegmentCapacity;
        public static int NodeStorageSize = VanillaNodeCapacity;

        public static int GetNodeBufferLength()
        {
            NetManager manager = Singleton<NetManager>.instance;
            if (manager == null || manager.m_nodes == null || manager.m_nodes.m_buffer == null)
            {
                return VanillaNodeCapacity;
            }

            return manager.m_nodes.m_buffer.Length;
        }

        public static int GetSegmentBufferLength()
        {
            NetManager manager = Singleton<NetManager>.instance;
            if (manager == null || manager.m_segments == null || manager.m_segments.m_buffer == null)
            {
                return VanillaSegmentCapacity;
            }

            return manager.m_segments.m_buffer.Length;
        }

        public static int GetNodeWriteSize()
        {
            int length = GetNodeBufferLength();
            if (PatchState.SaveExpanded)
            {
                return length;
            }

            return length < VanillaNodeCapacity
                ? length
                : VanillaNodeCapacity;
        }

        public static int GetSegmentWriteSize()
        {
            int length = GetSegmentBufferLength();
            if (PatchState.SaveExpanded)
            {
                return length;
            }

            return length < VanillaSegmentCapacity
                ? length
                : VanillaSegmentCapacity;
        }

        public static int GetNodeStopLimit()
        {
            int length = GetNodeBufferLength();
            int maximum = length - 512;
            if (maximum > ExpandedNodeStop)
            {
                maximum = ExpandedNodeStop;
            }

            return maximum;
        }

        public static int GetSegmentStopLimit()
        {
            int length = GetSegmentBufferLength();
            int maximum = length - 512;
            if (maximum > ExpandedSegmentStop)
            {
                maximum = ExpandedSegmentStop;
            }

            return maximum;
        }

        public static int GetColorIndexCapacity()
        {
            return ExpandedColorIndexCapacity;
        }

        public static int GetNodeColorIndexBase()
        {
            int segmentCapacity = SegmentLoopLimit;
            if (segmentCapacity < VanillaSegmentCapacity)
            {
                segmentCapacity = VanillaSegmentCapacity;
            }

            return SegmentColorIndexBase + segmentCapacity;
        }

        public static int GetEventColorIndexBase()
        {
            int nodeCapacity = NodeLoopLimit;
            if (nodeCapacity < VanillaNodeCapacity)
            {
                nodeCapacity = VanillaNodeCapacity;
            }

            return GetNodeColorIndexBase() + nodeCapacity;
        }

        // AdaptiveRoads serializes one record for every expanded node,
        // segment, and segment end. Its original fixed writer capacity was
        // sized for the vanilla arrays and is not large enough for 65024.
        public static int GetAdaptiveRoadsSerializationCapacity()
        {
            return 4194304;
        }

        // NetManager.UpdateNodeColors()/UpdateSegmentColors() (the no-argument
        // overloads) hardcode the vanilla refresh ranges 0..32767 and
        // 0..36863. They are called on info-view changes and force a full
        // recolor, so without these helpers the expanded ids drop out of the
        // range and keep a stale or uninitialized color-map pixel.
        public static int GetNodeColorUpdateMax()
        {
            return NodeLoopLimit - 1;
        }

        public static int GetSegmentColorUpdateMax()
        {
            return SegmentLoopLimit - 1;
        }

        // EightyOne2's water container rejects and clears pulse entries whose
        // node id is >= 32768 while reading, using a hardcoded vanilla limit.
        // Report the active pool size instead so expanded ids survive a load.
        public static int GetNodeIdValidationLimit()
        {
            bool expanded =
                PatchState.SaveExpanded
                || NetManagerDeserializeState.LoadingExpanded
                || Metadata.IsExpandedSave();
            if (!expanded)
            {
                return VanillaNodeCapacity;
            }

            return NodeLoopLimit > VanillaNodeCapacity
                ? NodeLoopLimit
                : ExpandedNodeCapacity;
        }

        public static int GetElectricityNodeGroupLength()
        {
            bool expanded =
                PatchState.SaveExpanded
                || NetManagerDeserializeState.LoadingExpanded
                || Metadata.IsExpandedSave();
            if (!expanded)
            {
                return VanillaNodeCapacity;
            }

            ElectricityManager manager =
                Singleton<ElectricityManager>.instance;
            if (manager == null || manager.m_nodeGroups == null)
            {
                return ExpandedNodeCapacity;
            }

            return manager.m_nodeGroups.Length;
        }

        public static int GetNodeReadSize()
        {
            if (NetManagerDeserializeState.LoadingExpanded)
            {
                return GetNodeBufferLength();
            }

            int length = GetNodeBufferLength();
            return length < VanillaNodeCapacity ? length : VanillaNodeCapacity;
        }

        // ElectricityManager and WaterManager serialize node-indexed data in
        // separate save blocks. Their block length must follow the save
        // marker itself, because those blocks may be deserialized before
        // NetManager sets NetManagerDeserializeState.LoadingExpanded.
        public static int GetAuxiliaryNodeStorageSize()
        {
            if (PatchState.SaveExpanded
                || NetManagerDeserializeState.LoadingExpanded
                || Metadata.IsExpandedSave())
            {
                return ExpandedNodeCapacity;
            }

            return VanillaNodeCapacity;
        }

        public static int GetSegmentReadSize()
        {
            if (NetManagerDeserializeState.LoadingExpanded)
            {
                return GetSegmentBufferLength();
            }

            int length = GetSegmentBufferLength();
            return length < VanillaSegmentCapacity ? length : VanillaSegmentCapacity;
        }
    }
}
