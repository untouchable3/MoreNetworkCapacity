using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class StaticBufferCompatibility
    {
        private static readonly HashSet<string> PatchedKianCommonsTypes =
            new HashSet<string>();

        internal static void Reset()
        {
            PatchedKianCommonsTypes.Clear();
        }

        internal static void ApplyAll(Harmony harmony)
        {
            NetManager netManager = Singleton<NetManager>.instance;
            if (netManager == null
                || netManager.m_nodes == null
                || netManager.m_segments == null
                || netManager.m_lanes == null
                || netManager.m_nodes.m_buffer == null
                || netManager.m_segments.m_buffer == null
                || netManager.m_lanes.m_buffer == null)
            {
                Logging.Warning(
                    "NetManager buffers are unavailable; "
                    + "third-party static buffer refresh is skipped.");
                return;
            }

            BuildingManager buildingManager =
                Singleton<BuildingManager>.instance;
            Array buildingBuffer = null;
            if (buildingManager != null
                && buildingManager.m_buildings != null)
            {
                buildingBuffer = buildingManager.m_buildings.m_buffer;
            }

            ApplyKianCommonsCompatibility(
                harmony,
                netManager.m_nodes.m_buffer,
                netManager.m_segments.m_buffer,
                netManager.m_lanes.m_buffer);
            RefreshTMPEBuffers(
                netManager.m_nodes.m_buffer,
                netManager.m_segments.m_buffer,
                netManager.m_lanes.m_buffer);
            RefreshMoveItBuffers(
                netManager.m_nodes.m_buffer,
                netManager.m_segments.m_buffer,
                buildingBuffer);
        }

        internal static void ApplyEarly(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();
            int patched = 0;
            for (int i = 0; i < assemblies.Length; ++i)
            {
                Assembly assembly = assemblies[i];
                Type type;
                try
                {
                    type = assembly.GetType(
                        "KianCommons.NetUtil",
                        false);
                }
                catch
                {
                    continue;
                }

                if (type == null)
                {
                    continue;
                }

                PatchKianCommonsLimits(harmony, type);
                ++patched;
            }

            if (patched == 0)
            {
                Logging.Info(
                    "KianCommons was not loaded; early validity-limit "
                    + "compatibility is inactive.");
            }
        }

        private static void ApplyKianCommonsCompatibility(
            Harmony harmony,
            Array nodeBuffer,
            Array segmentBuffer,
            Array laneBuffer)
        {
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();
            int refreshed = 0;
            for (int i = 0; i < assemblies.Length; ++i)
            {
                Assembly assembly = assemblies[i];
                Type type;
                try
                {
                    type = assembly.GetType(
                        "KianCommons.NetUtil",
                        false);
                }
                catch
                {
                    continue;
                }

                if (type == null)
                {
                    continue;
                }

                PatchKianCommonsLimits(harmony, type);

                bool changed = false;
                changed |= SetStaticArray(
                    type,
                    "nodeBuffer_",
                    nodeBuffer);
                changed |= SetStaticArray(
                    type,
                    "segmentBuffer_",
                    segmentBuffer);
                changed |= SetStaticArray(
                    type,
                    "laneBuffer_",
                    laneBuffer);
                if (changed)
                {
                    ++refreshed;
                    Logging.Info(
                        "Refreshed KianCommons static network buffers in "
                        + assembly.GetName().Name
                        + ".");
                }
            }

            if (refreshed == 0)
            {
                Logging.Info(
                    "No stale KianCommons static network buffers found.");
            }
        }

        private static void RefreshTMPEBuffers(
            Array nodeBuffer,
            Array segmentBuffer,
            Array laneBuffer)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    "TrafficManager");
            if (assembly == null)
            {
                Logging.Info(
                    "TM:PE is not loaded; static buffer refresh is inactive.");
                return;
            }

            // TM:PE's extension type initializers capture the game's original
            // arrays once. Refresh those references after pool replacement,
            // before lane-connector and AdaptiveRoads initialization use them.
            int refreshed = 0;
            refreshed += RefreshTMPEBufferField(
                assembly,
                "TrafficManager.Util.Extensions.NetNodeExtensions",
                "_nodeBuffer",
                nodeBuffer);
            refreshed += RefreshTMPEBufferField(
                assembly,
                "TrafficManager.Util.Extensions.NetSegmentExtensions",
                "_segBuffer",
                segmentBuffer);
            refreshed += RefreshTMPEBufferField(
                assembly,
                "TrafficManager.Util.Extensions.NetLaneExtensions",
                "_laneBuffer",
                laneBuffer);

            Logging.Info(
                "Refreshed "
                + refreshed
                + " TM:PE static network buffer fields.");
        }

        private static int RefreshTMPEBufferField(
            Assembly assembly,
            string typeName,
            string fieldName,
            Array value)
        {
            Type type = assembly.GetType(typeName, false);
            if (type == null)
            {
                Logging.Warning(
                    "Could not find TM:PE buffer holder "
                    + typeName
                    + ".");
                return 0;
            }

            return SetStaticArray(type, fieldName, value) ? 1 : 0;
        }

        private static void PatchKianCommonsLimits(
            Harmony harmony,
            Type type)
        {
            string key = type.Assembly.FullName + "|" + type.FullName;
            if (!PatchedKianCommonsTypes.Add(key))
            {
                return;
            }

            if (harmony == null)
            {
                Logging.Warning(
                    "Harmony is unavailable; KianCommons limit "
                    + "compatibility is inactive.");
                return;
            }

            try
            {
                PatchKianCommonsMethod(
                    harmony,
                    type,
                    "IsNodeValid",
                    "NodeLimitTranspiler");
                PatchKianCommonsMethod(
                    harmony,
                    type,
                    "IsSegmentValid",
                    "SegmentLimitTranspiler");
                PatchKianCommonsMethod(
                    harmony,
                    type,
                    "AssertSegmentValid",
                    "SegmentLimitTranspiler");
                Logging.Info(
                    "Patched KianCommons validity limits in "
                    + type.Assembly.GetName().Name
                    + ".");
            }
            catch (Exception exception)
            {
                PatchedKianCommonsTypes.Remove(key);
                Logging.Error(
                    "Failed to patch KianCommons limits in "
                    + type.Assembly.GetName().Name
                    + ": "
                    + exception);
            }
        }

        private static void PatchKianCommonsMethod(
            Harmony harmony,
            Type type,
            string methodName,
            string transpilerName)
        {
            MethodInfo target = null;
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (string.Equals(
                        method.Name,
                        methodName,
                        StringComparison.Ordinal)
                    && parameters.Length == 1
                    && parameters[0].ParameterType
                        == typeof(ushort))
                {
                    target = method;
                    break;
                }
            }

            if (target == null)
            {
                return;
            }

            MethodInfo transpiler = typeof(StaticBufferCompatibility)
                .GetMethod(
                    transpilerName,
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
            harmony.Patch(
                target,
                null,
                null,
                new HarmonyMethod(transpiler),
                null);
        }

        private static void RefreshMoveItBuffers(
            Array nodeBuffer,
            Array segmentBuffer,
            Array buildingBuffer)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    "MoveIt");
            if (assembly == null)
            {
                Logging.Info(
                    "MoveIt is not loaded; static buffer refresh is inactive.");
                return;
            }

            int refreshed = 0;
            refreshed += RefreshMoveItType(
                assembly,
                "MoveIt.MoveItTool",
                nodeBuffer,
                segmentBuffer,
                buildingBuffer);
            refreshed += RefreshMoveItType(
                assembly,
                "MoveIt.Instance",
                nodeBuffer,
                segmentBuffer,
                buildingBuffer);
            refreshed += RefreshMoveItType(
                assembly,
                "MoveIt.Action",
                nodeBuffer,
                segmentBuffer,
                buildingBuffer);

            Logging.Info(
                "Refreshed "
                + refreshed
                + " MoveIt static buffer fields.");
        }

        private static int RefreshMoveItType(
            Assembly assembly,
            string typeName,
            Array nodeBuffer,
            Array segmentBuffer,
            Array buildingBuffer)
        {
            Type type = assembly.GetType(typeName, false);
            if (type == null)
            {
                return 0;
            }

            int changed = 0;
            if (SetStaticArray(type, "nodeBuffer", nodeBuffer))
            {
                ++changed;
            }

            if (SetStaticArray(
                type,
                "segmentBuffer",
                segmentBuffer))
            {
                ++changed;
            }

            if (buildingBuffer != null)
            {
                if (SetStaticArray(
                    type,
                    "buildingBuffer",
                    buildingBuffer))
                {
                    ++changed;
                }
            }

            return changed;
        }

        private static bool SetStaticArray(
            Type type,
            string fieldName,
            Array value)
        {
            try
            {
                FieldInfo field = ThirdPartyCapacityCompatibility.FindField(
                    type,
                    fieldName);
                if (field == null
                    || !field.IsStatic
                    || !field.FieldType.IsArray
                    || value == null
                    || field.FieldType.GetElementType()
                        != value.GetType().GetElementType())
                {
                    return false;
                }

                Array current = field.GetValue(null) as Array;
                if (object.ReferenceEquals(current, value))
                {
                    return false;
                }

                field.SetValue(null, value);
                return true;
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to refresh static buffer "
                    + type.FullName
                    + "."
                    + fieldName
                    + ": "
                    + exception);
                return false;
            }
        }

        public static IEnumerable<CodeInstruction> NodeLimitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceLimit(
                instructions,
                Capacity.VanillaNodeCapacity,
                Capacity.ExpandedNodeCapacity);
        }

        public static IEnumerable<CodeInstruction> SegmentLimitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceLimit(
                instructions,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity);
        }

        private static IEnumerable<CodeInstruction> ReplaceLimit(
            IEnumerable<CodeInstruction> instructions,
            int oldLimit,
            int newLimit)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4
                    && instruction.operand is int
                    && (int)instruction.operand == oldLimit)
                {
                    instruction.operand = newLimit;
                }

                yield return instruction;
            }
        }
    }
}
