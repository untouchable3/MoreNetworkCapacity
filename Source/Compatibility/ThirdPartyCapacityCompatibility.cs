using System;
using System.Reflection;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static void Reset()
        {
            AdaptiveRoadsCompatibility.Reset();
            NetworkSkinsCompatibility.Reset();
            NodeControllerCompatibility.Reset();
            TMPECompatibility.Reset();
            EightyOne2Compatibility.Reset();
            StaticBufferCompatibility.Reset();
        }

        internal static void ApplyAll(Harmony harmony)
        {
            TryApply("TM:PE", delegate
            {
                TMPECompatibility.Apply(harmony);
            });
            TryApply("Node Controller Renewal", delegate
            {
                NodeControllerCompatibility.Apply(harmony);
            });
            TryApply("AdaptiveRoads", delegate
            {
                AdaptiveRoadsCompatibility.Apply(harmony);
            });
            TryApply("EightyOne2", delegate
            {
                EightyOne2Compatibility.Apply(harmony);
            });
            TryApply("NetworkSkins", delegate
            {
                NetworkSkinsCompatibility.Apply(harmony);
            });
            TryApply("Zoning Adjuster", delegate
            {
                ZoningAdjusterCompatibility.Apply();
            });
            TryApply("Third-party buffer references", delegate
            {
                StaticBufferCompatibility.ApplyAll(harmony);
            });
            TryApply("Final network color offsets", delegate
            {
                // NetworkSkins can inject its own color-map instructions
                // after MNC's initial transpiler has already run.
                NetworkColorOffsetPatch.ApplyFinalPass(harmony);
            });
        }

        internal static void ApplyEarly(Harmony harmony)
        {
            TryApply("NetworkSkins (early)", delegate
            {
                NetworkSkinsCompatibility.Apply(harmony);
            });
            TryApply("AdaptiveRoads (early)", delegate
            {
                AdaptiveRoadsCompatibility.Apply(harmony);
            });
            TryApply("Node Controller Renewal (early)", delegate
            {
                NodeControllerCompatibility.Apply(harmony);
            });
            TryApply("KianCommons validity limits (early)", delegate
            {
                StaticBufferCompatibility.ApplyEarly(harmony);
            });
        }

        internal static void PrepareForNetworkDeserialize(
            Harmony harmony,
            bool expandedSave)
        {
            TryApply("NetworkSkins network-load preparation", delegate
            {
                NetworkSkinsCompatibility.Apply(harmony);
            });
            TryApply("TM:PE network-load preparation", delegate
            {
                TMPECompatibility.PrepareForNetworkDeserialize(
                    harmony,
                    expandedSave);
            });
            TryApply("AdaptiveRoads network-load preparation", delegate
            {
                AdaptiveRoadsCompatibility.PrepareForNetworkDeserialize(
                    harmony,
                    expandedSave);
            });
            TryApply("Third-party network buffer refresh", delegate
            {
                StaticBufferCompatibility.ApplyAll(harmony);
            });
        }

        internal static void RetryAdaptiveRoads(Harmony harmony)
        {
            AdaptiveRoadsCompatibility.Retry(harmony);
        }

        internal static Assembly FindLoadedAssembly(string name)
        {
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; ++i)
            {
                Assembly assembly = assemblies[i];
                try
                {
                    if (string.Equals(
                        assembly.GetName().Name,
                        name,
                        StringComparison.Ordinal))
                    {
                        return assembly;
                    }
                }
                catch
                {
                    // Ignore assemblies whose metadata cannot be read.
                }
            }

            return null;
        }

        internal static Type FindType(
            Assembly assembly,
            string fullName,
            string modName)
        {
            if (assembly == null)
            {
                Logging.Info(
                    modName
                    + " is not loaded; compatibility is inactive.");
                return null;
            }

            Type type = assembly.GetType(fullName, false);
            if (type == null)
            {
                Logging.Warning(
                    "Could not find "
                    + fullName
                    + " in "
                    + modName
                    + ".");
            }

            return type;
        }

        internal static FieldInfo FindField(Type type, string name)
        {
            return ArrayCompatibility.FindField(type, name);
        }

        internal static FieldInfo FindBackingField(
            Type type,
            string propertyName)
        {
            return ArrayCompatibility.FindBackingField(
                type,
                propertyName);
        }

        private static void TryApply(
            string name,
            Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Logging.Error(
                    name
                    + " compatibility failed: "
                    + exception);
            }
        }
    }
}
