using System;
using HarmonyLib;
using ICities;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    public sealed class TLMStopStatisticsData : SerializableDataExtensionBase
    {
        private const string DataId = "MoreNetworkCapacity/TLMHighStopStatistics/v1";

        public override void OnLoadData()
        {
            TLMStopStatistics.Clear();
            if (!PatchState.ExpansionEnabled) return;
            try { TLMStopStatistics.Deserialize(serializableDataManager.LoadData(DataId)); }
            catch (Exception e) { Logging.Error("TLM high-stop statistics could not be restored: " + e); }
        }

        public override void OnSaveData()
        {
            if (!PatchState.ExpansionEnabled) return;
            serializableDataManager.SaveData(DataId, TLMStopStatistics.Serialize());
        }

        public override void OnReleased()
        { TLMStopStatistics.Clear(); base.OnReleased(); }
    }

    [HarmonyPatch(typeof(SimulationManager.Data), "Deserialize")]
    internal static class TLMStopStatisticsReadResetPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix() { TLMStopStatistics.Clear(); }
    }
}
