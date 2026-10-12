using ColossalFramework.Packaging;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // The other public LoadLevel overloads forward to this entry point.
    [HarmonyPatch(typeof(LoadingManager), "LoadLevel", new[] {
        typeof(Package.Asset), typeof(string), typeof(string),
        typeof(SimulationMetaData), typeof(bool) })]
    internal static class LoadStageJournalPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            LoadStageJournal.Record("load requested: before LoadingManager.LoadLevel");
        }
    }
}
