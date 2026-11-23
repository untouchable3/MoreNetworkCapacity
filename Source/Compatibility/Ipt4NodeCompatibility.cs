using System;
using System.Reflection;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class Ipt4NodeCompatibility
    {
        internal static readonly Guid SupportedBuild = new Guid("216cfa43-eea3-4623-a1ca-3c83a5774eb1");
        private static bool installed;

        internal static void Reset() { installed = false; }

        internal static void Apply(Harmony harmony)
        {
            if (installed) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("ImprovedPublicTransport4");
            if (assembly == null || assembly.ManifestModule.ModuleVersionId != SupportedBuild) return;

            Type cache = assembly.GetType("ImprovedPublicTransport.Data.CachedNodeData", true);
            Type limiter = assembly.GetType("StopsAndStations.PassengerCountLimiter", true);
            MethodInfo load = AccessTools.DeclaredMethod(cache, "TryLoadData", null, null);
            MethodInfo ensure = AccessTools.DeclaredMethod(limiter, "EnsureBuffers", null, null);
            MethodInfo bounds = AccessTools.DeclaredMethod(typeof(Ipt2NodeCompatibility), "Transpiler", null, null);
            MethodInfo refresh = AccessTools.DeclaredMethod(typeof(Ipt4NodeCompatibility), "RefreshBuffers", null, null);
            try
            {
                harmony.Patch(load, null, null, new HarmonyMethod(bounds), null);
                harmony.Patch(ensure, null, new HarmonyMethod(refresh), null, null);
                installed = true;
                Logging.Info("IPT4 node cache and passenger count front/back buffers patched.");
            }
            catch
            {
                harmony.Unpatch(load, bounds);
                harmony.Unpatch(ensure, refresh);
                throw;
            }
        }

        private static void RefreshBuffers(ref bool __result, ref ushort[] ___passengerCountFront,
            ref ushort[] ___passengerCountBack, ref NetNode[] ___nodes, ref NetSegment[] ___segments)
        {
            if (!__result) return;
            NetManager net = Singleton<NetManager>.instance;
            if (net == null || net.m_nodes == null || net.m_nodes.m_buffer == null ||
                net.m_segments == null || net.m_segments.m_buffer == null)
            {
                __result = false;
                return;
            }

            ___nodes = net.m_nodes.m_buffer;
            ___segments = net.m_segments.m_buffer;
            // Preserve both generations across a resize. IPT4 publishes the back buffer every 16 frames.
            int length = ___nodes.Length;
            if (___passengerCountFront.Length != length) Array.Resize(ref ___passengerCountFront, length);
            if (___passengerCountBack.Length != length) Array.Resize(ref ___passengerCountBack, length);
        }
    }
}
