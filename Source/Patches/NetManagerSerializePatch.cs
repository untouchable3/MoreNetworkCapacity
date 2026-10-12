using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager.Data), "Serialize")]
    internal static class NetManagerSerializePatch
    {
        private static DateTime _serializeStartedUtc;

        private static void Prefix()
        {
            _serializeStartedUtc = DateTime.UtcNow;
            Logging.Info(
                "MNC SAVE NetManager.Serialize BEGIN"
                + " | saveExpanded=" + PatchState.SaveExpanded
                + " | metadataExpanded=" + Metadata.IsExpandedSave()
                + " | updateMode=" + GetUpdateMode()
                + " | nodes=" + GetNodeBufferLength()
                + " | segments=" + GetSegmentBufferLength()
                + " | lanes=" + GetLaneBufferLength()
                + " | nodeWriteSize=" + Capacity.GetNodeWriteSize()
                + " | segmentWriteSize=" + Capacity.GetSegmentWriteSize());
        }

        private static void Postfix()
        {
            Logging.Info(
                "MNC SAVE NetManager.Serialize END"
                + " | elapsedMs="
                + (DateTime.UtcNow - _serializeStartedUtc).TotalMilliseconds
                + " | saveExpanded=" + PatchState.SaveExpanded
                + " | metadataExpanded=" + Metadata.IsExpandedSave()
                + " | nodes=" + GetNodeBufferLength()
                + " | segments=" + GetSegmentBufferLength()
                + " | nodeWriteSize=" + Capacity.GetNodeWriteSize()
                + " | segmentWriteSize=" + Capacity.GetSegmentWriteSize());
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Logging.Error(
                    "MNC SAVE NetManager.Serialize EXCEPTION: "
                    + __exception);
            }

            return __exception;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);

            FieldInfo nodesField =
                AccessTools.Field(typeof(NetManager), "m_nodes");
            FieldInfo segmentsField =
                AccessTools.Field(typeof(NetManager), "m_segments");
            FieldInfo nodesBufferField = nodesField == null
                ? null
                : AccessTools.Field(nodesField.FieldType, "m_buffer");
            FieldInfo segmentsBufferField = segmentsField == null
                ? null
                : AccessTools.Field(segmentsField.FieldType, "m_buffer");

            NetworkBufferSizePatch.Apply(
                list, nodesField, nodesBufferField,
                segmentsField, segmentsBufferField,
                AccessTools.Method(typeof(Capacity), "GetNodeWriteSize", null, null),
                AccessTools.Method(typeof(Capacity), "GetSegmentWriteSize", null, null));

            Logging.Info(
                "Network save sizes patched: nodes="
                + 1
                + ", segments="
                + 1
                + ", expanded="
                + PatchState.SaveExpanded
                + ".");

            return list;
        }


        private static int GetNodeBufferLength()
        {
            NetManager manager = Singleton<NetManager>.instance;
            return manager == null
                || manager.m_nodes == null
                || manager.m_nodes.m_buffer == null
                ? -1
                : manager.m_nodes.m_buffer.Length;
        }

        private static int GetSegmentBufferLength()
        {
            NetManager manager = Singleton<NetManager>.instance;
            return manager == null
                || manager.m_segments == null
                || manager.m_segments.m_buffer == null
                ? -1
                : manager.m_segments.m_buffer.Length;
        }

        private static int GetLaneBufferLength()
        {
            NetManager manager = Singleton<NetManager>.instance;
            return manager == null
                || manager.m_lanes == null
                || manager.m_lanes.m_buffer == null
                ? -1
                : manager.m_lanes.m_buffer.Length;
        }

        private static SimulationManager.UpdateMode GetUpdateMode()
        {
            SimulationManager simulation =
                Singleton<SimulationManager>.instance;
            return simulation == null || simulation.m_metaData == null
                ? SimulationManager.UpdateMode.Undefined
                : simulation.m_metaData.m_updateMode;
        }

    }
}
