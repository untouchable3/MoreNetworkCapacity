using System;
using System.Reflection;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class ZoningAdjusterCompatibility
    {
        private const string AssemblyName = "ZoningAdjuster";
        private const string ModName = "Zoning Adjuster";

        internal static void Apply()
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "Zoning Adjuster is not loaded; compatibility is inactive.");
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "ZoningAdjuster.SegmentData",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = ArrayCompatibility.GetStaticInstance(
                type,
                "Instance",
                "s_instance",
                "Zoning Adjuster SegmentData");
            FieldInfo field = ThirdPartyCapacityCompatibility.FindField(
                type,
                "_zoneBlockFlags");
            ArrayCompatibility.ExpandDirect(
                field,
                instance,
                0,
                Capacity.ExpandedSegmentCapacity,
                null,
                false,
                "Zoning Adjuster SegmentData._zoneBlockFlags");

            Logging.Info("Zoning Adjuster compatibility pass completed.");
        }
    }
}
