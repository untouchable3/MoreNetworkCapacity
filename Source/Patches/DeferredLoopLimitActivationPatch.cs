using ColossalFramework;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(SimulationManager), "SimulationStep")]
    internal static class DeferredLoopLimitActivationPatch
    {
        private static void Postfix()
        {
            if (NetManagerCustomizer.LoopLimitActivationPending)
                NetManagerCustomizer.ActivateDeferredLoopLimits();
            if (AdaptiveRoadsCompatibility.RetryPending
                || NetworkColorOffsetPatch.LatePassPending)
                ThirdPartyCapacityCompatibility.RetryAdaptiveRoads(
                    HarmonyBootstrap.HarmonyInstance);
        }
    }
}
