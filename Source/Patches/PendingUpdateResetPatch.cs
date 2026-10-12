using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(SerializableDataWrapper), "OnLoadData")]
    internal static class PendingUpdateResetPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            NetManager netManager = Singleton<NetManager>.instance;
            NetManagerCustomizer.ResetPendingUpdatesAfterLoad(netManager);
        }
    }
}
