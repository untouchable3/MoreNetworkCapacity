using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch]
    internal static class NetworkColorOffsetPatch
    {
        private static bool _finalPassApplied;
        private static bool _lateFinalPassApplied;
        private static bool _postLoadFinalPassApplied;
        private static bool _adaptiveRoadsFinalPassApplied;

        internal static bool LatePassPending { get { return !_lateFinalPassApplied; } }

        private static readonly string[,] Targets =
        {
            { "NetManager", "UpdateColorMap" },
            { "NetManager", "UpdateNodeRenderer" },
            { "NetNode", "RenderInstance" },
            { "NetNode", "RefreshEndData" },
            { "NetNode", "RefreshEndFenceData" },
            { "NetNode", "RefreshBendData" },
            { "NetNode", "RefreshBendFenceData" },
            { "NetNode", "RefreshJunctionData" },
            { "NetNode", "PopulateGroupData" },
            { "NetSegment", "RenderInstance" },
            { "NetSegment", "RefreshRoadFence" },
            { "NetSegment", "PopulateGroupData" },
            { "EventManager", "UpdateColorMap" },
            { "EventManager", "GetEventRouteRoadColorIndex" }
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Assembly assembly = typeof(NetManager).Assembly;
            for (int targetIndex = 0;
                targetIndex < Targets.GetLength(0);
                ++targetIndex)
            {
                Type type = assembly.GetType(
                    Targets[targetIndex, 0],
                    false);
                if (type == null)
                {
                    Logging.Warning(
                        "Could not find color-offset target type "
                        + Targets[targetIndex, 0]
                        + ".");
                    continue;
                }

                bool found = false;
                foreach (MethodInfo method in
                    AccessTools.GetDeclaredMethods(type))
                {
                    if (method.Name != Targets[targetIndex, 1])
                    {
                        continue;
                    }

                    found = true;
                    yield return method;
                }

                if (!found)
                {
                    Logging.Warning(
                        "Could not find color-offset target "
                        + Targets[targetIndex, 0]
                        + "."
                        + Targets[targetIndex, 1]
                        + ".");
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo nodeBase = AccessTools.Method(
                typeof(Capacity),
                "GetNodeColorIndexBase",
                null,
                null);
            MethodInfo eventBase = AccessTools.Method(
                typeof(Capacity),
                "GetEventColorIndexBase",
                null,
                null);
            if (nodeBase == null || eventBase == null)
            {
                Logging.Error(
                    "Network color-offset helpers are unavailable; "
                    + "leaving "
                    + GetLabel(original)
                    + " unchanged.");
                return list;
            }

            int nodeReplacements = 0;
            int eventReplacements = 0;
            for (int i = 0; i < list.Count; ++i)
            {
                CodeInstruction instruction = list[i];
                if (IsInt32Constant(
                    instruction,
                    Capacity.VanillaNodeColorIndexBase))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = nodeBase;
                    ++nodeReplacements;
                }
                else if (IsInt32Constant(
                    instruction,
                    Capacity.VanillaEventColorIndexBase))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = eventBase;
                    ++eventReplacements;
                }
            }

            if (nodeReplacements != 0 || eventReplacements != 0)
            {
                Logging.Info(
                    "Network color offsets patched in "
                    + GetLabel(original)
                    + ": nodes="
                    + nodeReplacements
                    + ", events="
                    + eventReplacements
                    + ".");
            }

            return list;
        }

        // A third-party transpiler can inject a fresh copy of the vanilla
        // 86016 node-color base after the initial MNC transpiler has run.
        // Apply this pass after all compatibility patches are installed.
        internal static void ApplyFinalPass(Harmony harmony)
        {
            if (_finalPassApplied || harmony == null)
            {
                return;
            }

            MethodInfo transpiler = AccessTools.Method(
                typeof(NetworkColorOffsetPatch),
                "FinalNodeBaseTranspiler",
                null,
                null);
            if (transpiler == null)
            {
                Logging.Error(
                    "Could not find the final network node-color "
                    + "offset transpiler.");
                return;
            }

            HarmonyMethod finalPatch = new HarmonyMethod(transpiler);
            finalPatch.priority = Priority.Last;

            int patched = 0;
            foreach (MethodBase target in TargetMethods())
            {
                harmony.Patch(
                    target,
                    null,
                    null,
                    finalPatch,
                    null);
                ++patched;
            }

            _finalPassApplied = patched != 0;
            Logging.Info(
                "Installed final network node-color offset pass for "
                + patched
                + " methods.");
        }

        // AdaptiveRoads can rebuild its Harmony pipeline after the level-load
        // compatibility pass. Re-append this transpiler once on the first
        // simulation step so it runs after that late pipeline rebuild.
        internal static void ReapplyFinalPass(Harmony harmony)
        {
            if (_lateFinalPassApplied || harmony == null)
            {
                return;
            }

            MethodInfo transpiler = AccessTools.Method(
                typeof(NetworkColorOffsetPatch),
                "LateFinalNodeBaseTranspiler",
                null,
                null);
            if (transpiler == null)
            {
                Logging.Error(
                    "Could not find the late network node-color "
                    + "offset transpiler.");
                return;
            }

            HarmonyMethod latePatch = new HarmonyMethod(transpiler);
            latePatch.priority = Priority.Last;

            int patched = 0;
            foreach (MethodBase target in TargetMethods())
            {
                try
                {
                    harmony.Patch(
                        target,
                        null,
                        null,
                        latePatch,
                        null);
                    ++patched;
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "Late network node-color pass skipped for "
                        + GetLabel(target)
                        + ": "
                        + exception.GetType().Name
                        + ".");
                }
            }

            _lateFinalPassApplied = patched != 0;
            Logging.Info(
                "Reapplied late network node-color offset pass for "
                + patched
                + " methods.");
        }

        // Node Controller can install rendering transpilers after startup
        // Harmony is ready. This pass is installed from OnLevelLoaded, after
        // all known compatibility passes have completed for the loaded map.
        internal static void ApplyPostLoadFinalPass(Harmony harmony)
        {
            if (_postLoadFinalPassApplied || harmony == null)
            {
                return;
            }

            MethodInfo transpiler = AccessTools.Method(
                typeof(NetworkColorOffsetPatch),
                "PostLoadFinalNodeBaseTranspiler",
                null,
                null);
            if (transpiler == null)
            {
                Logging.Error(
                    "Could not find the post-load network node-color "
                    + "offset transpiler.");
                return;
            }

            HarmonyMethod postLoadPatch = new HarmonyMethod(transpiler);
            postLoadPatch.priority = Priority.Last;

            int patched = 0;
            foreach (MethodBase target in TargetMethods())
            {
                try
                {
                    harmony.Patch(
                        target,
                        null,
                        null,
                        postLoadPatch,
                        null);
                    ++patched;
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "Post-load network node-color pass skipped for "
                        + GetLabel(target)
                        + ": "
                        + exception.GetType().Name
                        + ".");
                }
            }

            _postLoadFinalPassApplied = patched != 0;
            Logging.Info(
                "Installed post-load network node-color offset pass for "
                + patched
                + " methods.");
        }

        internal static void ResetFinalPass()
        {
            _finalPassApplied = false;
            _lateFinalPassApplied = false;
            _postLoadFinalPassApplied = false;
            _adaptiveRoadsFinalPassApplied = false;
        }

        // AdaptiveRoads installs its in-game transpilers from LifeCycle.Load,
        // which can happen after LoadingWrapper.OnLevelLoaded.  A pass
        // installed from that lifecycle callback is the first one that is
        // actually after AdaptiveRoads' render pipeline.
        internal static void ApplyAfterAdaptiveRoadsLoad(Harmony harmony)
        {
            if (_adaptiveRoadsFinalPassApplied || harmony == null)
            {
                return;
            }

            MethodInfo transpiler = AccessTools.Method(
                typeof(NetworkColorOffsetPatch),
                "AdaptiveRoadsFinalNodeBaseTranspiler",
                null,
                null);
            if (transpiler == null)
            {
                Logging.Error(
                    "Could not find the AdaptiveRoads-final network "
                    + "node-color offset transpiler.");
                return;
            }

            HarmonyMethod finalPatch = new HarmonyMethod(transpiler);
            finalPatch.priority = Priority.Last;

            int patched = 0;
            foreach (MethodBase target in TargetMethods())
            {
                try
                {
                    harmony.Patch(
                        target,
                        null,
                        null,
                        finalPatch,
                        null);
                    ++patched;
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "AdaptiveRoads-final network node-color pass skipped "
                        + "for "
                        + GetLabel(target)
                        + ": "
                        + exception.GetType().Name
                        + ".");
                }
            }

            _adaptiveRoadsFinalPassApplied = patched != 0;
            Logging.Info(
                "Installed AdaptiveRoads-final network node-color offset "
                + "pass for "
                + patched
                + " methods.");
        }

        private static IEnumerable<CodeInstruction> FinalNodeBaseTranspiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeBases(
                original,
                instructions,
                "Final network node-color offsets patched in ");
        }

        // Keep this as a distinct Harmony method. Harmony identifies patch
        // methods by owner and method, so registering the original final
        // transpiler a second time does not create a new chain position.
        private static IEnumerable<CodeInstruction> LateFinalNodeBaseTranspiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeBases(
                original,
                instructions,
                "Late final network node-color offsets patched in ");
        }

        private static IEnumerable<CodeInstruction> PostLoadFinalNodeBaseTranspiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeBases(
                original,
                instructions,
                "Post-load final network node-color offsets patched in ");
        }

        private static IEnumerable<CodeInstruction>
            AdaptiveRoadsFinalNodeBaseTranspiler(
                MethodBase original,
                IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceNodeBases(
                original,
                instructions,
                "AdaptiveRoads-final network node-color offsets patched in ");
        }

        private static IEnumerable<CodeInstruction> ReplaceNodeBases(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions,
            string logPrefix)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo nodeBase = AccessTools.Method(
                typeof(Capacity),
                "GetNodeColorIndexBase",
                null,
                null);
            if (nodeBase == null)
            {
                return list;
            }

            int replacements = 0;
            for (int i = 0; i < list.Count; ++i)
            {
                if (!IsInt32Constant(
                        list[i],
                        Capacity.VanillaNodeColorIndexBase))
                {
                    continue;
                }

                list[i].opcode = OpCodes.Call;
                list[i].operand = nodeBase;
                ++replacements;
            }

            if (replacements != 0)
            {
                Logging.Info(
                    logPrefix
                    + GetLabel(original)
                    + ": "
                    + replacements
                    + ".");
            }

            return list;
        }

        private static string GetLabel(MethodBase method)
        {
            return method.DeclaringType.FullName
                + "."
                + method.Name;
        }

        private static bool IsInt32Constant(
            CodeInstruction instruction,
            int value)
        {
            return (instruction.opcode == OpCodes.Ldc_I4
                    || instruction.opcode == OpCodes.Ldc_I4_S)
                && instruction.operand is int
                && (int)instruction.operand == value;
        }
    }
}
