using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2ElectricityRecovery
    {
        private const int GroupLimit = ElectricityManager.MAX_PULSE_GROUPS;
        private static ElectricityManager.Cell[] _waitingForCycle;
        private static ElectricityManager.Cell[] _validatedGrid;
        private static FieldInfo _loadedGroupsField;
        private static FieldInfo _loadedUnitsField;
        private static FieldInfo _mergeIndexField;

        internal static void Reset() { _waitingForCycle = null; _validatedGrid = null; }

        internal static MethodInfo ResolveRoot(MethodInfo simulation)
        {
            Type intRef = typeof(int).MakeByRefType();
            Type[] args = { typeof(ElectricityManager), typeof(int), typeof(ElectricityManager.Cell[]),
                intRef, intRef, intRef, intRef, intRef, typeof(bool).MakeByRefType() };
            ParameterInfo[] parameters = simulation.GetParameters();
            for (int i = 0; i < args.Length; ++i)
                if (parameters[i].ParameterType != args[i])
                    throw new InvalidOperationException("81 Tiles electricity simulation signature changed.");
            MethodInfo root = AccessTools.Method(simulation.DeclaringType, "GetRootGroup", new[] { typeof(ushort) }, null);
            if (root == null || !root.IsStatic || root.ReturnType != typeof(ushort))
                throw new InvalidOperationException("81 Tiles electricity group lookup signature changed.");
            return root;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodInfo validateIndex = AccessTools.Method(typeof(EightyOne2ElectricityRecovery), "ValidateIndex", null, null);
            MethodInfo validateParent = AccessTools.Method(typeof(EightyOne2ElectricityRecovery), "ValidateParent", null, null);
            MethodInfo validateQueue = AccessTools.Method(typeof(EightyOne2ElectricityRecovery), "ValidateQueueIndex", null, null);
            Type owner = original.DeclaringType;
            FieldInfo groupBuffer = AccessTools.Field(owner, "s_pulseGroups");
            FieldInfo unitBuffer = AccessTools.Field(owner, "s_pulseUnits");
            if (groupBuffer == null || unitBuffer == null) throw new InvalidOperationException("81 Tiles electricity pulse arrays changed.");
            Type groups = groupBuffer.FieldType.GetElementType();
            Type units = unitBuffer.FieldType.GetElementType();
            FieldInfo mergeIndex = AccessTools.Field(groups, "m_mergeIndex");
            if (mergeIndex == null || mergeIndex.IsStatic || mergeIndex.FieldType != typeof(ushort))
                throw new InvalidOperationException("81 Tiles electricity merge parent field changed.");
            _loadedGroupsField = groupBuffer;
            _loadedUnitsField = unitBuffer;
            _mergeIndexField = mergeIndex;
            Dictionary<int, CodeInstruction[]> insertions = new Dictionary<int, CodeInstruction[]>();
            int cellReads = 0, nodeReads = 0, groupParentReads = 0, queueReads = 0, queueWrites = 0;
            for (int i = 3; i < code.Count; ++i)
            {
                if (object.Equals(code[i].operand, units) && (code[i].opcode == OpCodes.Ldelem || code[i].opcode == OpCodes.Stelem))
                {
                    bool write = code[i].opcode == OpCodes.Stelem;
                    int checkAt = write ? i - 1 : i;
                    if (checkAt < 3) throw new InvalidOperationException("81 Tiles electricity queue access pattern changed.");
                    int local = EightyOne2SimulationRanges.Local(code[checkAt - 3], true);
                    int value = write ? EightyOne2SimulationRanges.Local(code[checkAt], true) : -1;
                    CodeInstruction cursor = code[checkAt - 2];
                    int expectedCursor = write ? 5 : 4;
                    ParameterInfo parameter = cursor.operand as ParameterInfo;
                    int cursorIndex = parameter != null ? parameter.Position
                        : cursor.operand is byte ? (byte)cursor.operand
                        : cursor.operand is short ? (short)cursor.operand
                        : cursor.operand is int ? (int)cursor.operand : -1;
                    if (local < 0 || original.GetMethodBody().LocalVariables[local].LocalType != unitBuffer.FieldType
                        || (cursor.opcode != OpCodes.Ldarg_S && cursor.opcode != OpCodes.Ldarg) || cursorIndex != expectedCursor
                        || code[checkAt - 1].opcode != OpCodes.Ldind_I4
                        || (write && (value < 0 || original.GetMethodBody().LocalVariables[value].LocalType != units
                            || code[i].labels.Count != 0 || code[i].blocks.Count != 0)))
                        throw new InvalidOperationException("81 Tiles electricity queue access pattern changed.");
                    // Check while the stack contains the array and index, before a write loads its struct value.
                    CodeInstruction load = code[checkAt - 3];
                    insertions.Add(checkAt, new[] { new CodeInstruction(OpCodes.Dup, null),
                        new CodeInstruction(load.opcode, load.operand), new CodeInstruction(OpCodes.Ldlen, null),
                        new CodeInstruction(OpCodes.Conv_I4, null), new CodeInstruction(OpCodes.Call, validateQueue) });
                    if (write) ++queueWrites; else ++queueReads;
                    continue;
                }
                CodeInstruction arrayLoad = null;
                FieldInfo indexField = code[i - 1].operand as FieldInfo;
                if (code[i].opcode == OpCodes.Ldelem && object.Equals(code[i].operand, typeof(ElectricityManager.Cell)))
                {
                    if (code[i - 2].opcode != OpCodes.Ldarg_2 || EightyOne2SimulationRanges.Local(code[i - 1], true) < 0)
                        throw new InvalidOperationException("81 Tiles electricity cell index source changed.");
                    // Row initialization is bounded by the original phase grid loop.
                    if (++cellReads > 1) arrayLoad = code[i - 2];
                }
                else if (code[i].opcode == OpCodes.Ldelema && object.Equals(code[i].operand, groups)
                    && code[i - 1].opcode == OpCodes.Ldfld && indexField != null
                    && indexField.DeclaringType == groups && indexField.Name == "m_mergeIndex")
                {
                    arrayLoad = code[i - 3];
                    ++groupParentReads;
                }
                else if (code[i].opcode == OpCodes.Ldelema && object.Equals(code[i].operand, typeof(NetNode))
                    && code[i - 1].opcode == OpCodes.Ldfld && indexField != null
                    && indexField.DeclaringType == units && indexField.Name == "m_node")
                {
                    arrayLoad = code[i - 3];
                    ++nodeReads;
                }
                if (arrayLoad != null)
                {
                    int local = EightyOne2SimulationRanges.Local(arrayLoad, true);
                    Type element = code[i].operand as Type;
                    if (arrayLoad.opcode != OpCodes.Ldarg_2 && (local < 0
                        || original.GetMethodBody().LocalVariables[local].LocalType != element.MakeArrayType()))
                        throw new InvalidOperationException("81 Tiles electricity array source changed.");
                    if (element == groups)
                        insertions.Add(i, new[] { new CodeInstruction(OpCodes.Dup, null), new CodeInstruction(OpCodes.Ldarg_3, null),
                            new CodeInstruction(OpCodes.Ldind_I4, null), new CodeInstruction(OpCodes.Call, validateParent) });
                    else
                        insertions.Add(i, new[] { new CodeInstruction(OpCodes.Dup, null),
                            new CodeInstruction(arrayLoad.opcode, arrayLoad.operand), new CodeInstruction(OpCodes.Call, validateIndex) });
                }
            }
            if (cellReads != 3 || nodeReads != 1 || groupParentReads != 1 || queueReads != 1 || queueWrites != 3)
                throw new InvalidOperationException("81 Tiles electricity pulse access pattern changed.");
            return InsertChecks(code, insertions);
        }

        internal static IEnumerable<CodeInstruction> RootTranspiler(MethodBase original, IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            FieldInfo buffer = AccessTools.Field(original.DeclaringType, "s_pulseGroups");
            MethodInfo validate = AccessTools.Method(typeof(EightyOne2ElectricityRecovery), "ValidateGroup", null, null);
            LocalBuilder hops = generator.DeclareLocal(typeof(int));
            Dictionary<int, CodeInstruction[]> insertions = new Dictionary<int, CodeInstruction[]>();
            for (int i = 2; i + 1 < code.Count; ++i)
            {
                FieldInfo merge = code[i + 1].operand as FieldInfo;
                if (code[i].opcode != OpCodes.Ldelema || code[i - 2].opcode != OpCodes.Ldsfld
                    || !object.Equals(code[i - 2].operand, buffer) || code[i - 1].opcode != OpCodes.Ldarg_0
                    || code[i + 1].opcode != OpCodes.Ldfld || merge == null || merge.Name != "m_mergeIndex"
                    || merge.DeclaringType != buffer.FieldType.GetElementType()) continue;
                insertions.Add(i, new[] { new CodeInstruction(OpCodes.Dup, null),
                    new CodeInstruction(OpCodes.Ldloca, hops), new CodeInstruction(OpCodes.Call, validate) });
            }
            if (insertions.Count != 2) throw new InvalidOperationException("81 Tiles electricity root lookup changed.");
            insertions.Add(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, hops) });
            return InsertChecks(code, insertions);
        }

        private static IEnumerable<CodeInstruction> InsertChecks(List<CodeInstruction> code, Dictionary<int, CodeInstruction[]> insertions)
        {
            for (int i = 0; i < code.Count; ++i)
            {
                CodeInstruction[] checks;
                if (insertions.TryGetValue(i, out checks))
                {
                    if (code[i].blocks.Count != 0) throw new InvalidOperationException("81 Tiles electricity exception region changed.");
                    checks[0].labels.AddRange(code[i].labels);
                    code[i] = new CodeInstruction(code[i]);
                    code[i].labels.Clear();
                    foreach (CodeInstruction check in checks) yield return check;
                }
                yield return code[i];
            }
        }

        internal static void ValidateState(int groupLength, int queueLength, int count, int start, int end)
        {
            int limit = Math.Min(GroupLimit, groupLength);
            if ((uint)count > (uint)limit) throw new InvalidPulseException("group count", count, limit);
            if ((uint)start >= (uint)queueLength) throw new InvalidPulseException("queue start", start, queueLength);
            if ((uint)end >= (uint)queueLength) throw new InvalidPulseException("queue end", end, queueLength);
        }

        internal static void ValidateQueueIndex(int index, int length)
        {
            if ((uint)index >= (uint)length) throw new InvalidPulseException("pulse queue index", index, length);
        }

        internal static void ValidateIndex(int index, Array array)
        {
            if ((uint)index >= (uint)array.Length)
                throw new InvalidPulseException(array.GetType().GetElementType().Name, index, array.Length);
        }

        private static void ValidateLoadedCycle(int count, int start, int end)
        {
            Array groups = (Array)_loadedGroupsField.GetValue(null);
            Array units = (Array)_loadedUnitsField.GetValue(null);
            ValidateState(groups == null ? 0 : groups.Length, units == null ? 0 : units.Length, count, start, end);
            if (count == 0) return;
            ushort[] parents = new ushort[count];
            for (int i = 0; i < count; ++i) parents[i] = (ushort)_mergeIndexField.GetValue(groups.GetValue(i));
            ValidateLoadedParents(parents);
        }

        internal static void ValidateLoadedParents(ushort[] parents)
        {
            // A completed chain is already known to reach the no-parent sentinel.
            int[] visited = new int[parents.Length];
            for (int i = 0; i < parents.Length; ++i)
            {
                int marker = i + 1;
                int group = i;
                while (group != ushort.MaxValue)
                {
                    if (group >= parents.Length) throw new InvalidPulseException("saved merge parent", group, parents.Length);
                    if (visited[group] == marker) throw new InvalidPulseException("saved cyclic merge group", group, parents.Length);
                    if (visited[group] != 0) break;
                    visited[group] = marker;
                    group = parents[group];
                }
            }
        }

        internal static void ValidateGroup(int group, ref int hops)
        {
            // 81 Tiles allocates at least 1024 entries and creates at most 1024 groups.
            const int limit = GroupLimit;
            if ((uint)group >= (uint)limit)
                throw new InvalidPulseException(hops == 0 ? "root input group" : "root merge parent after " + hops + " links", group, limit);
            if (++hops > limit) throw new InvalidPulseException("cyclic root group after " + hops + " links", group, limit);
        }

        internal static void ValidateParent(int group, int count)
        {
            if ((uint)group >= (uint)count) throw new InvalidPulseException("merge parent", group, count);
        }

        internal static bool Prefix(ElectricityManager __0, int __1, ElectricityManager.Cell[] __2,
            ref int __3, ref int __4, ref int __5, ref int __6, ref int __7, ref bool __8)
        {
            if (__1 == 0 || __1 == 1000) return true;
            if (ReferenceEquals(_waitingForCycle, __2))
            {
                if ((Singleton<SimulationManager>.instance.m_currentFrameIndex & 255u) != 0) return false;
            }
            _waitingForCycle = null;
            if (ReferenceEquals(_validatedGrid, __2)) return true;
            _validatedGrid = __2;
            try
            {
                ValidateLoadedCycle(__3, __4, __5);
                return true;
            }
            catch (InvalidPulseException failure)
            {
                Finalizer(failure, __0, __2, ref __3, ref __4, ref __5, ref __6, ref __7, ref __8);
                return false;
            }
        }

        internal static Exception Finalizer(Exception __exception, ElectricityManager __0, ElectricityManager.Cell[] __2,
            ref int __3, ref int __4, ref int __5, ref int __6, ref int __7, ref bool __8)
        {
            InvalidPulseException failure = __exception as InvalidPulseException;
            if (failure == null) return __exception;

            int count = __3, start = __4, end = __5;
            __3 = __4 = __5 = __6 = __7 = 0;
            __8 = false;
            ushort[] groups = __0.m_nodeGroups;
            for (int i = 0; i < groups.Length; ++i) groups[i] = ushort.MaxValue;
            for (int i = 0; i < __2.Length; ++i)
            {
                __2[i].m_pulseGroup = ushort.MaxValue;
                __2[i].m_tmpElectrified = __2[i].m_electrified;
            }
            _waitingForCycle = __2;
            Logging.Warning("81 Tiles electricity pulse state invalid: " + failure.Message
                + " | active groups=" + count + " | queue start=" + start + " | queue end=" + end
                + ". Discarded the partial electricity cycle; simulation will rebuild it at the next phase zero.\n"
                + failure.StackTrace);
            return null;
        }

        private sealed class InvalidPulseException : Exception
        {
            internal InvalidPulseException(string source, int index, int limit)
                : base(source + " value " + index + " (limit " + limit + ")") { }
        }
    }
}
