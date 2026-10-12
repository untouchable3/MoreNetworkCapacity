using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class SimulationStepDiagnostics
    {
        internal const string HarmonyId = "com.codex.csl.more-network-capacity.diagnostics";
        internal static readonly bool Requested = string.Equals(
            Environment.GetEnvironmentVariable("MNC_DIAGNOSTICS"),
            "1", StringComparison.Ordinal);
        private const int CapturedFrameCount = 3;
        private const int FirstFrameWarningSeconds = 20;
        private const int WaitForFirstFrameSeconds = 30;
        private const int RepeatReportSeconds = 10;
        private const int MaximumReports = 8;
        private const int HistoryLimit = 512;
        private const int TraceLinesInReport = 48;
        private const int LoadingStateSampleMilliseconds = 1000;

        private sealed class ActiveStep
        {
            public string Name;
            public DateTime StartedUtc;
            public int SubStep;
        }

        private static readonly object Sync = new object();
        private static readonly object PatchSync = new object();
        private static readonly object FileSync = new object();
        private static readonly List<ActiveStep> ActiveSteps =
            new List<ActiveStep>();
        private static readonly List<ActiveStep> ActiveAsyncTasks =
            new List<ActiveStep>();
        private static readonly List<ActiveStep> ActiveDetails =
            new List<ActiveStep>();
        private static readonly Queue<string> History =
            new Queue<string>();
        private static readonly HashSet<MethodBase> PatchedMethods =
            new HashSet<MethodBase>();
        private static readonly HashSet<string> PatchedTypeNames =
            new HashSet<string>();
        private static readonly Dictionary<int, int> TerrainIteratorStates =
            new Dictionary<int, int>();
        private static readonly FieldInfo AsyncTaskNameField =
            typeof(AsyncTaskBase).GetField(
                "m_Name",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic);

        private static Harmony _harmony;
        private static Thread _watchdog;
        private static string _logPath;
        private static string _managerSnapshot = "unavailable";
        private static SimulationManager _simulationManager;
        private static LoadingManager _loadingManager;
        private static DateTime _monitorStartedUtc;
        private static DateTime _frameStartedUtc;
        private static DateTime _lastReportUtc;
        private static DateTime _simulationThreadStartedUtc;
        private static DateTime _lastFixedUpdateUtc;
        private static int _armed;
        private static int _enabled;
        private static int _stopWatchdog;
        private static int _watchdogGeneration;
        private static int _frameActive;
        private static int _frameNumber;
        private static int _completedFrames;
        private static int _reportCount;
        private static int _colorUpdateCount;
        private static int _asyncTaskCount;
        private static int _fixedUpdateCount;
        private static int _patchedMethodCount;
        private static int _detailPatched;
        private static bool _logFailed;
        private static DateTime _lastLoadingStateSampleUtc;
        private static bool _loadingStatePatched;
        private static MethodBase _loadingManagerUpdate;
        private static MethodBase _loadingQueueLoadingAction;
        private static MethodBase _terrainUpdateData;
        private static MethodBase _terrainIteratorMoveNext;
        private static MethodBase _waterSimulationUpdatePatch;
        private static MethodBase _terrainLateUpdate;
        private static MethodBase _loadingWrapperOnLevelLoaded;
        private static FieldInfo _loadingQueueField;
        private static FieldInfo _loadingHasQueuedActionsField;
        private static MethodInfo _loadingQueuePeekMethod;
        private static FieldInfo _terrainIteratorIndexField;
        private static FieldInfo _terrainIteratorStateField;
        private static DateTime _lastQueueStateUtc;
        private static int _lastQueueCount = -1;
        private static string _lastQueueHeadType;
        private static bool _lastQueueHasQueuedActions;
        private static int _terrainUpdateActive;
        private static int _terrainUpdateEnqueueCount;
        private static DateTime _terrainUpdateStartedUtc;

        internal static bool IsEnabled
        {
            get
            {
                return Interlocked.CompareExchange(
                    ref _enabled,
                    0,
                    0) != 0;
            }
        }

        internal static bool IsArmed
        {
            get
            {
                return Interlocked.CompareExchange(
                    ref _armed,
                    0,
                    0) != 0;
            }
        }

        internal static void InstallEarly(Harmony harmony)
        {
            if (!Requested || harmony == null)
            {
                return;
            }

            if (_harmony == null)
            {
                _harmony = new Harmony(HarmonyId);
                Type[] framePatches = {
                    typeof(Patches.SimulationStepDiagnosticsFramePatch),
                    typeof(Patches.SimulationStepDiagnosticsRegistrationPatch),
                    typeof(Patches.SimulationStepDiagnosticsFixedUpdatePatch),
                    typeof(Patches.SimulationStepDiagnosticsThreadPatch),
                    typeof(Patches.SimulationStepDiagnosticsAsyncTaskPatch),
                    typeof(Patches.SimulationStepDiagnosticsColorPatch)
                };
                for (int i = 0; i < framePatches.Length; ++i)
                    _harmony.CreateClassProcessor(framePatches[i]).Patch();
            }
            bool firstInstall =
                Interlocked.CompareExchange(ref _armed, 1, 0) == 0;
            int patchedMethods = PatchAllManagerMethods();
            _patchedMethodCount += patchedMethods;
            int patchedDetails = PatchNetworkDetailMethods();
            if (firstInstall)
            {
                Logging.Info(
                    "Simulation diagnostics armed before the simulation "
                    + "thread starts (patched manager methods="
                    + patchedMethods
                    + ", network detail methods="
                    + patchedDetails
                    + ").");
            }
        }

        internal static void InitializeEarly()
        {
            if (!IsArmed || _harmony == null)
            {
                return;
            }

            InitializeLog();
            PatchLoadingStateMethods();
            WriteLine("EARLY DIAGNOSTICS " + BuildInfo.Version);
        }

        internal static void Stop()
        {
            Interlocked.Exchange(ref _armed, 0);
            Interlocked.Exchange(ref _enabled, 0);
            Interlocked.Exchange(ref _stopWatchdog, 1);
            Interlocked.Increment(ref _watchdogGeneration);
            if (_watchdog != null && _watchdog != Thread.CurrentThread)
                _watchdog.Join(1500);
            _watchdog = null;
            if (_harmony != null)
            {
                _harmony.UnpatchAll(HarmonyId);
                _harmony = null;
            }
            lock (Sync)
            {
                ActiveSteps.Clear();
                ActiveDetails.Clear();
                ActiveAsyncTasks.Clear();
                History.Clear();
                TerrainIteratorStates.Clear();
                _frameActive = 0;
                _completedFrames = 0;
                _frameNumber = 0;
            }
            PatchedMethods.Clear();
            PatchedTypeNames.Clear();
            _patchedMethodCount = 0;
            _detailPatched = 0;
            _loadingStatePatched = false;
            _lastLoadingStateSampleUtc = DateTime.MinValue;
            _simulationManager = null;
            _loadingManager = null;
        }

        internal static void BeginMonitoring(
            Harmony harmony,
            NetManager manager)
        {
            InstallEarly(harmony);
            if (!IsArmed
                || Interlocked.CompareExchange(ref _enabled, 1, 0) != 0)
            {
                return;
            }

            _simulationManager = Singleton<SimulationManager>.instance;
            _loadingManager = Singleton<LoadingManager>.instance;
            _monitorStartedUtc = DateTime.UtcNow;
            _lastReportUtc = DateTime.MinValue;
            if (_frameActive == 0)
            {
                _frameStartedUtc = DateTime.MinValue;
                _frameNumber = 0;
            }

            _completedFrames = 0;
            _reportCount = 0;
            _colorUpdateCount = 0;
            _asyncTaskCount = 0;
            _fixedUpdateCount = 0;
            _logFailed = false;
            _stopWatchdog = 0;
            _lastQueueStateUtc = DateTime.MinValue;
            _lastQueueCount = -1;
            _lastQueueHeadType = null;
            _lastQueueHasQueuedActions = false;

            lock (Sync)
            {
                if (_frameActive == 0)
                {
                    ActiveSteps.Clear();
                    ActiveAsyncTasks.Clear();
                    ActiveDetails.Clear();
                    History.Clear();
                }
            }

            InitializeLog();
            int patchedMethods = PatchAllManagerMethods();
            _patchedMethodCount += patchedMethods;
            _managerSnapshot = BuildRegisteredManagerSnapshot();
            int nodeLength = manager == null
                || manager.m_nodes == null
                ? -1
                : manager.m_nodes.m_buffer.Length;
            int segmentLength = manager == null
                || manager.m_segments == null
                ? -1
                : manager.m_segments.m_buffer.Length;

            WriteLine(
                "BEGIN " + BuildInfo.Version
                + " | nodes=" + nodeLength
                + " | segments=" + segmentLength
                + " | loopLimits=" + Capacity.NodeLoopLimit
                + "/" + Capacity.SegmentLoopLimit
                + " | patchedManagerMethods=" + _patchedMethodCount
                + " | currentFrameActive=" + (_frameActive != 0)
                + " | currentFrameElapsed="
                + (_frameActive != 0
                    ? FormatSeconds(
                        GetElapsedSeconds(_frameStartedUtc))
                    : "0.000s"));
            WriteLine("REGISTERED " + _managerSnapshot);
            WriteTerrainState();

            if (_logFailed || string.IsNullOrEmpty(_logPath))
            {
                Logging.Warning(
                    "Simulation diagnostics could not create their log file. "
                    + "Only the normal game log will contain the startup marker.");
            }
            else
            {
                Logging.Info(
                    "Simulation diagnostics enabled for the expanded save. "
                    + "Log: "
                    + _logPath
                    + " (patched manager methods="
                    + _patchedMethodCount
                    + ").");
            }

            int generation = Interlocked.Increment(ref _watchdogGeneration);
            _watchdog = new Thread(delegate() { WatchdogLoop(generation); });
            _watchdog.IsBackground = true;
            _watchdog.Name = "MoreNetworkCapacity-SimulationWatchdog";
            _watchdog.Start();
        }

        private static void PatchLoadingStateMethods()
        {
            if (_loadingStatePatched)
            {
                return;
            }

            _loadingStatePatched = true;
            TryPatchLoadingMethod(
                typeof(LoadingManager),
                "Update",
                Type.EmptyTypes,
                "LoadingManager.Update");
            TryPatchLoadingMethod(
                typeof(LoadingManager),
                "QueueLoadingAction",
                new[] { typeof(System.Collections.IEnumerator) },
                "LoadingManager.QueueLoadingAction");
            TryPatchLoadingMethod(
                typeof(TerrainManager),
                "UpdateData",
                new[] { typeof(SimulationManager.UpdateMode) },
                "TerrainManager.UpdateData");
            TryPatchLoadingMethod(
                typeof(TerrainManager),
                "LateUpdateData",
                new[] { typeof(SimulationManager.UpdateMode) },
                "TerrainManager.LateUpdateData");
            TryPatchLoadingMethod(
                typeof(LoadingWrapper),
                "OnLevelLoaded",
                new[] { typeof(SimulationManager.UpdateMode) },
                "LoadingWrapper.OnLevelLoaded");
            TryPatchTerrainIterator();
            TryPatchWaterSimulation();
            WriteLine(
                "LOADING PATCHES | loadingManager="
                + (_loadingManagerUpdate != null)
                + " queue="
                + (_loadingQueueLoadingAction != null)
                + " terrainUpdateData="
                + (_terrainUpdateData != null)
                + " terrainIterator="
                + (_terrainIteratorMoveNext != null)
                + " water="
                + (_waterSimulationUpdatePatch != null)
                + " terrain="
                + (_terrainLateUpdate != null)
                + " wrapper="
                + (_loadingWrapperOnLevelLoaded != null));
        }

        private static void TryPatchLoadingMethod(
            Type type,
            string name,
            Type[] parameters,
            string label)
        {
            MethodBase method;
            method = FindDeclaredMethod(type, name, parameters);
            if (method == null)
            {
                Logging.Warning("Could not find loading diagnostic target " + label);
                return;
            }

            try
            {
                Logging.Info(
                    "Resolved loading diagnostic target "
                    + label
                    + " as "
                    + GetMethodLabel(method)
                    + " | parameters="
                    + method.GetParameters().Length
                    + " | token="
                    + method.MetadataToken);
                string prefixName = name == "QueueLoadingAction"
                    ? "QueueLoadingActionPrefix"
                    : (name == "UpdateData"
                        ? "TerrainUpdateDataPrefix"
                        : "LoadingPrefix");
                string postfixName = name == "QueueLoadingAction"
                    ? "QueueLoadingActionPostfix"
                    : (name == "UpdateData"
                        ? "TerrainUpdateDataPostfix"
                        : "LoadingPostfix");
                MethodInfo prefix = typeof(SimulationStepDiagnostics).GetMethod(
                    prefixName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(SimulationStepDiagnostics).GetMethod(
                    postfixName,
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = name == "UpdateData"
                    ? typeof(SimulationStepDiagnostics).GetMethod(
                        "TerrainUpdateDataFinalizer",
                        BindingFlags.Static | BindingFlags.NonPublic)
                    : null;
                _harmony.Patch(
                    method,
                    new HarmonyMethod(prefix),
                    new HarmonyMethod(postfix),
                    null,
                    finalizer == null
                        ? null
                        : new HarmonyMethod(finalizer));
                if (type == typeof(LoadingManager))
                {
                    if (name == "Update")
                    {
                        _loadingManagerUpdate = method;
                    }
                    else if (name == "QueueLoadingAction")
                    {
                        _loadingQueueLoadingAction = method;
                    }
                }
                else if (type == typeof(TerrainManager))
                {
                    if (name == "UpdateData")
                    {
                        _terrainUpdateData = method;
                    }
                    else
                    {
                        _terrainLateUpdate = method;
                    }
                }
                else
                {
                    _loadingWrapperOnLevelLoaded = method;
                }
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "Could not attach loading diagnostics to "
                    + label
                    + ": "
                    + exception.Message);
            }
        }

        private static MethodBase FindDeclaredMethod(
            Type type,
            string name,
            Type[] parameters)
        {
            Type current = type;
            while (current != null)
            {
                try
                {
                    MethodInfo declared = AccessTools.DeclaredMethod(
                        current,
                        name,
                        parameters,
                        null);
                    if (declared != null)
                    {
                        return declared;
                    }
                }
                catch
                {
                }

                try
                {
                    List<MethodInfo> methods =
                        AccessTools.GetDeclaredMethods(current);
                    for (int i = 0; i < methods.Count; ++i)
                    {
                        MethodInfo method = methods[i];
                        if (method.Name != name)
                        {
                            continue;
                        }

                        ParameterInfo[] methodParameters =
                            method.GetParameters();
                        if (methodParameters.Length != parameters.Length)
                        {
                            continue;
                        }

                        bool matches = true;
                        for (int parameterIndex = 0;
                            parameterIndex < parameters.Length;
                            ++parameterIndex)
                        {
                            if (methodParameters[parameterIndex].ParameterType
                                != parameters[parameterIndex])
                            {
                                matches = false;
                                break;
                            }
                        }

                        if (matches)
                        {
                            return method;
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    MethodInfo method = current.GetMethod(
                        name,
                        BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly,
                        null,
                        parameters,
                        null);
                    if (method != null)
                    {
                        return method;
                    }
                }
                catch (AmbiguousMatchException)
                {
                    MethodInfo[] methods = current.GetMethods(
                        BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < methods.Length; ++i)
                    {
                        MethodInfo method = methods[i];
                        if (method.Name != name)
                        {
                            continue;
                        }

                        ParameterInfo[] methodParameters =
                            method.GetParameters();
                        if (methodParameters.Length != parameters.Length)
                        {
                            continue;
                        }

                        bool matches = true;
                        for (int parameterIndex = 0;
                            parameterIndex < parameters.Length;
                            ++parameterIndex)
                        {
                            if (methodParameters[parameterIndex].ParameterType
                                != parameters[parameterIndex])
                            {
                                matches = false;
                                break;
                            }
                        }

                        if (matches)
                        {
                            return method;
                        }
                    }
                }
                catch
                {
                }

                current = current.BaseType;
            }

            return null;
        }

        private static void TryPatchTerrainIterator()
        {
            Type[] nestedTypes = typeof(TerrainManager).GetNestedTypes(
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            for (int i = 0; i < nestedTypes.Length; ++i)
            {
                Type nestedType = nestedTypes[i];
                if (nestedType.Name.IndexOf(
                        "UpdateTerrainPatch",
                        StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                MethodBase moveNext = AccessTools.Method(
                    nestedType,
                    "MoveNext",
                    Type.EmptyTypes,
                    null);
                if (moveNext == null)
                {
                    continue;
                }

                _terrainIteratorIndexField = AccessTools.Field(
                    nestedType,
                    "index");
                _terrainIteratorStateField = AccessTools.Field(
                    nestedType,
                    "$PC");
                if (_terrainIteratorIndexField == null
                    || _terrainIteratorStateField == null)
                {
                    Logging.Warning(
                        "Terrain iterator fields were not found on "
                        + nestedType.FullName);
                    return;
                }

                try
                {
                    MethodInfo prefix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            "TerrainIteratorPrefix",
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    MethodInfo postfix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            "TerrainIteratorPostfix",
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    _harmony.Patch(
                        moveNext,
                        new HarmonyMethod(prefix),
                        new HarmonyMethod(postfix),
                        null,
                        null);
                    _terrainIteratorMoveNext = moveNext;
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "Could not attach terrain iterator diagnostics: "
                        + exception.Message);
                }

                return;
            }

            Logging.Warning("Could not find TerrainManager.UpdateTerrainPatch iterator");
        }

        private static void TryPatchWaterSimulation()
        {
            MethodBase method = AccessTools.Method(
                typeof(WaterSimulation),
                "UpdatePatch",
                new[] { typeof(int), typeof(int) },
                null);
            if (method == null)
            {
                Logging.Warning(
                    "Could not find WaterSimulation.UpdatePatch diagnostic target");
                return;
            }

            try
            {
                MethodInfo prefix = typeof(SimulationStepDiagnostics).GetMethod(
                    "WaterPatchPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postfix = typeof(SimulationStepDiagnostics).GetMethod(
                    "WaterPatchPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo finalizer = typeof(SimulationStepDiagnostics).GetMethod(
                    "WaterPatchFinalizer",
                    BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(
                    method,
                    new HarmonyMethod(prefix),
                    new HarmonyMethod(postfix),
                    null,
                    new HarmonyMethod(finalizer));
                _waterSimulationUpdatePatch = method;
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "Could not attach water patch diagnostics: "
                    + exception.Message);
            }
        }

        private static void LoadingPrefix(MethodBase __originalMethod)
        {
            WriteLoadingState("ENTER " + GetMethodLabel(__originalMethod));
        }

        private static void LoadingPostfix(MethodBase __originalMethod)
        {
            WriteLoadingState("EXIT " + GetMethodLabel(__originalMethod));
        }

        private static void QueueLoadingActionPrefix(
            System.Collections.IEnumerator __0)
        {
            if (__0 == null)
            {
                WriteLine("QUEUE action=null");
                return;
            }

            string actionType = __0.GetType().FullName;
            if (actionType.IndexOf(
                    "TerrainManager+<UpdateTerrainPatch>",
                    StringComparison.Ordinal) >= 0)
            {
                int terrainIndex;
                if (TryReadTerrainIteratorIndex(__0, out terrainIndex))
                {
                    if (Interlocked.CompareExchange(
                            ref _terrainUpdateActive,
                            0,
                            0) != 0)
                    {
                        Interlocked.Increment(
                            ref _terrainUpdateEnqueueCount);
                    }

                    WriteLine(
                        "QUEUE terrain-enqueue | index="
                        + terrainIndex
                        + " | before="
                        + ReadLoadingQueueCount());
                }
                else
                {
                    WriteLine(
                        "QUEUE terrain-enqueue | index=unavailable"
                        + " | before="
                        + ReadLoadingQueueCount());
                }
                return;
            }

            if (actionType.IndexOf(
                    "RenderManager+<UpdateRenderGroup>",
                    StringComparison.Ordinal) < 0)
            {
                WriteLine(
                    "QUEUE enqueue | before="
                    + ReadLoadingQueueCount()
                    + " | action="
                    + actionType);
            }
        }

        private static void QueueLoadingActionPostfix()
        {
            RecordQueueState("QUEUE enqueue-complete");
        }

        private static void TerrainUpdateDataPrefix(
            TerrainManager __instance,
            SimulationManager.UpdateMode __0)
        {
            _terrainUpdateStartedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _terrainUpdateEnqueueCount, 0);
            Interlocked.Exchange(ref _terrainUpdateActive, 1);
            WriteLine(
                "TERRAIN_UPDATE_DATA enter | mode="
                + __0
                + " | "
                + DescribeTerrainBuffers(__instance));
        }

        private static void TerrainUpdateDataPostfix(
            TerrainManager __instance,
            SimulationManager.UpdateMode __0)
        {
            int enqueued = Interlocked.Exchange(
                ref _terrainUpdateEnqueueCount,
                0);
            Interlocked.Exchange(ref _terrainUpdateActive, 0);
            WriteLine(
                "TERRAIN_UPDATE_DATA exit | mode="
                + __0
                + " | terrainEnqueues="
                + enqueued
                + " | elapsed="
                + FormatSeconds(
                    (_terrainUpdateStartedUtc == DateTime.MinValue
                        ? 0.0
                        : (DateTime.UtcNow - _terrainUpdateStartedUtc)
                            .TotalSeconds))
                + " | "
                + DescribeTerrainBuffers(__instance));
        }

        private static Exception TerrainUpdateDataFinalizer(
            Exception __exception,
            TerrainManager __instance,
            SimulationManager.UpdateMode __0)
        {
            if (__exception == null)
            {
                return null;
            }

            int enqueued = Interlocked.Exchange(
                ref _terrainUpdateEnqueueCount,
                0);
            Interlocked.Exchange(ref _terrainUpdateActive, 0);
            WriteLine(
                "TERRAIN_UPDATE_DATA exception | mode="
                + __0
                + " | terrainEnqueues="
                + enqueued
                + " | exception="
                + __exception.GetType().FullName
                + " | message="
                + __exception.Message
                + " | details="
                + __exception.ToString().Replace(
                    Environment.NewLine,
                    " <- ")
                + " | "
                + DescribeTerrainBuffers(__instance));
            return __exception;
        }

        private static string DescribeTerrainBuffers(TerrainManager terrain)
        {
            if (terrain == null)
            {
                return "terrain=null";
            }

            try
            {
                Array patches = ReadTerrainArray(terrain, "m_patches");
                Array heights = ReadTerrainArray(terrain, "m_blockHeights");
                Array heights2 = ReadTerrainArray(terrain, "m_blockHeights2");
                return "lastUpdatedPatch="
                    + ReadTerrainInt(terrain, "m_lastUpdatedPatch")
                    + " | terrainPatches="
                    + (patches == null ? -1 : patches.Length)
                    + " | blockHeights="
                    + (heights == null ? -1 : heights.Length)
                    + "/"
                    + (heights2 == null ? -1 : heights2.Length);
            }
            catch (Exception exception)
            {
                return "terrain-state-error="
                    + exception.GetType().Name;
            }
        }

        private static Array ReadTerrainArray(
            TerrainManager terrain,
            string fieldName)
        {
            FieldInfo field = AccessTools.Field(
                typeof(TerrainManager),
                fieldName);
            return field == null
                ? null
                : field.GetValue(terrain) as Array;
        }

        private static int ReadTerrainInt(
            TerrainManager terrain,
            string fieldName)
        {
            FieldInfo field = AccessTools.Field(
                typeof(TerrainManager),
                fieldName);
            return field == null
                ? -1
                : Convert.ToInt32(field.GetValue(terrain));
        }

        private static void RecordQueueState(string eventName)
        {
            DateTime now = DateTime.UtcNow;
            if (_lastQueueStateUtc != DateTime.MinValue
                && (now - _lastQueueStateUtc).TotalMilliseconds < 250.0)
            {
                return;
            }

            int count = ReadLoadingQueueCount();
            string headType = ReadLoadingQueueHeadType();
            bool hasQueuedActions = ReadLoadingHasQueuedActions();
            if (count == _lastQueueCount
                && string.Equals(
                    headType,
                    _lastQueueHeadType,
                    StringComparison.Ordinal)
                && hasQueuedActions == _lastQueueHasQueuedActions
                && _lastQueueStateUtc != DateTime.MinValue)
            {
                return;
            }

            _lastQueueStateUtc = now;
            _lastQueueCount = count;
            _lastQueueHeadType = headType;
            _lastQueueHasQueuedActions = hasQueuedActions;
            WriteLine(
                eventName
                + " | count="
                + count
                + " | head="
                + (headType ?? "<empty>")
                + " | hasQueuedActions="
                + hasQueuedActions);
        }

        private static int ReadLoadingQueueCount()
        {
            try
            {
                LoadingManager loading = Singleton<LoadingManager>.instance;
                if (loading == null)
                {
                    return -1;
                }

                if (_loadingQueueField == null)
                {
                    _loadingQueueField = AccessTools.Field(
                        typeof(LoadingManager),
                        "m_mainThreadQueue");
                }

                object queue = _loadingQueueField == null
                    ? null
                    : _loadingQueueField.GetValue(loading);
                PropertyInfo count = queue == null
                    ? null
                    : queue.GetType().GetProperty("Count");
                return count == null
                    ? -1
                    : (int)count.GetValue(queue, null);
            }
            catch
            {
                return -1;
            }
        }

        private static string ReadLoadingQueueHeadType()
        {
            try
            {
                LoadingManager loading = Singleton<LoadingManager>.instance;
                if (loading == null)
                {
                    return "<loading-null>";
                }

                if (_loadingQueueField == null)
                {
                    _loadingQueueField = AccessTools.Field(
                        typeof(LoadingManager),
                        "m_mainThreadQueue");
                }

                object queue = _loadingQueueField == null
                    ? null
                    : _loadingQueueField.GetValue(loading);
                if (queue == null)
                {
                    return "<queue-null>";
                }

                if (_loadingQueuePeekMethod == null)
                {
                    _loadingQueuePeekMethod = queue.GetType().GetMethod(
                        "Peek",
                        BindingFlags.Instance
                        | BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null);
                }

                if (_loadingQueuePeekMethod == null)
                {
                    return "<peek-unavailable>";
                }

                object action = _loadingQueuePeekMethod.Invoke(queue, null);
                return action == null
                    ? "<empty>"
                    : action.GetType().FullName;
            }
            catch (Exception exception)
            {
                return "<queue-error:" + exception.GetType().Name + ">";
            }
        }

        private static bool TryReadTerrainIteratorIndex(
            object instance,
            out int index)
        {
            index = -1;
            if (instance == null)
            {
                return false;
            }

            try
            {
                FieldInfo indexField = _terrainIteratorIndexField;
                if (indexField == null)
                {
                    indexField = AccessTools.Field(
                        instance.GetType(),
                        "index");
                }

                if (indexField == null)
                {
                    return false;
                }

                index = (int)indexField.GetValue(instance);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ReadLoadingHasQueuedActions()
        {
            try
            {
                LoadingManager loading = Singleton<LoadingManager>.instance;
                if (loading == null)
                {
                    return false;
                }

                if (_loadingHasQueuedActionsField == null)
                {
                    _loadingHasQueuedActionsField = AccessTools.Field(
                        typeof(LoadingManager),
                        "m_hasQueuedActions");
                }

                return _loadingHasQueuedActionsField != null
                    && (bool)_loadingHasQueuedActionsField.GetValue(loading);
            }
            catch
            {
                return false;
            }
        }

        private static void TerrainIteratorPrefix(object __instance)
        {
            int index;
            int state;
            if (!TryReadTerrainIteratorState(__instance, out index, out state)
                || !IsInterestingTerrainPatch(index))
            {
                return;
            }

            bool changed = false;
            lock (Sync)
            {
                int previous;
                if (!TerrainIteratorStates.TryGetValue(index, out previous)
                    || previous != state)
                {
                    TerrainIteratorStates[index] = state;
                    changed = true;
                }
            }

            if (changed)
            {
                WriteLine(
                    "TERRAIN_ITER enter | index="
                    + index
                    + " | state="
                    + state
                    + " | queue="
                    + ReadLoadingQueueCount());
            }
        }

        private static void TerrainIteratorPostfix(
            object __instance,
            bool __result)
        {
            int index;
            int state;
            if (!TryReadTerrainIteratorState(__instance, out index, out state)
                || !IsInterestingTerrainPatch(index)
                || __result)
            {
                return;
            }

            WriteLine(
                "TERRAIN_ITER complete | index="
                + index
                + " | state="
                + state);
        }

        private static bool TryReadTerrainIteratorState(
            object instance,
            out int index,
            out int state)
        {
            index = -1;
            state = -1;
            if (instance == null
                || _terrainIteratorIndexField == null
                || _terrainIteratorStateField == null)
            {
                return false;
            }

            try
            {
                index = (int)_terrainIteratorIndexField.GetValue(instance);
                state = (int)_terrainIteratorStateField.GetValue(instance);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInterestingTerrainPatch(int index)
        {
            return index == 54 || index == 55 || index == 80;
        }

        private static void WaterPatchPrefix(int x, int z)
        {
            int index = x + z * 9;
            if (!IsInterestingTerrainPatch(index))
            {
                return;
            }

            WriteLine(
                "WATER_PATCH enter | index="
                + index
                + " | x="
                + x
                + " | z="
                + z);
        }

        private static void WaterPatchPostfix(int x, int z)
        {
            int index = x + z * 9;
            if (!IsInterestingTerrainPatch(index))
            {
                return;
            }

            WriteLine(
                "WATER_PATCH exit | index="
                + index
                + " | exception="
                + "none");
        }

        private static Exception WaterPatchFinalizer(
            Exception __exception,
            int x,
            int z)
        {
            if (__exception == null)
            {
                return null;
            }

            int index = x + z * 9;
            if (IsInterestingTerrainPatch(index))
            {
                WriteLine(
                    "WATER_PATCH exception | index="
                    + index
                    + " | exception="
                    + __exception.GetType().FullName
                    + " | message="
                    + __exception.Message);
            }

            return __exception;
        }

        private static void WriteLoadingState(string eventName)
        {
            DateTime now = DateTime.UtcNow;
            bool force = eventName.IndexOf("OnLevelLoaded", StringComparison.Ordinal)
                >= 0;
            if (!force
                && _lastLoadingStateSampleUtc != DateTime.MinValue
                && (now - _lastLoadingStateSampleUtc).TotalMilliseconds
                    < LoadingStateSampleMilliseconds)
            {
                return;
            }

            _lastLoadingStateSampleUtc = now;
            try
            {
                LoadingManager loading = Singleton<LoadingManager>.instance;
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                StringBuilder line = new StringBuilder();
                line.Append("LOADSTATE ");
                line.Append(eventName);
                line.Append(" | ");
                AppendLoadingField(line, loading, "m_loadingComplete");
                AppendLoadingField(line, loading, "m_sceneProgress");
                AppendLoadingField(line, loading, "m_simulationProgress");
                AppendLoadingField(line, loading, "m_currentCoroutine");
                AppendLoadingField(line, loading, "m_loadingProfilerMain");
                AppendLoadingField(line, loading, "m_loadingProfilerSimulation");
                AppendLoadingField(line, terrain, "m_lastUpdatedPatch");
                WriteLine(line.ToString());
            }
            catch (Exception exception)
            {
                WriteLine(
                    "LOADSTATE error="
                    + exception.GetType().Name
                    + ":"
                    + exception.Message);
            }
        }

        private static void AppendLoadingField(
            StringBuilder builder,
            object instance,
            string fieldName)
        {
            builder.Append(fieldName);
            builder.Append("=");
            if (instance == null)
            {
                builder.Append("instance-null");
                return;
            }

            try
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), fieldName);
                object value = field == null ? null : field.GetValue(instance);
                if (value == null)
                {
                    builder.Append(field == null ? "unavailable" : "null");
                    return;
                }

                LoadingProfiler profiler = value as LoadingProfiler;
                if (profiler != null)
                {
                    AppendProfiler(builder, profiler);
                    return;
                }

                builder.Append(Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                builder.Append("error:");
                builder.Append(exception.GetType().Name);
            }
        }

        private static void AppendProfiler(
            StringBuilder builder,
            LoadingProfiler profiler)
        {
            try
            {
                FieldInfo active = AccessTools.Field(
                    typeof(LoadingProfiler),
                    "m_currentLoadingEvent");
                object activeValue = active == null
                    ? null
                    : active.GetValue(profiler);
                builder.Append("active=");
                builder.Append(activeValue == null ? "null" : activeValue);
            }
            catch (Exception exception)
            {
                builder.Append("error:");
                builder.Append(exception.GetType().Name);
            }
        }

        private static void WriteTerrainState()
        {
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            if (terrain == null)
            {
                WriteLine("TERRAIN manager=null");
                return;
            }

            try
            {
                FieldInfo lastPatch = AccessTools.Field(
                    typeof(TerrainManager),
                    "m_lastUpdatedPatch");
                FieldInfo patches = AccessTools.Field(
                    typeof(TerrainManager),
                    "m_patches");
                FieldInfo updateLock = AccessTools.Field(
                    typeof(TerrainManager),
                    "m_updateLock");
                Array patchArray = patches == null
                    ? null
                    : patches.GetValue(terrain) as Array;
                object lockValue = updateLock == null
                    ? null
                    : updateLock.GetValue(terrain);

                WriteLine(
                    "TERRAIN lastUpdatedPatch="
                    + (lastPatch == null
                        ? "unavailable"
                        : lastPatch.GetValue(terrain))
                    + " patches="
                    + (patchArray == null
                        ? "null"
                        : patchArray.Length.ToString())
                    + " updateLock="
                    + (lockValue == null ? "null" : "set"));
            }
            catch (Exception exception)
            {
                WriteLine(
                    "TERRAIN stateError="
                    + exception.GetType().Name
                    + ":"
                    + exception.Message);
            }
        }

        internal static void RecordFrameEnter()
        {
            if (!IsArmed)
            {
                return;
            }

            lock (Sync)
            {
                if (_completedFrames >= CapturedFrameCount)
                {
                    return;
                }

                if (_frameActive != 0)
                {
                    return;
                }

                _frameActive = 1;
                ++_frameNumber;
                _frameStartedUtc = DateTime.UtcNow;
                AddHistoryLocked(
                    "FRAME " + _frameNumber + " ENTER");
            }
        }

        internal static void RecordFrameExit()
        {
            if (!IsArmed)
            {
                return;
            }

            int completedFrames;
            int frameNumber;
            double elapsedSeconds;
            lock (Sync)
            {
                if (_frameActive == 0)
                {
                    return;
                }

                completedFrames = ++_completedFrames;
                frameNumber = _frameNumber;
                elapsedSeconds = GetElapsedSeconds(_frameStartedUtc);
                _frameActive = 0;
                ActiveDetails.Clear();
                AddHistoryLocked(
                    "FRAME " + frameNumber
                    + " EXIT "
                    + FormatSeconds(elapsedSeconds));
            }

            if (completedFrames <= CapturedFrameCount)
            {
                WriteLine(
                    "FRAME " + frameNumber
                    + " COMPLETE | elapsed="
                    + FormatSeconds(elapsedSeconds)
                    + " | colorUpdates="
                    + _colorUpdateCount);
            }

            if (completedFrames == CapturedFrameCount)
            {
                WriteLine(
                    "CAPTURE COMPLETE | completedFrames="
                    + completedFrames);
                Stop();
            }
        }

        internal static void RecordManagerEnter(
            MethodBase method,
            int subStep)
        {
            if (!IsArmed || method == null)
            {
                return;
            }

            string name = GetMethodLabel(method);
            lock (Sync)
            {
                if (_frameActive == 0)
                {
                    return;
                }

                ActiveStep step = new ActiveStep();
                step.Name = name;
                step.StartedUtc = DateTime.UtcNow;
                step.SubStep = subStep;
                ActiveSteps.Add(step);
                AddHistoryLocked(
                    "  ENTER " + name + " subStep=" + subStep);
            }
        }

        internal static void RecordManagerExit(MethodBase method)
        {
            if (!IsArmed || method == null)
            {
                return;
            }

            string name = GetMethodLabel(method);
            double elapsedSeconds = 0.0;
            lock (Sync)
            {
                for (int i = ActiveSteps.Count - 1; i >= 0; --i)
                {
                    if (ActiveSteps[i].Name != name)
                    {
                        continue;
                    }

                    elapsedSeconds =
                        GetElapsedSeconds(ActiveSteps[i].StartedUtc);
                    ActiveSteps.RemoveAt(i);
                    break;
                }

                AddHistoryLocked(
                    "  EXIT  " + name
                    + " "
                    + FormatSeconds(elapsedSeconds));
            }
        }

        internal static void RecordDetailEnter(
            MethodBase method,
            int itemId)
        {
            if (!IsArmed || method == null)
            {
                return;
            }

            string name = GetMethodLabel(method);
            lock (Sync)
            {
                if (_frameActive == 0)
                {
                    return;
                }

                ActiveStep step = new ActiveStep();
                step.Name = name;
                step.StartedUtc = DateTime.UtcNow;
                step.SubStep = itemId;
                ActiveDetails.Add(step);
            }
        }

        internal static void RecordDetailExit(MethodBase method)
        {
            if (!IsArmed || method == null)
            {
                return;
            }

            string name = GetMethodLabel(method);
            lock (Sync)
            {
                for (int i = ActiveDetails.Count - 1; i >= 0; --i)
                {
                    if (ActiveDetails[i].Name != name)
                    {
                        continue;
                    }

                    ActiveDetails.RemoveAt(i);
                    break;
                }
            }
        }

        internal static void RecordColorUpdateEnter()
        {
            if (!IsEnabled)
            {
                return;
            }

            lock (Sync)
            {
                if (_completedFrames >= CapturedFrameCount)
                {
                    return;
                }

                AddHistoryLocked("  COLOR UpdateColorMap ENTER");
            }
        }

        internal static void RecordColorUpdateExit()
        {
            if (!IsEnabled)
            {
                return;
            }

            lock (Sync)
            {
                if (_completedFrames >= CapturedFrameCount)
                {
                    return;
                }

                int count = ++_colorUpdateCount;
                AddHistoryLocked(
                    "  COLOR UpdateColorMap EXIT count=" + count);
            }
        }

        internal static void RecordManagerRegistered(
            UnityEngine.Object manager)
        {
            if (!IsArmed || manager == null)
            {
                return;
            }

            TryPatchManagerType(manager.GetType());
        }

        internal static void RecordSimulationThreadEnter()
        {
            if (!IsArmed)
            {
                return;
            }

            lock (Sync)
            {
                _simulationThreadStartedUtc = DateTime.UtcNow;
                AddHistoryLocked("SIMULATION THREAD START");
            }
        }

        internal static void RecordFixedUpdate()
        {
            if (!IsArmed)
            {
                return;
            }

            lock (Sync)
            {
                ++_fixedUpdateCount;
                _lastFixedUpdateUtc = DateTime.UtcNow;
            }
        }

        internal static void RecordAsyncTaskEnter(AsyncTaskBase task)
        {
            if (!IsArmed || task == null)
            {
                return;
            }

            string name = GetAsyncTaskName(task);
            lock (Sync)
            {
                if (_frameActive == 0)
                {
                    return;
                }

                ActiveStep step = new ActiveStep();
                step.Name = name;
                step.StartedUtc = DateTime.UtcNow;
                ActiveAsyncTasks.Add(step);
                ++_asyncTaskCount;
                AddHistoryLocked("  ASYNC ENTER " + name);
            }
        }

        internal static void RecordAsyncTaskExit(AsyncTaskBase task)
        {
            if (!IsArmed || task == null)
            {
                return;
            }

            string name = GetAsyncTaskName(task);
            double elapsedSeconds = 0.0;
            lock (Sync)
            {
                for (int i = ActiveAsyncTasks.Count - 1; i >= 0; --i)
                {
                    if (ActiveAsyncTasks[i].Name != name)
                    {
                        continue;
                    }

                    elapsedSeconds =
                        GetElapsedSeconds(
                            ActiveAsyncTasks[i].StartedUtc);
                    ActiveAsyncTasks.RemoveAt(i);
                    break;
                }

                AddHistoryLocked(
                    "  ASYNC EXIT  " + name
                    + " "
                    + FormatSeconds(elapsedSeconds));
            }
        }

        private static void WatchdogLoop(int generation)
        {
            while (Interlocked.CompareExchange(
                    ref _stopWatchdog,
                    0,
                    0) == 0
                && generation == Interlocked.CompareExchange(ref _watchdogGeneration, 0, 0))
            {
                Thread.Sleep(1000);

                if (!IsEnabled
                    || generation != Interlocked.CompareExchange(ref _watchdogGeneration, 0, 0))
                {
                    return;
                }

                RecordQueueState("QUEUE watchdog");

                string report = null;
                lock (Sync)
                {
                    DateTime now = DateTime.UtcNow;
                    bool frameActive = _frameActive != 0;
                    double relevantElapsed = frameActive
                        ? GetElapsedSeconds(_frameStartedUtc)
                        : GetElapsedSeconds(_monitorStartedUtc);
                    int threshold = frameActive
                        ? FirstFrameWarningSeconds
                        : WaitForFirstFrameSeconds;
                    if (relevantElapsed < threshold
                        || _reportCount >= MaximumReports
                        || (_lastReportUtc != DateTime.MinValue
                            && GetElapsedSeconds(_lastReportUtc)
                                < RepeatReportSeconds))
                    {
                        continue;
                    }

                    report = BuildWatchdogReportLocked(
                        now,
                        frameActive,
                        relevantElapsed);
                    _lastReportUtc = now;
                    ++_reportCount;
                }

                WriteLine(report);
            }
        }

        private static string BuildWatchdogReportLocked(
            DateTime now,
            bool frameActive,
            double elapsedSeconds)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("WATCHDOG | ");
            if (frameActive)
            {
                builder.Append("simulation frame ");
                builder.Append(_frameNumber);
                builder.Append(" has not completed for ");
            }
            else
            {
                builder.Append(
                    "SimulationManager.SimulationStep has not entered for ");
            }

            builder.Append(FormatSeconds(elapsedSeconds));
            builder.Append(" | completedFrames=");
            builder.Append(_completedFrames);
            builder.Append(" | colorUpdates=");
            builder.Append(_colorUpdateCount);
            builder.Append(" | fixedUpdates=");
            builder.Append(_fixedUpdateCount);
            builder.Append(" | asyncTasks=");
            builder.Append(_asyncTaskCount);

            if (ActiveSteps.Count == 0)
            {
                if (frameActive)
                {
                    builder.Append(
                        " | NO ACTIVE ISimulationManager "
                        + "(the main thread is between manager calls, "
                        + "before the manager loop, or inside native code)");
                }
            }
            else
            {
                ActiveStep current = ActiveSteps[ActiveSteps.Count - 1];
                builder.Append(" | CURRENT ");
                builder.Append(current.Name);
                builder.Append(" subStep=");
                builder.Append(current.SubStep);
                builder.Append(" entered=");
                builder.Append(
                    FormatSeconds(
                        GetElapsedSeconds(current.StartedUtc)));
                if (ActiveSteps.Count > 1)
                {
                    builder.Append(" | activeDepth=");
                    builder.Append(ActiveSteps.Count);
                }
            }

            if (ActiveAsyncTasks.Count > 0)
            {
                ActiveStep currentTask =
                    ActiveAsyncTasks[ActiveAsyncTasks.Count - 1];
                builder.Append(" | CURRENT ASYNC ");
                builder.Append(currentTask.Name);
                builder.Append(" entered=");
                builder.Append(
                    FormatSeconds(
                        GetElapsedSeconds(currentTask.StartedUtc)));
                if (ActiveAsyncTasks.Count > 1)
                {
                    builder.Append(" | asyncDepth=");
                    builder.Append(ActiveAsyncTasks.Count);
                }
            }

            if (ActiveDetails.Count > 0)
            {
                ActiveStep detail =
                    ActiveDetails[ActiveDetails.Count - 1];
                builder.Append(" | DETAIL ");
                builder.Append(detail.Name);
                if (detail.SubStep >= 0)
                {
                    builder.Append(" id=");
                    builder.Append(detail.SubStep);
                }
                builder.Append(" entered=");
                builder.Append(
                    FormatSeconds(
                        GetElapsedSeconds(detail.StartedUtc)));
                if (ActiveDetails.Count > 1)
                {
                    builder.Append(" | detailDepth=");
                    builder.Append(ActiveDetails.Count);
                }
            }

            AppendRuntimeStateLocked(builder);
            AppendNetworkStateLocked(builder);
            AppendSimulationStackLocked(builder);

            builder.Append(Environment.NewLine);
            builder.Append("REGISTERED ");
            builder.Append(_managerSnapshot);
            builder.Append(Environment.NewLine);
            builder.Append("RECENT TRACE:");

            string[] trace = History.ToArray();
            int start = Math.Max(
                0,
                trace.Length - TraceLinesInReport);
            for (int i = start; i < trace.Length; ++i)
            {
                builder.Append(Environment.NewLine);
                builder.Append("  ");
                builder.Append(trace[i]);
            }

            return builder.ToString();
        }

        private static void AppendRuntimeStateLocked(
            StringBuilder builder)
        {
            builder.Append(Environment.NewLine);
            builder.Append("RUNTIME");

            if (_simulationManager == null)
            {
                builder.Append(" simulationManager=null");
            }
            else
            {
                AppendField(
                    builder,
                    _simulationManager,
                    "m_updateCounter");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_maxFramesBehind");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_inSimulationStep");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_hasActions");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_simulationSpeed");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_simulationPaused");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_forcedSimulationPause");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_currentFrameIndex");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_referenceFrameIndex");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_simulationThread");
                AppendField(
                    builder,
                    _simulationManager,
                    "m_simulationActions");
            }

            if (_loadingManager == null)
            {
                builder.Append(" loadingManager=null");
            }
            else
            {
                AppendField(
                    builder,
                    _loadingManager,
                    "m_currentlyLoading");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_renderDataReady");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_metaDataLoaded");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_simulationDataLoaded");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_loadingComplete");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_sceneProgress");
                AppendField(
                    builder,
                    _loadingManager,
                    "m_simulationProgress");
            }
        }

        private static void AppendNetworkStateLocked(
            StringBuilder builder)
        {
            builder.Append(Environment.NewLine);
            builder.Append("NETSTATE");

            NetManager manager = Singleton<NetManager>.instance;
            if (manager == null)
            {
                builder.Append(" manager=null");
                return;
            }

            AppendArrayFieldState(
                builder,
                manager,
                "m_updatedNodes",
                "updatedNodes");
            AppendArrayFieldState(
                builder,
                manager,
                "m_updatedSegments",
                "updatedSegments");
            AppendArrayFieldState(
                builder,
                manager,
                "m_adjustedSegments",
                "adjustedSegments");
            AppendField(builder, manager, "m_nodesUpdated");
            AppendField(builder, manager, "m_segmentsUpdated");
            AppendField(builder, manager, "m_wetnessChanged");
            AppendField(builder, manager, "m_updateNameVisibility");

            try
            {
                builder.Append(" pendingNodeBits=");
                builder.Append(
                    NetManagerCustomizer.CountPendingNodeUpdates());
                builder.Append(" pendingSegmentBits=");
                builder.Append(
                    NetManagerCustomizer.CountPendingSegmentUpdates());
                builder.Append(" highCreatedNodes=");
                builder.Append(
                    NetManagerCustomizer.CountHighNodeIds(manager));
                builder.Append(" highCreatedSegments=");
                builder.Append(
                    NetManagerCustomizer.CountHighSegmentIds(manager));
            }
            catch (Exception exception)
            {
                builder.Append(" networkCounts=error:");
                builder.Append(exception.Message);
            }
        }

        private static void AppendArrayFieldState(
            StringBuilder builder,
            object instance,
            string fieldName,
            string label)
        {
            try
            {
                FieldInfo field = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                Array array = field == null
                    ? null
                    : field.GetValue(instance) as Array;
                builder.Append(" ");
                builder.Append(label);
                builder.Append("Length=");
                builder.Append(array == null ? -1 : array.Length);

                ulong[] bits = array as ulong[];
                builder.Append(" ");
                builder.Append(label);
                builder.Append("Bits=");
                builder.Append(CountSetBits(bits));
            }
            catch (Exception exception)
            {
                builder.Append(" ");
                builder.Append(label);
                builder.Append("=error:");
                builder.Append(exception.Message);
            }
        }

        private static void AppendSimulationStackLocked(
            StringBuilder builder)
        {
            builder.Append(Environment.NewLine);
            builder.Append("SIMSTACK");

            if (_simulationManager == null)
            {
                builder.Append(" simulationManager=null");
                return;
            }

            try
            {
                FieldInfo threadField =
                    typeof(SimulationManager).GetField(
                        "m_simulationThread",
                        BindingFlags.Instance
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                Thread simulationThread = threadField == null
                    ? null
                    : threadField.GetValue(_simulationManager) as Thread;
                if (simulationThread == null)
                {
                    builder.Append(" thread=null");
                    return;
                }

                builder.Append(" thread=");
                builder.Append(simulationThread.Name);
                builder.Append(" alive=");
                builder.Append(simulationThread.IsAlive);
                builder.Append(" state=");
                builder.Append(simulationThread.ThreadState);

                if (!simulationThread.IsAlive)
                {
                    return;
                }

                // This Mono runtime does not implement
                // StackTrace(Thread, bool) ("the requested feature is not
                // implemented"), so another thread's managed stack cannot be
                // captured. The network detail patches record the live call
                // chain instead, innermost frame first.
                builder.Append(" managedStack=unsupported");
                for (int i = ActiveDetails.Count - 1; i >= 0; --i)
                {
                    ActiveStep detail = ActiveDetails[i];
                    builder.Append(Environment.NewLine);
                    builder.Append("  #");
                    builder.Append(ActiveDetails.Count - 1 - i);
                    builder.Append(" ");
                    builder.Append(detail.Name);
                    if (detail.SubStep >= 0)
                    {
                        builder.Append(" id=");
                        builder.Append(detail.SubStep);
                    }
                    builder.Append(" entered=");
                    builder.Append(
                        FormatSeconds(
                            GetElapsedSeconds(detail.StartedUtc)));
                }
            }
            catch (Exception exception)
            {
                builder.Append(" stack=error:");
                builder.Append(exception.Message);
            }
        }

        private static int CountSetBits(ulong[] bits)
        {
            if (bits == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < bits.Length; ++i)
            {
                ulong value = bits[i];
                while (value != 0)
                {
                    value &= value - 1;
                    ++count;
                }
            }

            return count;
        }

        private static void AppendField(
            StringBuilder builder,
            object instance,
            string fieldName)
        {
            try
            {
                FieldInfo field = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                if (field == null)
                {
                    builder.Append(" ");
                    builder.Append(fieldName);
                    builder.Append("=missing");
                    return;
                }

                object value = field.GetValue(instance);
                builder.Append(" ");
                builder.Append(fieldName);
                builder.Append("=");
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                Thread thread = value as Thread;
                if (thread != null)
                {
                    builder.Append("alive:");
                    builder.Append(thread.IsAlive);
                    builder.Append(",state:");
                    builder.Append(thread.ThreadState);
                    return;
                }

                System.Collections.ICollection collection =
                    value as System.Collections.ICollection;
                if (collection != null)
                {
                    builder.Append("count:");
                    builder.Append(collection.Count);
                    return;
                }

                builder.Append(Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                builder.Append(" ");
                builder.Append(fieldName);
                builder.Append("=error:");
                builder.Append(exception.Message);
            }
        }

        private static void InitializeLog()
        {
            try
            {
                string root = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(root))
                {
                    root = Path.GetTempPath();
                }

                string directory = Path.Combine(
                    Path.Combine(
                        Path.Combine(
                            root,
                            "Colossal Order"),
                        "Cities_Skylines"),
                    "MoreNetworkCapacity");
                Directory.CreateDirectory(directory);
                _logPath = Path.Combine(
                    directory,
                    "simulation-step-diagnostics.log");
                File.WriteAllText(
                    _logPath,
                    "MoreNetworkCapacity simulation-step diagnostics"
                    + Environment.NewLine);
            }
            catch (Exception exception)
            {
                _logFailed = true;
                _logPath = null;
                Logging.Warning(
                    "Could not initialize the simulation diagnostics log: "
                    + exception.Message);
            }
        }

        private static void WriteLine(string message)
        {
            if (string.IsNullOrEmpty(_logPath) || _logFailed)
            {
                return;
            }

            string line = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture)
                + " | "
                + message
                + Environment.NewLine;
            lock (FileSync)
            {
                try
                {
                    File.AppendAllText(_logPath, line);
                }
                catch
                {
                    _logFailed = true;
                }
            }
        }

        private static int PatchNetworkDetailMethods()
        {
            if (Interlocked.CompareExchange(
                    ref _detailPatched,
                    1,
                    0) != 0)
            {
                return 0;
            }

            int patched = 0;
            patched += PatchDetailMethod(
                typeof(NetManager),
                "SimulationStepImpl",
                new Type[] { typeof(int) },
                2);
            patched += PatchDetailMethod(
                typeof(NetNode),
                "CalculateNode",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetNode),
                "UpdateNode",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetNode),
                "UpdateBounds",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetNode),
                "UpdateLaneConnection",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetManager),
                "UpdateNodeFlags",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetManager),
                "UpdateNodeRenderer",
                new Type[] { typeof(ushort), typeof(bool) },
                1);
            patched += PatchDetailMethod(
                typeof(NetSegment),
                "CalculateSegment",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetSegment),
                "UpdateSegment",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetSegment),
                "UpdateBounds",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetSegment),
                "UpdateLanes",
                new Type[] { typeof(ushort), typeof(bool) },
                1);
            patched += PatchDetailMethod(
                typeof(NetManager),
                "UpdateSegmentFlags",
                new Type[] { typeof(ushort) },
                1);
            patched += PatchDetailMethod(
                typeof(NetManager),
                "UpdateSegmentRenderer",
                new Type[] { typeof(ushort), typeof(bool) },
                1);
            patched += PatchDetailMethod(
                typeof(PathVisualizer),
                "SimulationStep",
                new Type[] { typeof(int) },
                2);
            patched += PatchDetailMethod(
                typeof(NetAdjust),
                "SimulationStep",
                new Type[] { typeof(int) },
                2);

            Logging.Info(
                "Simulation network detail diagnostics patched "
                + patched
                + " methods.");
            return patched;
        }

        private static int PatchDetailMethod(
            Type type,
            string name,
            Type[] parameters,
            int argumentMode)
        {
            MethodBase method = AccessTools.Method(
                type,
                name,
                parameters,
                null);
            if (method == null)
            {
                Logging.Warning(
                    "Could not find network detail diagnostic target "
                    + GetTypeName(type)
                    + "."
                    + name
                    + ".");
                return 0;
            }

            return TryPatchDetailMethod(method, argumentMode);
        }

        private static int TryPatchDetailMethod(
            MethodBase method,
            int argumentMode)
        {
            if (method == null || _harmony == null)
            {
                return 0;
            }

            lock (PatchSync)
            {
                if (PatchedMethods.Contains(method))
                {
                    return 0;
                }

                PatchedMethods.Add(method);
                try
                {
                    string prefixName = argumentMode == 1
                        ? "DetailIdPrefix"
                        : (argumentMode == 2
                            ? "DetailIntPrefix"
                            : "DetailPrefix");
                    MethodInfo prefix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            prefixName,
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    MethodInfo postfix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            "DetailPostfix",
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    _harmony.Patch(
                        method,
                        new HarmonyMethod(prefix),
                        new HarmonyMethod(postfix),
                        null,
                        null);
                    return 1;
                }
                catch (Exception exception)
                {
                    PatchedMethods.Remove(method);
                    Logging.Warning(
                        "Could not attach network detail diagnostics to "
                        + GetMethodLabel(method)
                        + ": "
                        + exception.Message);
                    return 0;
                }
            }
        }

        private static int PatchAllManagerMethods()
        {
            int patched = 0;
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0;
                assemblyIndex < assemblies.Length;
                ++assemblyIndex)
            {
                Type[] types = GetLoadableTypes(
                    assemblies[assemblyIndex]);
                for (int typeIndex = 0;
                    typeIndex < types.Length;
                    ++typeIndex)
                {
                    Type type = types[typeIndex];
                    if (type == null || type.IsInterface)
                    {
                        continue;
                    }

                    patched += TryPatchManagerType(type);
                }
            }

            return patched;
        }

        private static int TryPatchManagerType(Type type)
        {
            if (type == null || type.IsInterface)
            {
                return 0;
            }

            Type managerInterface;
            try
            {
                managerInterface =
                    GetGameSimulationManagerInterface(type);
            }
            catch
            {
                return 0;
            }

            if (managerInterface == null)
            {
                return 0;
            }

            int patched = 0;
            try
            {
                InterfaceMapping map = type.GetInterfaceMap(
                    managerInterface);
                for (int i = 0;
                    i < map.InterfaceMethods.Length;
                    ++i)
                {
                    if (map.InterfaceMethods[i].Name
                        != "SimulationStep"
                        || map.InterfaceMethods[i]
                            .GetParameters().Length != 1)
                    {
                        continue;
                    }

                    if (TryPatchManagerMethod(map.TargetMethods[i]))
                    {
                        ++patched;
                    }
                }
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "Could not inspect simulation manager "
                    + GetTypeName(type)
                    + ": "
                    + exception.Message);
            }

            return patched;
        }

        private static bool TryPatchManagerMethod(MethodBase method)
        {
            if (method == null || _harmony == null)
            {
                return false;
            }

            lock (PatchSync)
            {
                if (PatchedMethods.Contains(method))
                {
                    return false;
                }

                PatchedMethods.Add(method);
                try
                {
                    MethodInfo prefix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            "ManagerPrefix",
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    MethodInfo postfix = typeof(SimulationStepDiagnostics)
                        .GetMethod(
                            "ManagerPostfix",
                            BindingFlags.Static
                            | BindingFlags.NonPublic);
                    _harmony.Patch(
                        method,
                        new HarmonyMethod(prefix),
                        new HarmonyMethod(postfix),
                        null,
                        null);
                    PatchedTypeNames.Add(
                        GetTypeName(method.DeclaringType));
                    return true;
                }
                catch (Exception exception)
                {
                    PatchedMethods.Remove(method);
                    Logging.Warning(
                        "Could not attach simulation diagnostics to "
                        + GetMethodLabel(method)
                        + ": "
                        + exception.Message);
                    return false;
                }
            }
        }

        private static void ManagerPrefix(
            MethodBase __originalMethod,
            int subStep)
        {
            RecordManagerEnter(__originalMethod, subStep);
        }

        private static void ManagerPostfix(MethodBase __originalMethod)
        {
            RecordManagerExit(__originalMethod);
        }

        private static void DetailPrefix(
            MethodBase __originalMethod)
        {
            RecordDetailEnter(__originalMethod, -1);
        }

        private static void DetailIdPrefix(
            MethodBase __originalMethod,
            ushort __0)
        {
            RecordDetailEnter(__originalMethod, __0);
        }

        private static void DetailIntPrefix(
            MethodBase __originalMethod,
            int __0)
        {
            RecordDetailEnter(__originalMethod, __0);
        }

        private static void DetailPostfix(
            MethodBase __originalMethod)
        {
            RecordDetailExit(__originalMethod);
        }

        private static Type GetGameSimulationManagerInterface(Type type)
        {
            if (type == null)
            {
                return null;
            }

            Type[] interfaces = type.GetInterfaces();
            for (int i = 0; i < interfaces.Length; ++i)
            {
                Type candidate = interfaces[i];
                if (candidate.FullName == "ISimulationManager"
                    && candidate.Assembly
                        == typeof(SimulationManager).Assembly)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                List<Type> result = new List<Type>();
                Type[] types = exception.Types;
                for (int i = 0; i < types.Length; ++i)
                {
                    if (types[i] != null)
                    {
                        result.Add(types[i]);
                    }
                }

                return result.ToArray();
            }
            catch
            {
                return new Type[0];
            }
        }

        private static string BuildRegisteredManagerSnapshot()
        {
            try
            {
                FieldInfo managersField = typeof(SimulationManager)
                    .GetField(
                        "m_managers",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (managersField == null)
                {
                    return "m_managers field unavailable";
                }

                object managers = managersField.GetValue(null);
                if (managers == null)
                {
                    return "m_managers is null";
                }

                Type listType = managers.GetType();
                FieldInfo bufferField = listType.GetField(
                    "m_buffer",
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                FieldInfo sizeField = listType.GetField(
                    "m_size",
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                if (bufferField == null || sizeField == null)
                {
                    return "m_managers layout unavailable";
                }

                Array buffer = bufferField.GetValue(managers) as Array;
                int size = (int)sizeField.GetValue(managers);
                if (buffer == null)
                {
                    return "m_managers buffer is null";
                }

                List<string> names = new List<string>();
                for (int i = 0; i < size && i < buffer.Length; ++i)
                {
                    object item = buffer.GetValue(i);
                    if (item == null)
                    {
                        continue;
                    }

                    string name = GetTypeName(item.GetType());
                    if (PatchedTypeNames.Contains(name))
                    {
                        name += "[patched]";
                    }
                    else
                    {
                        name += "[not-patched]";
                    }

                    names.Add(name);
                }

                return string.Join(", ", names.ToArray());
            }
            catch (Exception exception)
            {
                return "m_managers snapshot failed: "
                    + exception.Message;
            }
        }

        private static void AddHistoryLocked(string entry)
        {
            History.Enqueue(entry);
            while (History.Count > HistoryLimit)
            {
                History.Dequeue();
            }
        }

        private static string GetMethodLabel(MethodBase method)
        {
            if (method == null)
            {
                return "<null>";
            }

            return GetTypeName(method.DeclaringType)
                + "."
                + method.Name;
        }

        private static string GetAsyncTaskName(AsyncTaskBase task)
        {
            if (task == null)
            {
                return "<null>";
            }

            try
            {
                if (AsyncTaskNameField != null)
                {
                    object value = AsyncTaskNameField.GetValue(task);
                    string name = value as string;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch
            {
            }

            return GetTypeName(task.GetType());
        }

        private static string GetTypeName(Type type)
        {
            return type == null
                ? "<null>"
                : type.FullName;
        }

        private static double GetElapsedSeconds(DateTime startedUtc)
        {
            return Math.Max(
                0.0,
                (DateTime.UtcNow - startedUtc).TotalSeconds);
        }

        private static string FormatSeconds(double value)
        {
            return value.ToString(
                    "0.000",
                    CultureInfo.InvariantCulture)
                + "s";
        }
    }
}
