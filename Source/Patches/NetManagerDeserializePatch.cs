using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager.Data), "Deserialize")]
    internal static class NetManagerDeserializePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            LoadStageJournal.Record("load: before network preparation");
            NodeControllerLoadUpdates.Reset();
            if (SimulationStepDiagnostics.Requested)
            {
                SimulationStepDiagnostics.InstallEarly(HarmonyBootstrap.HarmonyInstance);
                SimulationStepDiagnostics.InitializeEarly();
            }
            NetManager manager = Singleton<NetManager>.instance;
            bool expandedSave = Metadata.IsExpandedSave();
            bool gameplay = NetManagerCustomizer.IsGameplayLoadMode();
            LoadStageJournal.Record("load: preparing network pools; expanded save="
                + expandedSave + ", gameplay=" + gameplay);
            bool expandedNow = false;

            if (PatchState.ExpansionEnabled && manager != null)
            {
                if (expandedSave)
                {
                    expandedNow =
                        NetManagerCustomizer.PrepareExpandedSaveForLoad(
                            manager);
                }
                else if (gameplay)
                {
                    expandedNow =
                        NetManagerCustomizer.PrepareVanillaSaveForLoad(manager);
                }
            }

            if ((expandedSave || PatchState.ExpansionEnabled && gameplay)
                && !expandedNow)
            {
                LoadStageJournal.Record("load failed: network pools could not be prepared");
                throw new InvalidOperationException(
                    "Network pools could not be prepared; refusing to read an incompatible save layout.");
            }

            if (expandedNow)
            {
                LoadStageJournal.Record("load: before network deserialize compatibility");
                ThirdPartyCapacityCompatibility.PrepareForNetworkDeserialize(
                    HarmonyBootstrap.HarmonyInstance,
                    expandedSave);
            }

            NetManagerDeserializeState.SetLoadingExpanded(
                expandedSave && expandedNow);

            Logging.Info(
                "Network load preparation: save expanded="
                + expandedSave
                + ", update mode="
                + GetUpdateMode()
                + ", gameplay="
                + gameplay
                + ", arrays expanded="
                + expandedNow
                + ", loading expanded="
                + NetManagerDeserializeState.LoadingExpanded
                + ".");
            LoadStageJournal.Record("load: prepared; before original network data read");
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            LoadStageJournal.Record("load: original network read returned; before post-read expansion");
            NetManager manager = Singleton<NetManager>.instance;
            if (manager == null
                || !PatchState.ExpansionEnabled
                || !NetManagerCustomizer.IsGameplayLoadMode())
            {
                LoadStageJournal.Record("load: network post-read skipped outside expanded gameplay");
                return;
            }

            if (!NetManagerDeserializeState.LoadingExpanded
                && !NetManagerCustomizer.ExpandPreservingLoadedData(manager))
            {
                LoadStageJournal.Record("load failed: post-read network expansion incomplete");
                return;
            }

            LoadStageJournal.Record("load: before network free-list rebuild");
            NetManagerCustomizer.RebuildFreeLists(manager);
            NetManagerCustomizer.UpdateLoopLimitsForLoadedData(manager);
            LoadStageJournal.Record("load: network post-read preparation completed");
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);

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
                AccessTools.Method(typeof(Capacity), "GetNodeReadSize", null, null),
                AccessTools.Method(typeof(Capacity), "GetSegmentReadSize", null, null));

            Logging.Info(
                "Network load sizes patched: nodes="
                + 1
                + ", segments="
                + 1
                + ", expanded="
                + NetManagerDeserializeState.LoadingExpanded
                + ".");

            return list;
        }


        private static SimulationManager.UpdateMode GetUpdateMode()
        {
            SimulationManager simulation =
                Singleton<SimulationManager>.instance;
            if (simulation == null || simulation.m_metaData == null)
            {
                return SimulationManager.UpdateMode.Undefined;
            }

            return simulation.m_metaData.m_updateMode;
        }
    }
}
