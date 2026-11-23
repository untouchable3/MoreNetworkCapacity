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

            Type managerType = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                ManagerTypeName,
                ModName);
            if (managerType == null)
            {
                throw new InvalidOperationException("Node Controller is loaded but its required Manager type is missing.");
            }

            if (!_patchesApplied)
            {
                _patchesApplied = PatchManager(harmony, managerType);
            }

            // This optimization must not prevent the required buffer/load hooks.
            try { NodeControllerFlagChecks.Apply(harmony, assembly); }
            catch (Exception exception)
            {
                Logging.Warning("Node Controller corner flag optimization is unavailable; "
                    + "required capacity hooks remain active: " + exception);
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
                throw new InvalidOperationException("Harmony is unavailable for required Node Controller capacity hooks.");
            }

            try
            {
                // Check the complete required surface before installing any hook.
                FieldInfo bufferField = ThirdPartyCapacityCompatibility.FindBackingField(managerType, "Buffer")
                    ?? ThirdPartyCapacityCompatibility.FindField(managerType, "Buffer");
                ConstructorInfo requiredConstructor = managerType.GetConstructor(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                MethodInfo requiredClear = managerType.GetMethod("Clear", BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                MethodInfo requiredInitial = managerType.GetMethod("StartInitialUpdate", BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null,
                    new[] { typeof(ushort[]), typeof(bool) }, null);
                MethodInfo requiredFromXml = FindMethod(managerType, "FromXml", 3);
                ParameterInfo[] xmlParameters = requiredFromXml == null ? null : requiredFromXml.GetParameters();
                if (bufferField == null || bufferField.IsStatic || !bufferField.FieldType.IsArray
                    || bufferField.FieldType.GetArrayRank() != 1 || bufferField.FieldType.GetElementType().IsValueType
                    || requiredConstructor == null || requiredClear == null || requiredClear.ReturnType != typeof(void)
                    || requiredInitial == null || requiredInitial.ReturnType != typeof(void)
                    || requiredFromXml == null || requiredFromXml.ReturnType != typeof(void)
                    || xmlParameters[0].ParameterType.FullName != "System.Xml.Linq.XElement"
                    || xmlParameters[1].ParameterType.FullName != "ModsCommon.Utilities.NetObjectsMap"
                    || xmlParameters[2].ParameterType != typeof(bool))
                    throw new InvalidOperationException("Required Node Controller buffer/load method shape changed.");

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
                // The caller propagates required failures to bootstrap/load handling.
                // In particular, do not log a successful pass after a partial install.
                throw new InvalidOperationException("Failed to patch required Node Controller Manager hooks.", exception);
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
            MethodInfo found = null;
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                if (string.Equals(
                        method.Name,
                        name,
                        StringComparison.Ordinal)
                    && method.GetParameters().Length == parameterCount)
                {
                    if (found != null) throw new InvalidOperationException("Ambiguous Node Controller method: " + name);
                    found = method;
                }
            }

            return found;
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

        public static void InitialUpdatePostfix(ushort[] __0, bool __1, object __instance)
        {
            NodeControllerLoadUpdates.Capture(__0, __1);
            if (!__1) return;
            if (__0 == null || __0.Length == 0) { NodeControllerLoadUpdates.CaptureFinal(null); return; }
            Type managerType = __instance.GetType();
            Type options = managerType.GetNestedType("Options", BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo state = managerType.GetField("InitialUpdateState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo update = options == null ? null : managerType.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { options, typeof(ushort[]) }, null);
            if (update == null || state == null || Convert.ToInt32(Enum.Parse(options, "UpdateAll")) != 14)
                throw new InvalidOperationException("Node Controller final update API changed.");
            ushort[] nodes = (ushort[])__0.Clone();
            object flags = Enum.ToObject(options, 14);
            NodeControllerLoadUpdates.CaptureFinal(delegate
            {
                int phase = Convert.ToInt32(state.GetValue(null));
                if (phase != 0 && phase != 2)
                    throw new InvalidOperationException("Node Controller initial update has not completed.");
                System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
                Logging.Info("Node Controller final geometry BEGIN; nodes=" + nodes.Length);
                try
                {
                    update.Invoke(__instance, new object[] { flags, nodes });
                    Logging.Info("Node Controller final geometry END; elapsedMs=" + timer.ElapsedMilliseconds
                        + "; subsequent network work may still be pending.");
                }
                catch (Exception exception)
                {
                    Logging.Warning("Node Controller final geometry FAILED; elapsedMs=" + timer.ElapsedMilliseconds + "; " + exception);
                    throw;
                }
            });
        }

        public static IEnumerable<CodeInstruction> FromXmlTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int match = -1, limits = 0, idSources = 0;
            for (int i = 0; i < code.Count; ++i)
            {
                MethodInfo source = code[i].operand as MethodInfo;
                if (source != null && source.Name == "GetAttrValue" && source.DeclaringType.FullName == "ModsCommon.Utilities.XmlExtension"
                    && source.IsGenericMethod && source.GetGenericArguments().Length == 1
                    && source.GetGenericArguments()[0] == typeof(ushort) && i >= 3
                    && code[i - 3].opcode == OpCodes.Ldstr && object.Equals(code[i - 3].operand, "Id")) ++idSources;
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, Capacity.VanillaNodeCapacity)) continue;
                ++limits;
                if (i < 15 || i + 10 >= code.Count) continue;
                int id = Local(code[i - 1], false);
                int input = i - 12;
                source = code[input].operand as MethodInfo;
                MethodInfo map = code[input + 5].operand as MethodInfo;
                MethodInfo node = code[i + 3].operand as MethodInfo;
                MethodInfo flags = code[i + 4].operand as MethodInfo;
                int mappedId = AddressLocal(code[input + 4]);
                if (id < 0 || source == null || code[input].opcode != OpCodes.Call
                    || source.Name != "GetAttrValue" || source.DeclaringType.FullName != "ModsCommon.Utilities.XmlExtension"
                    || !source.IsGenericMethod || source.GetGenericArguments().Length != 1
                    || source.GetGenericArguments()[0] != typeof(ushort) || source.ReturnType != typeof(ushort)
                    || source.GetParameters().Length != 4 || code[input - 3].opcode != OpCodes.Ldstr
                    || !object.Equals(code[input - 3].operand, "Id") || code[input - 2].opcode != OpCodes.Ldc_I4_0
                    || code[input - 1].opcode != OpCodes.Ldnull || Local(code[input + 1], true) != id
                    || code[input + 2].opcode != OpCodes.Ldarg_2 || Local(code[input + 3], false) != id
                    || mappedId < 0 || mappedId == id || !IdLocals(code, id, mappedId)
                    || code[input + 5].opcode != OpCodes.Callvirt || !IsNodeMap(map)
                    || !BranchTo(code[input + 6], code[i - 3])
                    || Local(code[input + 7], false) != mappedId || Local(code[input + 8], true) != id
                    || Local(code[i - 3], false) != id || !IsFalseBranch(code[i - 2])
                    || (code[i + 1].opcode != OpCodes.Bgt && code[i + 1].opcode != OpCodes.Bgt_S)
                    || !SameForwardTarget(code, code[i - 2].operand, code[i + 1].operand, i + 10)
                    || Local(code[i + 2], false) != id || code[i + 3].opcode != OpCodes.Call
                    || node == null || node.DeclaringType.FullName != "ModsCommon.Utilities.NetExtension" || node.Name != "GetNode"
                    || node.GetParameters().Length != 1 || node.GetParameters()[0].ParameterType != typeof(ushort)
                    || !node.ReturnType.IsByRef || node.ReturnType.GetElementType().FullName != "NetNode"
                    || code[i + 4].opcode != OpCodes.Call || flags == null || flags.DeclaringType.FullName != "NetNode"
                    || flags.Name != "get_flags" || code[i + 5].opcode != OpCodes.Ldc_I4_3
                    || code[i + 6].opcode != OpCodes.Conv_I8 || code[i + 7].opcode != OpCodes.And
                    || code[i + 8].opcode != OpCodes.Ldc_I4_1 || code[i + 9].opcode != OpCodes.Conv_I8
                    || (code[i + 10].opcode != OpCodes.Beq && code[i + 10].opcode != OpCodes.Beq_S)) continue;
                match = i;
            }
            if (match < 0 || limits != 1 || idSources != 1)
                throw new InvalidOperationException("Node Controller XML node-id source/remap/guard pattern changed.");
            // NC Buffer always expands to65024, including vanilla-save loads.
            // Keep the inclusive upper-ID guard and all branch/EH metadata intact.
            code[match].operand = Capacity.ExpandedNodeCapacity - 1;
            return code;
        }

        private static bool IsNodeMap(MethodInfo method)
        {
            if (method == null || method.Name != "TryGetNode" || method.ReturnType != typeof(bool)
                || !method.DeclaringType.IsGenericType
                || method.DeclaringType.GetGenericTypeDefinition().FullName != "ModsCommon.Utilities.NetObjectsMap`1"
                || method.DeclaringType.GetGenericArguments()[0].FullName != "ModsCommon.Utilities.NetObjectId") return false;
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(ushort)
                && parameters[1].ParameterType == typeof(ushort).MakeByRefType();
        }
        private static bool IdLocals(List<CodeInstruction> code, int id, int mapped)
        {
            int stores = 0;
            foreach (CodeInstruction instruction in code)
            {
                LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
                if (variable != null && (variable.LocalIndex == id || variable.LocalIndex == mapped)
                    && variable.LocalType != typeof(ushort)) return false;
                if (Local(instruction, true) == id) ++stores;
                if (AddressLocal(instruction) == id) return false;
            }
            return stores == 2;
        }
        private static bool IsFalseBranch(CodeInstruction instruction)
        { return instruction.opcode == OpCodes.Brfalse || instruction.opcode == OpCodes.Brfalse_S; }
        private static bool BranchTo(CodeInstruction branch, CodeInstruction destination)
        {
            return IsFalseBranch(branch) && branch.operand is Label
                && destination.labels.Contains((Label)branch.operand);
        }
        private static bool SameForwardTarget(List<CodeInstruction> code, object left, object right, int after)
        {
            if (!(left is Label) || !(right is Label)) return false;
            // Harmony can assign distinct labels to two branches at one IL offset.
            for (int i = after + 1; i < code.Count; ++i)
                if (code[i].labels.Contains((Label)left) && code[i].labels.Contains((Label)right)) return true;
            return false;
        }
        private static int AddressLocal(CodeInstruction instruction)
        {
            if (instruction.opcode != OpCodes.Ldloca && instruction.opcode != OpCodes.Ldloca_S) return -1;
            return OperandLocal(instruction.operand);
        }
        private static int Local(CodeInstruction instruction, bool store)
        {
            OpCode code = instruction.opcode;
            if (code == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (code == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (code == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (code == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            return code == (store ? OpCodes.Stloc : OpCodes.Ldloc) || code == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)
                ? OperandLocal(instruction.operand) : -1;
        }
        private static int OperandLocal(object operand)
        {
            LocalVariableInfo local = operand as LocalVariableInfo;
            if (local != null) return local.LocalIndex;
            return operand is byte ? (byte)operand : operand is short ? (short)operand : operand is int ? (int)operand : -1;
        }
    }
}
