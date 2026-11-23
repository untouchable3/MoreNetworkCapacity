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

    [HarmonyPatch]
    internal static class SimulationStepDiagnosticsAsyncTaskPatch
    {
        private static System.Reflection.MethodBase[] TargetMethods()
        {
            return new System.Reflection.MethodBase[]
            {
                AccessTools.DeclaredMethod(typeof(AsyncTask), "Execute", null, null),
                AccessTools.DeclaredMethod(typeof(AsyncAction), "Execute", null, null)
            };
        }

        private static void Prefix(AsyncTaskBase __instance)
        {
            SimulationStepDiagnostics.RecordAsyncTaskEnter(__instance);
        }

        private static void Postfix(AsyncTaskBase __instance)
        {
            SimulationStepDiagnostics.RecordAsyncTaskExit(__instance);
        }
    }
}
