using ColossalFramework;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(LoadingWrapper), "OnLevelLoaded")]
    internal static class OnLevelLoadedPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            LoadStageJournal.Record("load: before level-loaded callbacks");
        }

        private static void Postfix()
        {
            LoadStageJournal.Record("load: before MNC level-loaded finalization");
            NetManager manager = Singleton<NetManager>.instance;
            bool expandedSave = Metadata.IsExpandedSave();
            if (!NetManagerCustomizer.IsGameplayMode())
            {
                NetManagerCustomizer.CancelDeferredLoopLimitActivation();
                Metadata.ClearExpandedSave();
                NetManagerDeserializeState.ResetLoadingExpanded();
                LoadStageJournal.Record("load: non-gameplay finalization completed");
                return;
            }

            if (!NetManagerCustomizer.IsExpanded(manager))
            {
                NetManagerCustomizer.CancelDeferredLoopLimitActivation();
                Logging.Error(
                    "Network arrays were not expanded during deserialization. "
                    + "Leaving the loaded network untouched.");
                Metadata.ClearExpandedSave();
                NetManagerDeserializeState.ResetLoadingExpanded();
                LoadStageJournal.Record("load failed: expected expanded arrays missing");
                return;
            }

            NetManagerCustomizer.UpdateLoopLimitsForLoadedData(manager);
            Metadata.SetExpandedSave();

            if (expandedSave)
            {
                // A marked expanded save can contain high ids that are only
                // valid with the expanded limits. Waiting for SimulationStep
                // deadlocks loading when the simulation cannot start with the
                // vanilla limits, so activate the loops before the first frame.
                NetManagerCustomizer.UpdateLoopLimits(manager);
                NetManagerCustomizer.CancelDeferredLoopLimitActivation();
                Logging.Info(
                    "Activated expanded loop limits immediately for a marked "
                    + "expanded save: "
                    + Capacity.NodeLoopLimit
                    + "/"
                    + Capacity.SegmentLoopLimit
                    + ".");
                SimulationStepDiagnostics.BeginMonitoring(
                    HarmonyBootstrap.HarmonyInstance,
                    manager);
            }

            LoadStageJournal.Record("load: before final compatibility and color preparation");
            ThirdPartyCapacityCompatibility.ApplyAll(
                HarmonyBootstrap.HarmonyInstance);
            NetworkColorOffsetPatch.ApplyPostLoadFinalPass(
                HarmonyBootstrap.HarmonyInstance);

            RenderManagerAwakePatch.EnsureCapacity();
            NetManagerColorUpdatePatch.RefreshExpandedColors();
            NetManagerDeserializeState.ResetLoadingExpanded();

            if (!expandedSave
                && Capacity.NodeLoopLimit == Capacity.VanillaNodeCapacity
                && Capacity.SegmentLoopLimit
                    == Capacity.VanillaSegmentCapacity)
            {
                NetManagerCustomizer.DeferLoopLimitActivation(manager);
            }
            else
            {
                NetManagerCustomizer.CancelDeferredLoopLimitActivation();
            }

            Logging.Info(
                "Network load completed with expanded arrays.");
            LoadStageJournal.Record("load: MNC finalization completed; no steady-simulation checkpoints");
        }
    }
}
