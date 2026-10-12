using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager), "UpdateColorMap")]
    internal static class SimulationStepDiagnosticsColorPatch
    {
        private static void Prefix()
        {
            SimulationStepDiagnostics.RecordColorUpdateEnter();
        }

        private static void Postfix()
        {
            SimulationStepDiagnostics.RecordColorUpdateExit();
        }
    }

    [HarmonyPatch(typeof(SimulationManager), "SimulationStep")]
    internal static class SimulationStepDiagnosticsFramePatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            SimulationStepDiagnostics.RecordFrameEnter();
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix()
        {
            SimulationStepDiagnostics.RecordFrameExit();
        }
    }

    [HarmonyPatch(typeof(SimulationManager), "RegisterManager")]
    internal static class SimulationStepDiagnosticsRegistrationPatch
    {
        private static void Postfix(UnityEngine.Object manager)
        {
            SimulationStepDiagnostics.RecordManagerRegistered(manager);
        }
    }

    [HarmonyPatch(typeof(SimulationManager), "FixedUpdate")]
    internal static class SimulationStepDiagnosticsFixedUpdatePatch
    {
        private static void Prefix()
        {
            SimulationStepDiagnostics.RecordFixedUpdate();
        }
    }

    [HarmonyPatch(typeof(SimulationManager), "SimulationThread")]
    internal static class SimulationStepDiagnosticsThreadPatch
    {
        private static void Prefix()
        {
            SimulationStepDiagnostics.RecordSimulationThreadEnter();
        }
    }

    [HarmonyPatch(typeof(AsyncTask), "Execute")]
    internal static class SimulationStepDiagnosticsAsyncTaskPatch
    {
        private static void Prefix(AsyncTask __instance)
        {
            SimulationStepDiagnostics.RecordAsyncTaskEnter(__instance);
        }

        private static void Postfix(AsyncTask __instance)
        {
            SimulationStepDiagnostics.RecordAsyncTaskExit(__instance);
        }
    }
}
