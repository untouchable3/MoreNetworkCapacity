using System;
using System.Reflection;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class NetworkSkinsCompatibility
    {
        private const string AssemblyName = "NetworkSkins";
        private const string ModName = "NetworkSkins";

        private static bool _awakePatched;
        private static Type _managerType;

        internal static void Reset()
        {
            _awakePatched = false;
            _managerType = null;
        }

        internal static void Apply(Harmony harmony)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "NetworkSkins is not loaded; compatibility is inactive.");
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "NetworkSkins.Skins.NetworkSkinManager",
                ModName);
            if (type == null)
            {
                return;
            }

            _managerType = type;
            ExpandManager();
            PatchAwake(harmony, type);

            Logging.Info("NetworkSkins compatibility pass completed.");
        }

        public static void AwakePostfix()
        {
            ExpandManager();
        }

        private static void ExpandManager()
        {
            Type type = _managerType;
            if (type == null)
            {
                return;
            }

            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "SegmentSkins"),
                null,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                null,
                true,
                "NetworkSkins NetworkSkinManager.SegmentSkins");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    type,
                    "NodeSkins"),
                null,
                Capacity.VanillaNodeCapacity,
                Capacity.ExpandedNodeCapacity,
                null,
                true,
                "NetworkSkins NetworkSkinManager.NodeSkins");
        }

        private static void PatchAwake(Harmony harmony, Type type)
        {
            if (_awakePatched || harmony == null)
            {
                return;
            }

            MethodInfo awake = type.GetMethod(
                "Awake",
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (awake == null)
            {
                Logging.Warning(
                    "Could not find NetworkSkins NetworkSkinManager.Awake.");
                return;
            }

            MethodInfo postfix = typeof(NetworkSkinsCompatibility).GetMethod(
                "AwakePostfix",
                BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            harmony.Patch(
                awake,
                null,
                new HarmonyMethod(postfix),
                null,
                null);
            _awakePatched = true;
            Logging.Info(
                "Patched NetworkSkins NetworkSkinManager.Awake for "
                + "post-construction array expansion.");
        }
    }
}
