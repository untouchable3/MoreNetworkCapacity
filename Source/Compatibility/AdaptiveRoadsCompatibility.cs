using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class AdaptiveRoadsCompatibility
    {
        private const string AssemblyName = "AdaptiveRoads";
        private const string ModName = "AdaptiveRoads";

        private static readonly HashSet<string> PatchedMethods =
            new HashSet<string>();

        private static object _preparedInstance;
        private static bool _createHookInstalled;
        private static bool _deserializeHookInstalled;
        private static bool _lifeCycleLoadHookInstalled;
        private static bool _asymPavementColorPrefixPatched;
        private static bool _waitingLogged;
        private static readonly CompatibilityRetryGate RetryGate =
            new CompatibilityRetryGate(120);
        private static Assembly _assembly;
        private static Type _managerType;
        private static bool _assemblyLoadSubscribed;
        private static bool _runtimePatchesReady;
        private static bool _runtimePatchPassFailed;

        internal static bool RetryPending { get { return RetryGate.Pending; } }

        internal static void Apply(Harmony harmony)
        {
            TryApply(harmony, true);
        }

        internal static void PrepareForNetworkDeserialize(
            Harmony harmony,
            bool expandedSave)
        {
            TryApply(harmony, expandedSave);
        }

        internal static void Retry(Harmony harmony)
        {
            int generation;
            if (RetryGate.TryBegin(out generation))
                TryApply(harmony, true, generation);

            // AdaptiveRoads may install/rebuild rendering patches after the
            // level-load compatibility pass. This is deliberately one-shot;
            // NetworkColorOffsetPatch owns the guard.
            if (NetworkColorOffsetPatch.LatePassPending)
                NetworkColorOffsetPatch.ReapplyFinalPass(harmony);
        }

        internal static void Reset()
        {
            _preparedInstance = null;
            _createHookInstalled = false;
            _deserializeHookInstalled = false;
            _lifeCycleLoadHookInstalled = false;
            _asymPavementColorPrefixPatched = false;
            _waitingLogged = false;
            _assembly = null;
            _managerType = null;
            _runtimePatchesReady = false;
            _runtimePatchPassFailed = false;
            if (_assemblyLoadSubscribed)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
                _assemblyLoadSubscribed = false;
            }
            RetryGate.Reset();
            PatchedMethods.Clear();
        }

        private static void TryApply(
            Harmony harmony,
            bool allowImmediateExpansion)
        {
            RetryGate.Request();
            TryApply(harmony, allowImmediateExpansion, RetryGate.Generation);
        }

        private static void AssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.FullName.StartsWith(
                    AssemblyName + ",", StringComparison.Ordinal))
                RetryGate.Request();
        }

        private static void TryApply(
            Harmony harmony,
            bool allowImmediateExpansion,
            int generation)
        {
            if (!_assemblyLoadSubscribed)
            {
                AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
                _assemblyLoadSubscribed = true;
            }
            Assembly assembly = _assembly;
            if (assembly == null)
                assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly(AssemblyName);
            if (assembly == null)
            {
                if (!_waitingLogged)
                {
                    Logging.Info(
                        "AdaptiveRoads is not loaded yet; compatibility "
                        + "will resume if the assembly is loaded.");
                    _waitingLogged = true;
                }

                RetryGate.Complete(generation);
                return;
            }

            _assembly = assembly;
            Type type = _managerType;
            if (type == null)
                type = ThirdPartyCapacityCompatibility.FindType(
                    assembly, "AdaptiveRoads.Manager.NetworkExtensionManager", ModName);
            if (type == null)
            {
                return;
            }
            _managerType = type;

            EnsureRuntimePatches(harmony, assembly, type);
            EnsureAsymPavementColorPrefix(harmony, assembly);
            EnsureCreateHook(harmony, type);
            EnsureDeserializeHook(harmony, type);
            EnsureLifeCycleLoadHook(harmony, assembly);

            object instance = ArrayCompatibility.GetStaticInstance(
                type,
                "RawInstance",
                "instance_",
                "AdaptiveRoads NetworkExtensionManager");
            if (instance == null)
            {
                if (!_waitingLogged)
                {
                    Logging.Warning(
                        "AdaptiveRoads NetworkExtensionManager is not "
                        + "initialized yet; runtime patches are installed "
                        + "and buffer expansion will run when Create or "
                        + "Awake completes.");
                    _waitingLogged = true;
                }

                return;
            }

            if (allowImmediateExpansion && PrepareInstance(type, instance))
            {
                Logging.Info("AdaptiveRoads compatibility pass completed.");
            }
            if (_runtimePatchesReady && _createHookInstalled
                && _deserializeHookInstalled && _lifeCycleLoadHookInstalled
                && _asymPavementColorPrefixPatched
                && ReferenceEquals(_preparedInstance, instance))
            {
                RetryGate.Complete(generation);
                Logging.Info("AdaptiveRoads compatibility ready; simulation polling stopped until a lifecycle request.");
            }
        }

        private static bool PrepareInstance(
            Type managerType,
            object instance)
        {
            if (instance == null)
            {
                return false;
            }

            if (ReferenceEquals(_preparedInstance, instance))
            {
                return false;
            }

            FieldInfo nodeBuffer = ThirdPartyCapacityCompatibility.FindField(
                managerType,
                "NodeBuffer");
            Type nodeElementType = GetElementType(
                nodeBuffer,
                "AdaptiveRoads NodeBuffer");
            ArrayCompatibility.ExpandDirect(
                nodeBuffer,
                instance,
                Capacity.VanillaNodeCapacity,
                Capacity.ExpandedNodeCapacity,
                CreateInitFactory(nodeElementType, "Init", false, false),
                false,
                "AdaptiveRoads NetworkExtensionManager.NodeBuffer");

            FieldInfo segmentBuffer =
                ThirdPartyCapacityCompatibility.FindField(
                    managerType,
                    "SegmentBuffer");
            Type segmentElementType = GetElementType(
                segmentBuffer,
                "AdaptiveRoads SegmentBuffer");
            ArrayCompatibility.ExpandDirect(
                segmentBuffer,
                instance,
                Capacity.VanillaSegmentCapacity,
                Capacity.ExpandedSegmentCapacity,
                CreateInitFactory(segmentElementType, "Init", false, false),
                false,
                "AdaptiveRoads NetworkExtensionManager.SegmentBuffer");

            FieldInfo segmentEndBuffer =
                ThirdPartyCapacityCompatibility.FindField(
                    managerType,
                    "SegmentEndBuffer");
            Type segmentEndElementType = GetElementType(
                segmentEndBuffer,
                "AdaptiveRoads SegmentEndBuffer");
            ArrayCompatibility.ExpandDirect(
                segmentEndBuffer,
                instance,
                Capacity.VanillaSegmentCapacity * 2,
                Capacity.ExpandedSegmentCapacity * 2,
                CreateInitFactory(
                    segmentEndElementType,
                    "Init",
                    true,
                    false),
                false,
                "AdaptiveRoads NetworkExtensionManager.SegmentEndBuffer");

            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    managerType,
                    "m_updatedNodes"),
                instance,
                512,
                1016,
                null,
                false,
                "AdaptiveRoads NetworkExtensionManager.m_updatedNodes");
            ArrayCompatibility.ExpandDirect(
                ThirdPartyCapacityCompatibility.FindField(
                    managerType,
                    "m_updatedSegments"),
                instance,
                576,
                1016,
                null,
                false,
                "AdaptiveRoads NetworkExtensionManager.m_updatedSegments");

            if (!HasExpectedLength(
                    nodeBuffer,
                    instance,
                    Capacity.ExpandedNodeCapacity)
                || !HasExpectedLength(
                    segmentBuffer,
                    instance,
                    Capacity.ExpandedSegmentCapacity)
                || !HasExpectedLength(
                    segmentEndBuffer,
                    instance,
                    Capacity.ExpandedSegmentCapacity * 2)
                || !HasExpectedLength(
                    ThirdPartyCapacityCompatibility.FindField(
                        managerType,
                        "m_updatedNodes"),
                    instance,
                    Capacity.ExpandedNodeCapacity / 64)
                || !HasExpectedLength(
                    ThirdPartyCapacityCompatibility.FindField(
                        managerType,
                        "m_updatedSegments"),
                    instance,
                    Capacity.ExpandedSegmentCapacity / 64))
            {
                Logging.Error(
                    "AdaptiveRoads buffer expansion did not reach the "
                    + "expected lengths; compatibility remains pending.");
                return false;
            }

            _preparedInstance = instance;
            _waitingLogged = false;
            Logging.Info(
                "Expanded AdaptiveRoads runtime buffers: nodes="
                + Capacity.ExpandedNodeCapacity
                + ", segments="
                + Capacity.ExpandedSegmentCapacity
                + ", segmentEnds="
                + (Capacity.ExpandedSegmentCapacity * 2)
                + ".");
            return true;
        }

        private static void EnsureRuntimePatches(
            Harmony harmony,
            Assembly assembly,
            Type managerType)
        {
            if (_runtimePatchesReady) return;
            _runtimePatchPassFailed = false;
            if (harmony == null)
            {
                Logging.Warning(
                    "Harmony is unavailable; AdaptiveRoads runtime "
                    + "limit compatibility is inactive.");
                return;
            }

            int previousCount = PatchedMethods.Count;
            PatchMethod(
                harmony,
                managerType,
                "UpdateAllNetworkFlags",
                Type.EmptyTypes,
                "AllNetworkLimitsTranspiler");
            PatchMethod(
                harmony,
                managerType,
                "get_SerializationCapacity",
                Type.EmptyTypes,
                "SerializationCapacityTranspiler");

            PatchMethod(
                harmony,
                ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.Manager.NetInfoExtionsion+Net",
                    ModName),
                "RemoveSegmentUserValue",
                new Type[] { typeof(int) },
                "SegmentLimitTranspiler");
            PatchMethod(
                harmony,
                ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.Manager.NetInfoExtionsion+Net",
                    ModName),
                "AllocateUserData",
                Type.EmptyTypes,
                "SegmentLimitTranspiler");
            PatchMethod(
                harmony,
                ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.Util.RoadUtils",
                    ModName),
                "SetDirectionImpl",
                new Type[] { typeof(bool) },
                "SegmentLimitTranspiler");
            PatchMethod(
                harmony,
                ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.Util.RoadEditorUtils",
                    ModName),
                "RefreshAllNetworks",
                Type.EmptyTypes,
                "NodeLimitTranspiler");
            PatchMethod(
                harmony,
                ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.UI.RoadEditor.Overlay",
                    ModName),
                "RenderOverlay",
                new Type[] { typeof(RenderManager.CameraInfo) },
                "SegmentLimitTranspiler");

            Type trackManager = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "AdaptiveRoads.Manager.TrackManager",
                ModName);
            PatchMethod(
                harmony,
                trackManager,
                "EndRendering",
                1,
                "AllNetworkLimitsTranspiler");
            PatchMethod(
                harmony,
                trackManager,
                "CalculateGroupData",
                7,
                "AllNetworkLimitsTranspiler");
            PatchMethod(
                harmony,
                trackManager,
                "PopulateGroupData",
                12,
                "AllNetworkLimitsTranspiler");

            Type netUtil = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "KianCommons.NetUtil",
                ModName);
            PatchMethod(
                harmony,
                netUtil,
                "IsSegmentValid",
                new Type[] { typeof(ushort) },
                "SegmentLimitTranspiler");
            PatchMethod(
                harmony,
                netUtil,
                "AssertSegmentValid",
                new Type[] { typeof(ushort) },
                "SegmentLimitTranspiler");
            PatchMethod(
                harmony,
                netUtil,
                "IsNodeValid",
                new Type[] { typeof(ushort) },
                "NodeLimitTranspiler");
            PatchSimilarLaneIterators(harmony, assembly);

            if (PatchedMethods.Count == 0)
            {
                Logging.Warning(
                    "No known AdaptiveRoads runtime capacity loops were "
                    + "patched.");
                return;
            }

            if (PatchedMethods.Count != previousCount)
            {
                Logging.Info(
                    "Patched "
                    + PatchedMethods.Count
                    + " AdaptiveRoads runtime capacity methods.");
            }
            _runtimePatchesReady = !_runtimePatchPassFailed && PatchedMethods.Count != 0;
        }

        private static void EnsureAsymPavementColorPrefix(
            Harmony harmony,
            Assembly assembly)
        {
            if (harmony == null || _asymPavementColorPrefixPatched)
            {
                return;
            }

            try
            {
                Type patchType = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.Patches.AsymPavements."
                    + "RefreshJunctionDataPatch",
                    ModName);
                if (patchType == null)
                {
                    return;
                }

                MethodInfo target = patchType.GetMethod(
                    "Prefix",
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                MethodInfo transpiler = typeof(AdaptiveRoadsCompatibility)
                    .GetMethod(
                        "AdaptiveRoadsColorPrefixTranspiler",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (target == null || transpiler == null)
                {
                    Logging.Warning(
                        "Could not resolve AdaptiveRoads AsymPavements "
                        + "RefreshJunctionData Prefix for color compatibility.");
                    return;
                }

                harmony.Patch(
                    target,
                    null,
                    null,
                    new HarmonyMethod(transpiler),
                    null);
                _asymPavementColorPrefixPatched = true;
                Logging.Info(
                    "Patched AdaptiveRoads AsymPavements "
                    + "RefreshJunctionData Prefix to use the expanded "
                    + "node-color base.");
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "Failed to patch AdaptiveRoads AsymPavements color "
                    + "Prefix: "
                    + exception.GetType().Name
                    + ".");
            }
        }

        public static IEnumerable<CodeInstruction>
            AdaptiveRoadsColorPrefixTranspiler(
                IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo helper = AccessTools.Method(
                typeof(Capacity),
                "GetNodeColorIndexBase",
                null,
                null);
            if (helper == null)
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
                list[i].operand = helper;
                ++replacements;
            }

            Logging.Info(
                "AdaptiveRoads AsymPavements color Prefix node-base "
                + "replacements: "
                + replacements
                + ".");
            return list;
        }

        private static void EnsureCreateHook(
            Harmony harmony,
            Type managerType)
        {
            if (harmony == null || _createHookInstalled)
            {
                return;
            }

            try
            {
                MethodInfo create = managerType.GetMethod(
                    "Create",
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                MethodInfo postfix = typeof(AdaptiveRoadsCompatibility)
                    .GetMethod(
                        "CreatePostfix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (create == null || postfix == null)
                {
                    Logging.Warning(
                        "Could not install the AdaptiveRoads Create hook.");
                    return;
                }

                harmony.Patch(
                    create,
                    null,
                    new HarmonyMethod(postfix),
                    null,
                    null);
                _createHookInstalled = true;
                Logging.Info(
                    "Installed AdaptiveRoads Create postfix for immediate "
                    + "expanded-buffer initialization.");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to install the AdaptiveRoads Create hook: "
                    + exception);
            }
        }

        private static void CreatePostfix(object __result)
        {
            if (__result == null)
            {
                return;
            }
            RetryGate.Request();

            // Expanded saves contain one AdaptiveRoads record for every high
            // id, so their arrays must be large before DeserializeImp runs.
            // Vanilla saves use the opposite order: reading them into an
            // expanded array makes the reader consume bytes that do not
            // exist, so expansion is deferred until after DeserializeImp.
            if (Metadata.IsExpandedSave())
            {
                PrepareInstance(__result.GetType(), __result);
            }
        }

        private static void EnsureDeserializeHook(
            Harmony harmony,
            Type managerType)
        {
            if (harmony == null || _deserializeHookInstalled)
            {
                return;
            }

            try
            {
                MethodInfo deserialize = null;
                MethodInfo[] methods = managerType.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; ++i)
                {
                    if (methods[i].Name != "DeserializeImp"
                        || methods[i].GetParameters().Length != 1)
                    {
                        continue;
                    }

                    if (deserialize != null)
                    {
                        deserialize = null;
                        break;
                    }

                    deserialize = methods[i];
                }

                MethodInfo postfix = typeof(AdaptiveRoadsCompatibility)
                    .GetMethod(
                        "DeserializeImpPostfix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (deserialize == null || postfix == null)
                {
                    Logging.Warning(
                        "Could not install the AdaptiveRoads "
                        + "DeserializeImp hook.");
                    return;
                }

                harmony.Patch(
                    deserialize,
                    null,
                    new HarmonyMethod(postfix),
                    null,
                    null);
                _deserializeHookInstalled = true;
                Logging.Info(
                    "Installed AdaptiveRoads DeserializeImp postfix for "
                    + "vanilla-save buffer expansion.");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to install the AdaptiveRoads DeserializeImp "
                    + "hook: "
                    + exception);
            }
        }

        private static void EnsureLifeCycleLoadHook(
            Harmony harmony,
            Assembly assembly)
        {
            if (harmony == null || _lifeCycleLoadHookInstalled)
            {
                return;
            }

            try
            {
                Type lifeCycle = ThirdPartyCapacityCompatibility.FindType(
                    assembly,
                    "AdaptiveRoads.LifeCycle.LifeCycle",
                    ModName);
                if (lifeCycle == null)
                {
                    return;
                }

                MethodInfo load = lifeCycle.GetMethod(
                    "Load",
                    BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                MethodInfo postfix = typeof(AdaptiveRoadsCompatibility)
                    .GetMethod(
                        "LifeCycleLoadPostfix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (load == null || postfix == null)
                {
                    Logging.Warning(
                        "Could not install the AdaptiveRoads LifeCycle.Load "
                        + "color compatibility hook.");
                    return;
                }

                harmony.Patch(
                    load,
                    null,
                    new HarmonyMethod(postfix),
                    null,
                    null);
                _lifeCycleLoadHookInstalled = true;
                Logging.Info(
                    "Installed AdaptiveRoads LifeCycle.Load hook for the "
                    + "post-pipeline network color pass.");
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "Failed to install the AdaptiveRoads LifeCycle.Load "
                    + "color compatibility hook: "
                    + exception.GetType().Name
                    + ".");
            }
        }

        private static void LifeCycleLoadPostfix()
        {
            RetryGate.Request();
            NetworkColorOffsetPatch.ApplyAfterAdaptiveRoadsLoad(
                HarmonyBootstrap.HarmonyInstance);
        }

        private static void DeserializeImpPostfix(object __instance)
        {
            RetryGate.Request();
            if (__instance == null || Metadata.IsExpandedSave())
            {
                return;
            }

            PrepareInstance(__instance.GetType(), __instance);
        }

        private static bool HasExpectedLength(
            FieldInfo field,
            object instance,
            int expectedLength)
        {
            if (field == null)
            {
                return false;
            }

            try
            {
                Array array = field.GetValue(instance) as Array;
                return array != null
                    && array.Length == expectedLength;
            }
            catch
            {
                return false;
            }
        }

        private static void PatchSimilarLaneIterators(
            Harmony harmony,
            Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }

            for (int i = 0; i < types.Length; ++i)
            {
                Type type = types[i];
                if (type == null
                    || type.FullName == null
                    || type.FullName.IndexOf(
                        "AdaptiveRoads.Util.LaneHelpers+<GetSimilarLanes>",
                        StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                PatchMethod(
                    harmony,
                    type,
                    "MoveNext",
                    Type.EmptyTypes,
                    "SegmentLimitTranspiler");
            }
        }

        private static void PatchMethod(
            Harmony harmony,
            Type type,
            string methodName,
            Type[] parameterTypes,
            string transpilerName)
        {
            if (type == null)
            {
                _runtimePatchPassFailed = true;
                return;
            }

            MethodInfo target = null;
            try
            {
                target = type.GetMethod(
                    methodName,
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    parameterTypes,
                    null);
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to resolve AdaptiveRoads method "
                    + type.FullName
                    + "."
                    + methodName
                    + ": "
                    + exception);
            }

            PatchResolvedMethod(
                harmony,
                type,
                methodName,
                target,
                transpilerName);
        }

        private static void PatchMethod(
            Harmony harmony,
            Type type,
            string methodName,
            int parameterCount,
            string transpilerName)
        {
            if (type == null)
            {
                _runtimePatchPassFailed = true;
                return;
            }

            MethodInfo target = null;
            try
            {
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; ++i)
                {
                    if (methods[i].Name != methodName
                        || methods[i].GetParameters().Length
                            != parameterCount)
                    {
                        continue;
                    }

                    if (target != null)
                    {
                        Logging.Warning(
                            "Multiple AdaptiveRoads methods match "
                            + type.FullName
                            + "."
                            + methodName
                            + " with "
                            + parameterCount
                            + " parameters; leaving it unchanged.");
                        target = null;
                        break;
                    }

                    target = methods[i];
                }
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to resolve AdaptiveRoads method "
                    + type.FullName
                    + "."
                    + methodName
                    + ": "
                    + exception);
            }

            PatchResolvedMethod(
                harmony,
                type,
                methodName,
                target,
                transpilerName);
        }

        private static void PatchResolvedMethod(
            Harmony harmony,
            Type type,
            string methodName,
            MethodInfo target,
            string transpilerName)
        {
            if (type == null)
            {
                _runtimePatchPassFailed = true;
                return;
            }

            string key =
                type.Assembly.FullName
                + "|"
                + type.FullName
                + "|"
                + methodName;
            if (!PatchedMethods.Add(key))
            {
                return;
            }

            if (target == null)
            {
                _runtimePatchPassFailed = true;
                PatchedMethods.Remove(key);
                Logging.Warning(
                    "Could not find AdaptiveRoads method "
                    + type.FullName
                    + "."
                    + methodName
                    + " for capacity patching.");
                return;
            }

            try
            {
                MethodInfo transpiler = typeof(AdaptiveRoadsCompatibility)
                    .GetMethod(
                        transpilerName,
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                if (transpiler == null)
                {
                    _runtimePatchPassFailed = true;
                    PatchedMethods.Remove(key);
                    Logging.Error(
                        "Could not find AdaptiveRoads transpiler "
                        + transpilerName
                        + ".");
                    return;
                }

                harmony.Patch(
                    target,
                    null,
                    null,
                    new HarmonyMethod(transpiler),
                    null);
                Logging.Info(
                    "Patched AdaptiveRoads method "
                    + type.FullName
                    + "."
                    + methodName
                    + " for expanded limits.");
            }
            catch (Exception exception)
            {
                PatchedMethods.Remove(key);
                _runtimePatchPassFailed = true;
                Logging.Error(
                    "Failed to patch AdaptiveRoads method "
                    + type.FullName
                    + "."
                    + methodName
                    + ": "
                    + exception);
            }
        }

        public static IEnumerable<CodeInstruction> AllNetworkLimitsTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceLimits(instructions, true, true);
        }

        public static IEnumerable<CodeInstruction> NodeLimitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceLimits(instructions, true, false);
        }

        public static IEnumerable<CodeInstruction> SegmentLimitTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceLimits(instructions, false, true);
        }

        public static IEnumerable<CodeInstruction> SerializationCapacityTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            MethodInfo helper = AccessTools.Method(
                typeof(Capacity),
                "GetAdaptiveRoadsSerializationCapacity",
                null,
                null);
            if (helper == null)
            {
                Logging.Error(
                    "Could not find "
                    + "Capacity.GetAdaptiveRoadsSerializationCapacity(); "
                    + "leaving AdaptiveRoads serialization capacity "
                    + "unchanged.");
                return list;
            }

            int replacements = 0;
            for (int i = 0; i < list.Count; ++i)
            {
                CodeInstruction instruction = list[i];
                if (!IsInt32Constant(instruction, 1916928))
                {
                    continue;
                }

                instruction.opcode = OpCodes.Call;
                instruction.operand = helper;
                ++replacements;
            }

            if (replacements == 0)
            {
                Logging.Warning(
                    "AdaptiveRoads SerializationCapacity constant was not "
                    + "found; save buffer capacity remains vanilla-sized.");
            }

            return list;
        }

        private static IEnumerable<CodeInstruction> ReplaceLimits(
            IEnumerable<CodeInstruction> instructions,
            bool replaceNodes,
            bool replaceSegments)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (replaceNodes
                    && IsInt32Constant(
                        instruction,
                        Capacity.VanillaNodeCapacity))
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand =
                        Capacity.ExpandedNodeCapacity;
                }
                else if (replaceSegments
                    && IsInt32Constant(
                        instruction,
                        Capacity.VanillaSegmentCapacity))
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand =
                        Capacity.ExpandedSegmentCapacity;
                }

                yield return instruction;
            }
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

        private static Type GetElementType(
            FieldInfo field,
            string label)
        {
            if (field == null)
            {
                return null;
            }

            Type elementType = field.FieldType.GetElementType();
            if (elementType == null)
            {
                Logging.Warning(
                    "Compatibility member is not an array: "
                    + label
                    + ".");
            }

            return elementType;
        }

        private static IndexedElementFactory CreateInitFactory(
            Type elementType,
            string methodName,
            bool segmentEnd,
            bool lane)
        {
            if (elementType == null)
            {
                return null;
            }

            MethodInfo init = elementType.GetMethod(
                methodName,
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic);
            if (init == null)
            {
                Logging.Warning(
                    "Could not find "
                    + elementType.FullName
                    + "."
                    + methodName
                    + " for AdaptiveRoads initialization.");
                return null;
            }

            return delegate(int index)
            {
                object value = Activator.CreateInstance(elementType);
                if (segmentEnd)
                {
                    init.Invoke(
                        value,
                        new object[]
                        {
                            (ushort)(index / 2),
                            (index & 1) == 0
                        });
                }
                else if (lane)
                {
                    init.Invoke(
                        value,
                        new object[] { (uint)index });
                }
                else
                {
                    init.Invoke(
                        value,
                        new object[] { (ushort)index });
                }

                return value;
            };
        }
    }
}
