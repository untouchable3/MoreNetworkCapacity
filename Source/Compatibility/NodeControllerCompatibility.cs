using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class NodeControllerCompatibility
    {
        private const string AssemblyName = "NodeControllerRenewal";
        private const string ModName = "Node Controller Renewal";
        private const string ManagerTypeName = "NodeController.Manager";

        private static bool _patchesApplied;
        private static bool _fromXmlPatched;
        private static bool _initialUpdatePatched;
        private static readonly object BufferAccessorSync = new object();
        private static volatile BufferAccessor _bufferAccessor;

        private sealed class BufferAccessor
        {
            internal readonly Type Owner, Element;
            internal readonly FieldInfo Field;
            internal readonly Func<object, Array> Read;
            internal BufferAccessor(Type owner, FieldInfo field)
            {
                Owner = owner;
                Field = field;
                if (field == null || !field.FieldType.IsArray || field.FieldType.GetArrayRank() != 1) return;
                Element = field.FieldType.GetElementType();
                try
                {
                    // Read the backing field directly, never the patched Buffer getter.
                    DynamicMethod read = new DynamicMethod("MNC_ReadNodeControllerBuffer", typeof(Array),
                        new[] { typeof(object) }, typeof(NodeControllerCompatibility).Module, true);
                    ILGenerator il = read.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, owner);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Ret);
                    Read = (Func<object, Array>)read.CreateDelegate(typeof(Func<object, Array>));
                }
                catch (Exception exception)
                {
                    // Runtime restrictions can refuse emission. Cache the metadata anyway.
                    Logging.Warning("Node Controller direct buffer accessor unavailable; using cached reflection: "
                        + exception.GetType().Name);
                    Read = delegate(object manager) { return field.GetValue(manager) as Array; };
                }
            }
        }

        internal static void Reset()
        {
            _patchesApplied = false;
            _fromXmlPatched = false;
            _initialUpdatePatched = false;
            NodeControllerLoadUpdates.Reset();
            NodeControllerFlagChecks.Reset();
            lock (BufferAccessorSync) { _bufferAccessor = null; }
        }

        internal static void Apply(Harmony harmony)
        {
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "Node Controller Renewal is not loaded; "
                    + "compatibility is inactive.");
                return;
            }

            NodeControllerFlagChecks.Apply(harmony, assembly);

            Type managerType = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                ManagerTypeName,
                ModName);
            if (managerType == null)
            {
                return;
            }

            if (!_patchesApplied)
            {
                _patchesApplied = PatchManager(harmony, managerType);
            }

            // Constructor patching only affects instances created afterwards.
            // Applying to loaded instances happens lazily in the prefix.
            Logging.Info("Node Controller Renewal compatibility pass completed.");
        }

        private static bool PatchManager(
            Harmony harmony,
            Type managerType)
        {
            if (harmony == null)
            {
                Logging.Warning(
                    "Harmony is unavailable; Node Controller Renewal "
                    + "compatibility is inactive.");
                return false;
            }

            try
            {
                MethodInfo ensurePrefix = typeof(NodeControllerPatches)
                    .GetMethod(
                        "EnsureManagerBufferPrefix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                MethodInfo constructorPostfix = typeof(NodeControllerPatches)
                    .GetMethod(
                        "ManagerConstructorPostfix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                MethodInfo clearPostfix = typeof(NodeControllerPatches)
                    .GetMethod(
                        "ManagerClearPostfix",
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);

                ConstructorInfo constructor = managerType.GetConstructor(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (constructor != null)
                {
                    harmony.Patch(
                        constructor,
                        null,
                        new HarmonyMethod(constructorPostfix),
                        null,
                        null);
                }

                MethodInfo[] methods = managerType.GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                for (int i = 0; i < methods.Length; ++i)
                {
                    MethodInfo method = methods[i];
                    if (method.IsAbstract
                        || method.ContainsGenericParameters)
                    {
                        continue;
                    }

                    harmony.Patch(
                        method,
                        new HarmonyMethod(ensurePrefix),
                        null,
                        null,
                        null);
                }

                MethodInfo clearMethod = managerType.GetMethod(
                    "Clear",
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (clearMethod != null)
                {
                    harmony.Patch(
                        clearMethod,
                        null,
                        new HarmonyMethod(clearPostfix),
                        null,
                        null);
                }

                if (!_initialUpdatePatched)
                {
                    MethodInfo startInitialUpdate = managerType.GetMethod(
                        "StartInitialUpdate", BindingFlags.Instance | BindingFlags.Public
                        | BindingFlags.NonPublic, null,
                        new[] { typeof(ushort[]), typeof(bool) }, null);
                    if (startInitialUpdate != null)
                    {
                        MethodInfo capture = typeof(NodeControllerPatches).GetMethod(
                            "InitialUpdatePostfix", BindingFlags.Static | BindingFlags.Public);
                        harmony.Patch(startInitialUpdate, null, new HarmonyMethod(capture), null, null);
                        _initialUpdatePatched = true;
                        Logging.Info("Node Controller load update preservation hook installed.");
                    }
                    else
                    {
                        Logging.Warning("Node Controller StartInitialUpdate shape was not found; "
                            + "load geometry update preservation is unavailable.");
                    }
                }

                if (!_fromXmlPatched)
                {
                    MethodInfo fromXml = FindMethod(
                        managerType,
                        "FromXml",
                        3);
                    if (fromXml != null)
                    {
                        MethodInfo transpiler = typeof(NodeControllerPatches)
                            .GetMethod(
                                "FromXmlTranspiler",
                                BindingFlags.Static
                                | BindingFlags.Public
                                | BindingFlags.NonPublic);
                        harmony.Patch(
                            fromXml,
                            null,
                            null,
                            new HarmonyMethod(transpiler),
                            null);
                        _fromXmlPatched = true;
                        Logging.Info(
                            "Expanded Node Controller Renewal FromXml "
                            + "node limit.");
                    }
                }

                Logging.Info(
                    "Patched Node Controller Renewal Manager buffer access.");
                return true;
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to patch Node Controller Renewal Manager: "
                    + exception);
                return false;
            }
        }

        private static MethodInfo FindMethod(
            Type type,
            string name,
            int parameterCount)
        {
            MethodInfo[] methods = type.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                if (string.Equals(
                        method.Name,
                        name,
                        StringComparison.Ordinal)
                    && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }

            return null;
        }

        internal static void EnsureManagerBuffer(object manager)
        {
            if (manager == null)
            {
                return;
            }

            try
            {
                Type managerType = manager.GetType();
                BufferAccessor accessor = _bufferAccessor;
                if (accessor == null || accessor.Owner != managerType)
                {
                    lock (BufferAccessorSync)
                    {
                        accessor = _bufferAccessor;
                        if (accessor == null || accessor.Owner != managerType)
                        {
                            FieldInfo field = ThirdPartyCapacityCompatibility.FindBackingField(managerType, "Buffer")
                                ?? ThirdPartyCapacityCompatibility.FindField(managerType, "Buffer");
                            _bufferAccessor = accessor = new BufferAccessor(managerType, field);
                        }
                    }
                }
                if (accessor.Read == null) return;
                // Every call reads the live array, so Clear/replacement on the same
                // manager is still detected; manager identity is never a ready shortcut.
                Array previous = accessor.Read(manager);
                if (previous == null)
                {
                    return;
                }

                int newLength = Capacity.ExpandedNodeCapacity;
                if (previous.Length >= newLength)
                {
                    return;
                }

                if (previous.Length != Capacity.VanillaNodeCapacity)
                {
                    Logging.Warning(
                        "Node Controller Renewal Manager.Buffer has "
                        + "unexpected length "
                        + previous.Length
                        + "; leaving it unchanged.");
                    return;
                }

                Array replacement =
                    Array.CreateInstance(accessor.Element, newLength);
                Array.Copy(previous, replacement, previous.Length);

                accessor.Field.SetValue(manager, replacement);
                Logging.Info(
                    "Expanded Node Controller Renewal Manager.Buffer: "
                    + previous.Length
                    + " -> "
                    + newLength
                    + ".");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to expand Node Controller Renewal "
                    + "Manager.Buffer: "
                    + exception);
            }
        }
    }

    internal static class NodeControllerPatches
    {
        public static void EnsureManagerBufferPrefix(object __instance)
        {
            NodeControllerCompatibility.EnsureManagerBuffer(__instance);
        }

        public static void ManagerConstructorPostfix(object __instance)
        {
            NodeControllerCompatibility.EnsureManagerBuffer(__instance);
        }

        public static void ManagerClearPostfix(object __instance)
        {
            NodeControllerLoadUpdates.Reset();
            NodeControllerCompatibility.EnsureManagerBuffer(__instance);
        }

        public static void InitialUpdatePostfix(ushort[] __0, bool __1)
        {
            NodeControllerLoadUpdates.Capture(__0, __1);
        }

        public static IEnumerable<CodeInstruction> FromXmlTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4
                    && instruction.operand is int
                    && (int)instruction.operand
                        == Capacity.VanillaNodeCapacity)
                {
                    instruction.operand =
                        Capacity.ExpandedNodeCapacity - 1;
                }

                yield return instruction;
            }
        }
    }
}
