using System;
using System.Collections.Generic;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class HarmonyBootstrap
    {
        internal const string HarmonyId = "com.codex.csl.more-network-capacity";

        private static Harmony _harmony;
        private static int _enableGeneration;
        private static bool _enableRequested;

        internal static Harmony HarmonyInstance
        {
            get { return _harmony; }
        }

        internal static void Enable()
        {
            LoadStageJournal.Record("enable: before error-report setup");
            ErrorReport.Start();
            Logging.Info("Enable/load checkpoint journal: " + LoadStageJournal.Status);
            _enableRequested = true;
            int generation = ++_enableGeneration;
            LoadStageJournal.Record("enable: waiting for Harmony");
            HarmonyHelper.DoOnHarmonyReady(delegate
            {
                if (_enableRequested && generation == _enableGeneration)
                    ApplyPatches();
            });
        }

        internal static void Disable()
        {
            LoadStageJournal.Record("disable: before rollback");
            _enableRequested = false;
            ++_enableGeneration;
            try { RollBack(); }
            catch (Exception exception)
            {
                Logging.Error("Disable failed; restart required: " + exception);
                throw;
            }
            finally { ErrorReport.Stop(); LoadStageJournal.Record("disable finished; see errors if rollback failed"); }
        }

        private static void RollBack()
        {
            PatchState.MarkDisabled();
            NetManagerCustomizer.CancelDeferredLoopLimitActivation();
            try
            {
                SimulationStepDiagnostics.Stop();
            }
            finally
            {
                if (_harmony != null)
                {
                    // Keep the instance if unpatching fails, preventing an
                    // unsafe reinstallation over residual patches.
                    _harmony.UnpatchAll(HarmonyId);
                    _harmony = null;
                }
                ThirdPartyCapacityCompatibility.Reset();
                NetworkColorOffsetPatch.ResetFinalPass();
            }
        }

        private static void ApplyPatches()
        {
            if (_harmony != null)
            {
                return;
            }

            string stage = "game assembly validation";
            try
            {
                LoadStageJournal.Record("enable: " + stage);
                if (!GameVersion.IsSupported)
                {
                    Logging.Error("Unsupported game assembly. The mod will stay inactive.");
                    LoadStageJournal.Record("enable stopped: unsupported game assembly");
                    return;
                }

                LoadStageJournal.Record("enable: before Harmony creation and patch discovery");
                _harmony = new Harmony(HarmonyId);
                List<Type> patchTypes = GetPatchTypes();
                int applied = 0;
                for (int i = 0; i < patchTypes.Count; ++i)
                {
                    Type patchType = patchTypes[i];
                    stage = patchType.FullName;
                    LoadStageJournal.Record("enable: before patch " + stage);
                    _harmony.CreateClassProcessor(patchType).Patch();
                    ++applied;
                }

                PatchState.MarkEnabled();
                stage = "optional simulation diagnostics";
                LoadStageJournal.Record("enable: " + stage);
                SimulationStepDiagnostics.InstallEarly(_harmony);
                SimulationStepDiagnostics.InitializeEarly();
                stage = "render group compatibility";
                LoadStageJournal.Record("enable: " + stage);
                RenderGroupMeshDataPatch.Apply(_harmony);
                stage = "early compatibility";
                LoadStageJournal.Record("enable: " + stage);
                ThirdPartyCapacityCompatibility.ApplyEarly(_harmony);
                Logging.Info(
                    "Harmony patches applied for " + BuildInfo.Version
                    + " (" + applied + " patch classes). Detailed diagnostics="
                    + SimulationStepDiagnostics.Requested + ".");
                Logging.Info(
                    "Expanded network capacity: nodes="
                    + Capacity.ExpandedNodeCapacity
                    + ", segments=" + Capacity.ExpandedSegmentCapacity
                    + " (kept below 65536 so third-party UInt16 loop counters terminate).");
                LoadStageJournal.Record("enable completed");
            }
            catch (Exception exception)
            {
                Logging.Error("Enable failed at " + stage + ": " + exception);
                LoadStageJournal.Record("enable failed at " + stage + "; before rollback");
                try { RollBack(); }
                catch (Exception rollbackError)
                {
                    Logging.Error("Patch rollback failed; restart required: " + rollbackError);
                }
                LoadStageJournal.Record("enable failed; rollback attempt finished; see error report");
            }
        }

        private static List<Type> GetPatchTypes()
        {
            Assembly assembly = typeof(HarmonyBootstrap).Assembly;
            List<Type> result = new List<Type>();
            foreach (Type type in assembly.GetTypes())
            {
                // Detailed diagnostics have their own owner and are opt-in.
                if (type.Name.StartsWith("SimulationStepDiagnostics", StringComparison.Ordinal))
                    continue;
                if (type.GetCustomAttributes(
                        typeof(HarmonyPatch),
                        true).Length != 0)
                {
                    result.Add(type);
                }
            }

            result.Sort(ComparePatchTypes);
            return result;
        }

        private static int ComparePatchTypes(Type left, Type right)
        {
            int leftPriority = GetPatchPriority(left);
            int rightPriority = GetPatchPriority(right);
            if (leftPriority != rightPriority)
            {
                return leftPriority.CompareTo(rightPriority);
            }

            return string.CompareOrdinal(
                left.FullName,
                right.FullName);
        }

        private static int GetPatchPriority(Type type)
        {
            if (type.FullName
                == "MoreNetworkCapacity.Patches.CoreLoopPatch")
            {
                return 0;
            }

            if (type.FullName
                    == "MoreNetworkCapacity.Patches.CheckLimitsPatch"
                || type.FullName
                    == "MoreNetworkCapacity.Patches.NetManagerColorUpdatePatch")
            {
                return 1;
            }

            return 2;
        }

    }
}
