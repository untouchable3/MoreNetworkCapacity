using System;
using System.Reflection;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class Ipt3NodeCompatibility
    {
        internal static readonly Guid SupportedBuild = new Guid("4202d207-e0db-4fbf-8399-efce7d952327");
        private static bool installed;

        internal static void Reset() { installed = false; }

        internal static void Apply(Harmony harmony)
        {
            if (installed) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("ImprovedPublicTransport3");
            if (assembly == null || assembly.ManifestModule.ModuleVersionId != SupportedBuild) return;

            Type cache = assembly.GetType("ImprovedPublicTransport.Data.CachedNodeData", true);
            Type limiter = assembly.GetType("StopsAndStations.PassengerCountLimiter", true);
            MethodInfo load = AccessTools.DeclaredMethod(cache, "TryLoadData", null, null);
            MethodInfo save = AccessTools.DeclaredMethod(cache, "OnSaveData", null, null);
            MethodInfo tick = AccessTools.DeclaredMethod(limiter, "OnBeforeSimulationTick", null, null);
            MethodInfo frame = AccessTools.DeclaredMethod(limiter, "OnBeforeSimulationFrame", null, null);
            MethodInfo bounds = AccessTools.DeclaredMethod(typeof(Ipt2NodeCompatibility), "Transpiler", null, null);
            MethodInfo refresh = AccessTools.DeclaredMethod(typeof(Ipt3NodeCompatibility), "RefreshBuffers", null, null);
            try
            {
                harmony.Patch(load, null, null, new HarmonyMethod(bounds), null);
                harmony.Patch(save, null, null, new HarmonyMethod(bounds), null);
                harmony.Patch(tick, new HarmonyMethod(refresh), null, null, null);
                harmony.Patch(frame, new HarmonyMethod(refresh), null, null, null);
                installed = true;
                Logging.Info("IPT3 node cache, serialization and passenger count limiter patched.");
            }
            catch
            {
                harmony.Unpatch(load, bounds);
                harmony.Unpatch(save, bounds);
                harmony.Unpatch(tick, refresh);
                harmony.Unpatch(frame, refresh);
                throw;
            }
        }

        // IPT3 captures these arrays in the extension constructor, before a pool can be replaced.
        private static void RefreshBuffers(ref ushort[] ___passengerCount, ref NetNode[] ___nodes,
            ref NetSegment[] ___segments)
        {
            if (!Singleton<NetManager>.exists) return;
            NetManager net = Singleton<NetManager>.instance;
            if (net.m_nodes == null || net.m_nodes.m_buffer == null ||
                net.m_segments == null || net.m_segments.m_buffer == null) return;
            ___nodes = net.m_nodes.m_buffer;
            ___segments = net.m_segments.m_buffer;
            if (___passengerCount.Length != ___nodes.Length)
                Array.Resize(ref ___passengerCount, ___nodes.Length);
        }
    }
}
