using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager), "UpdateColorMap")]
    internal static class NetManagerColorUpdatePatch
    {
        // UpdateColorMap resets the pending ranges to "empty" after each pass.
        // Vanilla resets the minimum to the vanilla pool size; the trace here
        // follows the same idiom with the active loop limits so expanded ids
        // are not scanned on every frame.
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            FieldInfo nodeLimitField = AccessTools.Field(
                typeof(Capacity),
                "NodeLoopLimit");
            FieldInfo segmentLimitField = AccessTools.Field(
                typeof(Capacity),
                "SegmentLoopLimit");
            int replacements = 0;

            for (int i = 0; i + 1 < list.Count; ++i)
            {
                if (!IsAnyInt32Constant(list[i])
                    || list[i + 1].opcode != OpCodes.Stfld)
                {
                    continue;
                }

                FieldInfo field = list[i + 1].operand as FieldInfo;
                if (field == null || !(list[i].operand is int))
                {
                    continue;
                }

                int value = (int)list[i].operand;
                if (value == Capacity.VanillaNodeCapacity
                    && field.Name == "m_colorUpdateMinNode")
                {
                    list[i].opcode = OpCodes.Ldsfld;
                    list[i].operand = nodeLimitField;
                    ++replacements;
                }
                else if (value == Capacity.VanillaSegmentCapacity
                    && field.Name == "m_colorUpdateMinSegment")
                {
                    list[i].opcode = OpCodes.Ldsfld;
                    list[i].operand = segmentLimitField;
                    ++replacements;
                }
            }

            Logging.Info(
                "Color update ranges patched: "
                + replacements);
            return list;
        }

        // Called once after the expanded loop limits become active (either at
        // the end of loading or on the first gameplay frame when activation is
        // deferred). Marks the whole expanded pool dirty so every node and
        // segment gets a valid color-map pixel.
        internal static void RefreshExpandedColors()
        {
            NetManager manager = Singleton<NetManager>.instance;
            if (manager == null)
            {
                return;
            }

            bool nodeExpanded =
                Capacity.NodeLoopLimit > Capacity.VanillaNodeCapacity;
            bool segmentExpanded =
                Capacity.SegmentLoopLimit > Capacity.VanillaSegmentCapacity;
            if (!nodeExpanded && !segmentExpanded)
            {
                return;
            }

            if (nodeExpanded)
            {
                manager.UpdateNodeColors(
                    0,
                    (ushort)(Capacity.NodeLoopLimit - 1));
            }

            if (segmentExpanded)
            {
                manager.UpdateSegmentColors(
                    0,
                    (ushort)(Capacity.SegmentLoopLimit - 1));
            }

            Logging.Info(
                "Queued a full color refresh for the expanded pools: nodes="
                + (nodeExpanded ? Capacity.NodeLoopLimit : 0)
                + ", segments="
                + (segmentExpanded ? Capacity.SegmentLoopLimit : 0)
                + ".");
        }

        internal static IEnumerable<CodeInstruction> ReplaceRefreshMax(
            IEnumerable<CodeInstruction> instructions,
            int vanillaMax,
            string fieldName,
            string helperName,
            string label)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo helper = typeof(Capacity).GetMethod(
                helperName,
                BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            if (helper == null)
            {
                Logging.Error(
                    "Could not find Capacity."
                    + helperName
                    + "(); leaving "
                    + label
                    + " unchanged.");
                return list;
            }

            int replacements = 0;

            for (int i = 0; i + 1 < list.Count; ++i)
            {
                if (!IsInt32Constant(list[i], vanillaMax)
                    || list[i + 1].opcode != OpCodes.Stfld)
                {
                    continue;
                }

                FieldInfo field = list[i + 1].operand as FieldInfo;
                if (field == null || field.Name != fieldName)
                {
                    continue;
                }

                list[i].opcode = OpCodes.Call;
                list[i].operand = helper;
                ++replacements;
            }

            Logging.Info(
                "Color refresh range patched in "
                + label
                + ": "
                + replacements);
            return list;
        }

        private static bool IsAnyInt32Constant(CodeInstruction instruction)
        {
            return (instruction.opcode == OpCodes.Ldc_I4
                    || instruction.opcode == OpCodes.Ldc_I4_S)
                && instruction.operand is int;
        }

        internal static bool IsInt32Constant(
            CodeInstruction instruction,
            int value)
        {
            return IsAnyInt32Constant(instruction)
                && (int)instruction.operand == value;
        }

        // The overloads are resolved explicitly by arity so Harmony never has
        // to guess between UpdateNodeColors(ushort) and the no-argument pool
        // refresh.
        internal static MethodBase FindPoolRefreshMethod(string methodName)
        {
            MethodInfo[] methods = typeof(NetManager).GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; ++i)
            {
                if (methods[i].Name == methodName
                    && methods[i].GetParameters().Length == 0)
                {
                    return methods[i];
                }
            }

            Logging.Error(
                "Could not find the no-argument overload NetManager."
                + methodName
                + "().");
            return null;
        }
    }

    // The no-argument overloads refresh the entire pool, but the game
    // hardcodes the vanilla maxima. Replace them with the active loop limits
    // so forces such as an info-view change also touch the expanded ids.
    [HarmonyPatch]
    internal static class NetManagerUpdateNodeColorsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase target =
                NetManagerColorUpdatePatch.FindPoolRefreshMethod(
                    "UpdateNodeColors");
            if (target != null)
            {
                yield return target;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return NetManagerColorUpdatePatch.ReplaceRefreshMax(
                instructions,
                Capacity.VanillaNodeCapacity - 1,
                "m_colorUpdateMaxNode",
                "GetNodeColorUpdateMax",
                "NetManager.UpdateNodeColors()");
        }
    }

    [HarmonyPatch]
    internal static class NetManagerUpdateSegmentColorsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodBase target =
                NetManagerColorUpdatePatch.FindPoolRefreshMethod(
                    "UpdateSegmentColors");
            if (target != null)
            {
                yield return target;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return NetManagerColorUpdatePatch.ReplaceRefreshMax(
                instructions,
                Capacity.VanillaSegmentCapacity - 1,
                "m_colorUpdateMaxSegment",
                "GetSegmentColorUpdateMax",
                "NetManager.UpdateSegmentColors()");
        }
    }
}
