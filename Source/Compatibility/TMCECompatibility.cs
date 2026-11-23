using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class TMCECompatibility
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static bool _installed;
        private static FieldInfo[] _loadResetFields;

        internal static void Reset() { _installed = false; _loadResetFields = null; }

        internal static void Apply(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("TransferManagerCE");
            if (assembly == null) return;
            MethodBase[] targets = Prepare(assembly);
            MethodInfo rewrite = typeof(TMCECompatibility).GetMethod("Transpiler", All);
            MethodInfo ensure = typeof(TMCECompatibility).GetMethod("EnsureBitmapPrefix", All);
            List<MethodBase> attempted = new List<MethodBase>();
            try
            {
                for (int i = 0; i < targets.Length; ++i)
                {
                    attempted.Add(targets[i]);
                    HarmonyMethod transpiler = new HarmonyMethod(rewrite); transpiler.priority = Priority.Last;
                    harmony.Patch(targets[i], i == 1 ? new HarmonyMethod(ensure) : null, null, transpiler, null);
                }
                _installed = true;
                Logging.Info("Transfer Manager CE path-distance bitmap and search budget cover "
                    + Capacity.ExpandedNodeCapacity + " nodes; existing search objects grow on their owning thread.");
            }
            catch
            {
                foreach (MethodBase target in attempted)
                { harmony.Unpatch(target, rewrite); harmony.Unpatch(target, ensure); }
                throw;
            }
        }

        internal static MethodBase[] Prepare(Assembly assembly)
        {
            Type current = assembly.GetType("TransferManagerCore.PathDistance", false);
            Type legacy = assembly.GetType("TransferManagerCE.PathDistance", false);
            if ((current == null) == (legacy == null))
                throw new InvalidOperationException("TMCE PathDistance type is missing or ambiguous.");
            Type type = current ?? legacy;
            FieldInfo visited = type.GetField("m_visited", All);
            FieldInfo queue = type.GetField("m_sortedNodes", All);
            if (visited == null || visited.IsStatic || visited.FieldType != typeof(BitArray)
                || queue == null || queue.IsStatic || queue.FieldType.Assembly != assembly || queue.FieldType.Name != "PathQueue")
                throw new InvalidOperationException("TMCE path-distance field layout changed.");
            ConstructorInfo ctor = type.GetConstructor(All, null, new Type[] { typeof(bool), typeof(bool) }, null);
            MethodInfo search = type.GetMethod("FindNearestNeighborId", All, null, new Type[] {
                typeof(bool), typeof(ushort), typeof(ushort).MakeByRefType(), typeof(float).MakeByRefType(),
                typeof(long).MakeByRefType(), typeof(int).MakeByRefType() }, null);
            if (ctor == null || search == null || search.IsStatic || search.ReturnType != typeof(int))
                throw new InvalidOperationException("TMCE path-distance signature changed.");
            Rewrite(ctor, PatchProcessor.GetOriginalInstructions(ctor, (ILGenerator)null));
            Rewrite(search, PatchProcessor.GetOriginalInstructions(search, (ILGenerator)null));
            _loadResetFields = ResolveLoadResetFields(assembly, type.Namespace);
            return new MethodBase[] { ctor, search };
        }

        internal static FieldInfo[] ResolveLoadResetFields(Assembly assembly, string prefix)
        {
            string[] owners = { "PathDistanceCache", "PathDistanceCache", "PathDistanceCache",
                "PathConnectedCache", "PathConnectedCache", "PathConnectedCache", "PathNode" };
            string[] names = { "s_goodsNodeLoader", "s_pedestrianZoneNodeLoader", "s_otherServicesNodeLoader",
                "s_goodsGraph", "s_pedestrianZoneServicesGraph", "s_otherServicesGraph", "s_bInitNeeded" };
            FieldInfo[] fields = new FieldInfo[names.Length];
            for (int i = 0; i < fields.Length; ++i)
            {
                Type owner = assembly.GetType(prefix + "." + owners[i], false);
                FieldInfo field = owner == null ? null : owner.GetField(names[i], All);
                Type expected = i == 6 ? typeof(bool) : assembly.GetType(prefix + (i < 3 ? ".NodeLinkGraph" : ".PathConnected"), false);
                if (field == null || !field.IsStatic || field.IsInitOnly || expected == null || field.FieldType != expected)
                    throw new InvalidOperationException("TMCE load-cache layout changed: " + owners[i] + "." + names[i]);
                fields[i] = field;
            }
            return fields;
        }

        // Runs after the real network pools have been read/expanded and before
        // level-loaded callbacks create matching workers. Invalidate alone
        // cannot rebind the readonly buffers in old graph/generator objects.
        internal static void AfterNetworkDeserialize()
        {
            if (_loadResetFields == null) return;
            ResetLoadCaches(_loadResetFields);
            Logging.Info("Transfer Manager CE network caches reset for the incoming city; graph generation remains deferred.");
        }

        internal static void ResetLoadCaches(FieldInfo[] fields)
        {
            for (int i = 0; i < 6; ++i) fields[i].SetValue(null, null);
            fields[6].SetValue(null, true);
        }

        // Called once per search, on the worker that owns this PathDistance.
        // New objects already have full storage. Late-created/older instances
        // grow once, preserving their bits; no global object registry or scan.
        internal static void EnsureBitmapPrefix(ref BitArray ___m_visited)
        {
            if (___m_visited == null) throw new InvalidOperationException("TMCE visited bitmap is uninitialized.");
            if (___m_visited.Length < Capacity.ExpandedNodeCapacity)
                ___m_visited.Length = Capacity.ExpandedNodeCapacity;
        }

        private static IEnumerable<CodeInstruction> Transpiler(MethodBase __originalMethod, IEnumerable<CodeInstruction> instructions)
        { return Rewrite(__originalMethod, instructions); }

        internal static List<CodeInstruction> Rewrite(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions) code.Add(new CodeInstruction(instruction));
            if (!LoopLocalTypes.Agree(original, code)) throw new InvalidOperationException("TMCE local-variable types changed.");
            Type owner = original.DeclaringType;
            FieldInfo visited = owner.GetField("m_visited", All), queue = owner.GetField("m_sortedNodes", All);
            int match = -1, count = 0;
            if (original is ConstructorInfo)
            {
                ConstructorInfo create = typeof(BitArray).GetConstructor(new Type[] { typeof(int) });
                int writes = 0;
                for (int i = 0; i < code.Count; ++i)
                {
                    if (code[i].opcode == OpCodes.Stfld && object.Equals(code[i].operand, visited)) ++writes;
                    if (i > 0 && i + 2 < code.Count && code[i].LoadsConstant(Capacity.VanillaNodeCapacity)
                        && code[i - 1].opcode == OpCodes.Ldarg_0 && code[i + 1].opcode == OpCodes.Newobj
                        && object.Equals(code[i + 1].operand, create) && code[i + 2].opcode == OpCodes.Stfld
                        && object.Equals(code[i + 2].operand, visited)) { match = i; ++count; }
                }
                if (writes != 1) throw new InvalidOperationException("TMCE visited bitmap has unexpected assignments.");
            }
            else
            {
                MethodInfo update = owner.GetMethod("UpdateNode", All, null,
                    new Type[] { typeof(ushort), typeof(ushort), typeof(float) }, null);
                int updateIndex = -1, updateCount = 0, visitedReads = 0;
                for (int i = 0; i < code.Count; ++i)
                {
                    if (code[i].opcode == OpCodes.Call && object.Equals(code[i].operand, update)) { updateIndex = i; ++updateCount; }
                    if (code[i].opcode == OpCodes.Ldfld && object.Equals(code[i].operand, visited)) ++visitedReads;
                }
                if (update == null || update.ReturnType != typeof(void) || updateCount != 1 || visitedReads != 2)
                    throw new InvalidOperationException("TMCE search traversal anchors changed.");
                for (int i = 5; i + 9 < code.Count; ++i)
                {
                    int log = i + 3;
                    bool legacyLog = code[log].LoadsConstant(1);
                    if (legacyLog) ++log;
                    int exit = log + 1, tail = log + 2;
                    if (tail + 4 >= code.Count) continue;
                    if (!code[i].LoadsConstant(Capacity.VanillaNodeCapacity) || i <= updateIndex
                        || code[i - 4].opcode != OpCodes.Dup || !code[i - 3].LoadsConstant(1)
                        || code[i - 2].opcode != OpCodes.Add || Local(code[i - 5], true) < 0
                        || Local(code[i - 5], true) != Local(code[i - 1], false)
                        || code[i + 1].opcode != OpCodes.Ble && code[i + 1].opcode != OpCodes.Ble_S
                        || code[i + 2].opcode != OpCodes.Ldstr || !object.Equals(code[i + 2].operand, "Invalid loop detected.")
                        || code[log].opcode != OpCodes.Call || !IsDiagnosticLog(code[log].operand, legacyLog)
                        || code[exit].opcode.FlowControl != FlowControl.Branch
                        || code[tail].opcode != OpCodes.Ldarg_0 || !HasLabel(code[tail], code[i + 1].operand)
                        || code[tail + 1].opcode != OpCodes.Ldfld || !object.Equals(code[tail + 1].operand, queue)
                        || code[tail + 2].opcode != OpCodes.Callvirt || !IsQueueCount(code[tail + 2].operand, queue)
                        || !code[tail + 3].LoadsConstant(0) || code[tail + 4].opcode != OpCodes.Bgt && code[tail + 4].opcode != OpCodes.Bgt_S)
                        continue;
                    int local = Local(code[i - 5], true), initializers = 0, writes = 0, addresses = 0;
                    IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
                    if (local >= locals.Count || locals[local].LocalType != typeof(int)) continue;
                    for (int j = 0; j < code.Count; ++j)
                    {
                        if (Local(code[j], false) == local)
                        { ++writes; if (j > 0 && code[j - 1].LoadsConstant(0) && j < updateIndex) ++initializers; }
                        if (code[j].opcode.Name.StartsWith("ldloca") && OperandLocal(code[j].operand) == local) ++addresses;
                    }
                    int loopStart = LabelIndex(code, code[tail + 4].operand);
                    if (writes != 2 || initializers != 1 || addresses != 0 || loopStart < 0 || loopStart >= updateIndex) continue;
                    match = i; ++count;
                }
            }
            if (count != 1) throw new InvalidOperationException("TMCE capacity anchor missing or ambiguous: " + count);
            code[match].opcode = OpCodes.Ldc_I4; code[match].operand = Capacity.ExpandedNodeCapacity;
            return code;
        }

        private static bool IsQueueCount(object operand, FieldInfo field)
        {
            MethodInfo method = operand as MethodInfo;
            return method != null && field != null && method.DeclaringType == field.FieldType && method.Name == "get_Count"
                && method.ReturnType == typeof(int) && method.GetParameters().Length == 0;
        }
        private static bool IsDiagnosticLog(object operand, bool legacy)
        {
            MethodInfo method = operand as MethodInfo;
            if (method == null || !method.IsStatic || method.ReturnType != typeof(void)) return false;
            ParameterInfo[] parameters = method.GetParameters();
            return legacy ? method.DeclaringType.FullName == "SleepyCommon.CDebug" && method.Name == "Log"
                    && parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(bool)
                : method.DeclaringType.FullName == "SleepyCommon.Log" && method.Name == "Info"
                    && parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
        }
        private static bool HasLabel(CodeInstruction code, object operand)
        { return operand is Label && code.labels.Contains((Label)operand); }
        private static int LabelIndex(List<CodeInstruction> code, object operand)
        { for (int i = 0; i < code.Count; ++i) if (HasLabel(code[i], operand)) return i; return -1; }
        private static int OperandLocal(object value)
        {
            LocalVariableInfo local = value as LocalVariableInfo;
            return local != null ? local.LocalIndex : value is int ? (int)value : value is byte ? (byte)value
                : value is short ? (short)value : -1;
        }
        private static int Local(CodeInstruction code, bool load)
        {
            string prefix = load ? "ldloc" : "stloc";
            string name = code.opcode.Name;
            if (name == prefix + ".0") return 0;
            if (name == prefix + ".1") return 1;
            if (name == prefix + ".2") return 2;
            if (name == prefix + ".3") return 3;
            return name == prefix || name == prefix + ".s" ? OperandLocal(code.operand) : -1;
        }
    }
}
