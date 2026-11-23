using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // LoadingManager assigns the incoming metadata before deserializing this
    // container. Reset only MNC's previous-city state, retaining that marker.
    [HarmonyPatch(typeof(SimulationManager.Data), "Deserialize")]
    internal static class SimulationDataReadStatePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            if (!PatchState.ExpansionEnabled) return;
            PatchState.MarkSaveVanilla();
            NetManagerDeserializeState.ResetLoadingExpanded();
            NetManagerCustomizer.CancelDeferredLoopLimitActivation();
            LoadStageJournal.Record("load: before simulation data read; previous-city layout state reset");
        }
    }
}
