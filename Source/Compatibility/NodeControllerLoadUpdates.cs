using System;
using System.Collections.Generic;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class NodeControllerLoadUpdates
    {
        private static readonly object Sync = new object();
        private static ushort[] _initialNodes;

        internal static void Reset()
        {
            lock (Sync) { _initialNodes = null; }
        }

        internal static void Capture(ushort[] nodeIds, bool initial)
        {
            // Imports and interactive changes must not become a later load request.
            if (!initial) return;
            lock (Sync)
            {
                _initialNodes = nodeIds == null ? null : (ushort[])nodeIds.Clone();
            }
        }

        internal static int Replay(NetManager manager)
        {
            ushort[] nodeIds;
            lock (Sync)
            {
                nodeIds = _initialNodes;
                _initialNodes = null;
            }
            if (nodeIds == null || manager == null || manager.m_nodes == null
                || manager.m_nodes.m_buffer == null) return 0;

            NetNode[] buffer = manager.m_nodes.m_buffer;
            HashSet<ushort> queued = new HashSet<ushort>();
            for (int i = 0; i < nodeIds.Length; ++i)
            {
                ushort id = nodeIds[i];
                if (id == 0 || id >= buffer.Length
                    || (buffer[id].m_flags & NetNode.Flags.Created) == 0
                    || !queued.Add(id)) continue;
                // Original UpdateNode marks the node, connected segments and nearby
                // nodes for processing. Do not call CalculateNode/IMT on the load thread.
                manager.UpdateNode(id);
            }
            return queued.Count;
        }
    }
}
