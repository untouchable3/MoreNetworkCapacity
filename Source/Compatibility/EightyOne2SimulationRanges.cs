using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2SimulationRanges
    {
        private static bool _installed;
        internal static void Reset()
        {
            _installed = false;
            EightyOne2WaterRecovery.Reset();
            EightyOne2ElectricityRecovery.Reset();
        }

        internal static int NodeCount()
        {
            // Follow the same activation gate as the game's node simulation.
            // Main-menu installation must not create manager singletons.
            int count = Capacity.NodeLoopLimit;
            if (count != Capacity.VanillaNodeCapacity && count != Capacity.ExpandedNodeCapacity)
                throw new InvalidOperationException("Unsupported 81 Tiles node simulation capacity: " + count);
            return count;
        }

        internal static int SegmentCount()
        {
            int count = Capacity.SegmentLoopLimit;
            if (count != Capacity.VanillaSegmentCapacity && count != Capacity.ExpandedSegmentCapacity)
                throw new InvalidOperationException("Unsupported 81 Tiles segment traversal capacity: " + count);
            return count;
        }

        internal static MethodInfo ResolveWaterGrid(Assembly assembly)
        {
            Type type = assembly.GetType("EightyOne2.Patches.WaterManagerPatches", false);
            MethodInfo method = type == null ? null : type.GetMethod("UpdateGrid", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(WaterManager), typeof(float), typeof(float), typeof(float), typeof(float), typeof(WaterManager.Cell[]) }, null);
            if (method == null || method.ReturnType != typeof(bool)) throw new InvalidOperationException("81 Tiles water grid target changed.");
            return method;
        }

        internal static MethodInfo[] Resolve(Assembly assembly)
        {
            string[] names = { "ExpandedElectricityManager", "ExpandedWaterManager" };
            int[] parameters = { 9, 18 };
            MethodInfo[] result = new MethodInfo[2];
            for (int i = 0; i < names.Length; ++i)
            {
                Type type = assembly.GetType("EightyOne2.Patches." + names[i], false);
                if (type == null) throw new InvalidOperationException("81 Tiles simulation type missing: " + names[i]);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    ParameterInfo[] args = method.GetParameters();
                    if (method.Name != "SimulationStepImpl" || args.Length != parameters[i]
                        || args[0].ParameterType != (i == 0 ? typeof(ElectricityManager) : typeof(WaterManager))
                        || args[1].ParameterType != typeof(int) || method.ReturnType != typeof(void)) continue;
                    if (result[i] != null) throw new InvalidOperationException("Ambiguous 81 Tiles simulation target.");
                    result[i] = method;
                }
                if (result[i] == null) throw new InvalidOperationException("81 Tiles simulation signature changed: " + names[i]);
            }
            return result;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (_installed || harmony == null) return;
            MethodInfo[] targets = Resolve(assembly);
            MethodInfo waterGrid = ResolveWaterGrid(assembly);
            MethodInfo patch = AccessTools.Method(typeof(EightyOne2SimulationRanges), "Transpiler", null, null);
            MethodInfo gridPatch = AccessTools.Method(typeof(EightyOne2SimulationRanges), "WaterGridTranspiler", null, null);
            // Check both original bodies before installing either patch.
            foreach (MethodInfo target in targets)
                new List<CodeInstruction>(Transpiler(target, PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null)));
            new List<CodeInstruction>(WaterGridTranspiler(waterGrid, PatchProcessor.GetOriginalInstructions(waterGrid, (ILGenerator)null)));
            MethodInfo[] recoveryTargets = EightyOne2WaterRecovery.Resolve(targets[1]);
            Type recovery = typeof(EightyOne2WaterRecovery);
            MethodInfo recoveryPatch = AccessTools.Method(recovery, "Transpiler", null, null);
            MethodInfo recoveryPrefix = AccessTools.Method(recovery, "Prefix", null, null);
            MethodInfo recoveryFinalizer = AccessTools.Method(recovery, "Finalizer", null, null);
            foreach (MethodInfo target in recoveryTargets)
                new List<CodeInstruction>(EightyOne2WaterRecovery.Transpiler(target, PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null)));
            MethodInfo electricity = targets[0];
            MethodInfo electricityRoot = EightyOne2ElectricityRecovery.ResolveRoot(electricity);
            Type powerRecovery = typeof(EightyOne2ElectricityRecovery);
            MethodInfo electricityPatch = AccessTools.Method(powerRecovery, "Transpiler", null, null);
            MethodInfo electricityRootPatch = AccessTools.Method(powerRecovery, "RootTranspiler", null, null);
            MethodInfo electricityPrefix = AccessTools.Method(powerRecovery, "Prefix", null, null);
            MethodInfo electricityFinalizer = AccessTools.Method(powerRecovery, "Finalizer", null, null);
            new List<CodeInstruction>(EightyOne2ElectricityRecovery.Transpiler(electricity,
                PatchProcessor.GetOriginalInstructions(electricity, (ILGenerator)null)));
            ILGenerator rootGenerator;
            List<CodeInstruction> rootCode = PatchProcessor.GetOriginalInstructions(electricityRoot, out rootGenerator);
            new List<CodeInstruction>(EightyOne2ElectricityRecovery.RootTranspiler(electricityRoot, rootCode, rootGenerator));
            List<MethodInfo> attempted = new List<MethodInfo>();
            List<MethodInfo> attemptedRecovery = new List<MethodInfo>();
            try
            {
                // Install the group helper before generating its simulation callers.
                harmony.Patch(electricityRoot, null, null, new HarmonyMethod(electricityRootPatch), null);
                foreach (MethodInfo target in targets)
                {
                    attempted.Add(target);
                    harmony.Patch(target, null, null, new HarmonyMethod(patch), null);
                }
                harmony.Patch(waterGrid, null, null, new HarmonyMethod(gridPatch), null);
                foreach (MethodInfo target in recoveryTargets)
                {
                    attemptedRecovery.Add(target);
                    bool simulation = target == targets[1];
                    harmony.Patch(target, simulation ? new HarmonyMethod(recoveryPrefix) : null, null,
                        new HarmonyMethod(recoveryPatch), simulation ? new HarmonyMethod(recoveryFinalizer) : null);
                }
                harmony.Patch(electricity, new HarmonyMethod(electricityPrefix), null,
                    new HarmonyMethod(electricityPatch), new HarmonyMethod(electricityFinalizer));
                _installed = true;
                Logging.Info("81 Tiles node simulation and water segment-grid traversal follow active capacities; 256-frame phases preserved.");
                Logging.Info("81 Tiles water, sewage, heating and electricity pulse recovery installed.");
            }
            catch
            {
                harmony.Unpatch(electricity, electricityPatch);
                harmony.Unpatch(electricity, electricityPrefix);
                harmony.Unpatch(electricity, electricityFinalizer);
                foreach (MethodInfo target in attemptedRecovery) harmony.Unpatch(target, recoveryPatch);
                if (attemptedRecovery.Count > 0)
                {
                    harmony.Unpatch(targets[1], recoveryPrefix);
                    harmony.Unpatch(targets[1], recoveryFinalizer);
                }
                harmony.Unpatch(waterGrid, gridPatch);
                foreach (MethodInfo target in attempted) harmony.Unpatch(target, patch);
                harmony.Unpatch(electricityRoot, electricityRootPatch);
                throw;
            }
        }

        // 81 Tiles replaces the complete WaterManager.UpdateGrid routine. Its
        // linked-list counter is independent of the simulated node slices.
        internal static IEnumerable<CodeInstruction> WaterGridTranspiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            if (original == null || !ResolveWaterGrid(original.DeclaringType.Assembly).Equals(original))
                throw new InvalidOperationException("Unexpected 81 Tiles water-grid target.");
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            List<int> map;
            List<CodeInstruction> code = WithoutNops(list, out map);
            if (!LocalsAgree(original, code)) throw new InvalidOperationException("81 Tiles grid local metadata changed.");
            int match = -1, found = 0, constants = 0;
            FieldInfo next = typeof(NetSegment).GetField("m_nextGridSegment");
            FieldInfo poolField = typeof(NetManager).GetField("m_segments");
            FieldInfo gridField = typeof(NetManager).GetField("m_segmentGrid");
            FieldInfo buffer = typeof(Array16<NetSegment>).GetField("m_buffer");
            MethodInfo singleton = typeof(ColossalFramework.Singleton<NetManager>).GetProperty("instance").GetGetMethod();
            foreach (CodeInstruction instruction in code) if (Constant(instruction, Capacity.VanillaNodeCapacity)) ++constants;
            for (int i = 11; i + 24 < code.Count; ++i)
            {
                if (!Constant(code[i], Capacity.VanillaNodeCapacity)) continue;
                int pool = Local(code[i - 11], true), cursor = Local(code[i - 9], true), count = Local(code[i - 5], true);
                int condition = Local(code[i + 4], false);
                if (pool < 0 || cursor < 0 || count < 0 || condition < 0
                    || original.GetMethodBody().LocalVariables[pool].LocalType != typeof(Array16<NetSegment>)
                    || original.GetMethodBody().LocalVariables[cursor].LocalType != typeof(ushort)
                    || original.GetMethodBody().LocalVariables[count].LocalType != typeof(int)
                    || original.GetMethodBody().LocalVariables[condition].LocalType != typeof(bool)
                    || code[i - 10].opcode != OpCodes.Ldfld || !buffer.Equals(code[i - 10].operand)
                    || code[i - 8].opcode != OpCodes.Ldelema || code[i - 8].operand as Type != typeof(NetSegment)
                    || code[i - 7].opcode != OpCodes.Ldfld || !next.Equals(code[i - 7].operand)
                    || Local(code[i - 6], false) != cursor || !Constant(code[i - 4], 1)
                    || code[i - 3].opcode != OpCodes.Add || code[i - 2].opcode != OpCodes.Dup || Local(code[i - 1], false) != count
                    || code[i + 1].opcode != OpCodes.Clt || !Constant(code[i + 2], 0) || code[i + 3].opcode != OpCodes.Ceq
                    || Local(code[i + 5], true) != condition || !Branch(code[i + 6], false)) continue;
                // The successful guard must rejoin the same nonzero cursor
                // predicate used by the initial entry. The failure logs then
                // exits the list, never entering the next grid cell body.
                int nonzero = Destination(code, code[i + 6]);
                if (nonzero < i + 7 || nonzero + 5 >= code.Count
                    || Local(code[nonzero], true) != cursor || !Constant(code[nonzero + 1], 0)
                    || code[nonzero + 2].opcode != OpCodes.Cgt_Un || Local(code[nonzero + 3], false) < 0
                    || Local(code[nonzero + 4], true) != Local(code[nonzero + 3], false) || !Branch(code[nonzero + 5], true)
                    || !Constant(code[i + 7], 1) || code[i + 8].opcode != OpCodes.Ldstr
                    || (string)code[i + 8].operand != "Invalid list detected!\n"
                    || !Call(code[i + 9], typeof(Environment), "get_StackTrace")
                    || !Call(code[i + 10], typeof(string), "Concat")
                    || code[i + 11].opcode != OpCodes.Call || !Unconditional(code[i + 12])
                    || Destination(code, code[i + 12]) != nonzero + 6) continue;
                MethodInfo error = code[i + 11].operand as MethodInfo;
                if (error == null || error.Name != "Error" || !error.IsStatic || error.ReturnType != typeof(void)) continue;
                int body = Destination(code, code[nonzero + 5]);
                if (body < 0 || body >= i - 11 || Local(code[body], true) != pool
                    || code[body + 1].opcode != OpCodes.Ldfld || !buffer.Equals(code[body + 1].operand)
                    || Local(code[body + 2], true) != cursor || code[body + 3].opcode != OpCodes.Ldelema
                    || code[body + 3].operand as Type != typeof(NetSegment)) continue;
                int seed = -1, grid = -1;
                for (int j = 8; j + 3 < body; ++j)
                    if (Local(code[j], false) == cursor && code[j - 1].opcode == OpCodes.Ldelem_U2
                        && code[j - 2].opcode == OpCodes.Add && code[j - 4].opcode == OpCodes.Mul
                        && Constant(code[j - 5], 270) && Local(code[j - 7], true) >= 0
                        && Constant(code[j + 1], 0) && Local(code[j + 2], false) == count
                        && Unconditional(code[j + 3]) && Destination(code, code[j + 3]) == nonzero)
                    { if (seed >= 0) { seed = -2; break; } seed = j; grid = Local(code[j - 7], true); }
                if (seed < 0 || !SingleRoot(code, pool, singleton, poolField) || !SingleRoot(code, grid, singleton, gridField)
                    || !OnlyStores(code, pool, 1) || !OnlyStores(code, grid, 1)
                    || !OnlyStores(code, cursor, 2) || !OnlyStores(code, count, 2)
                    || Addressed(code, pool) || Addressed(code, grid) || Addressed(code, cursor) || Addressed(code, count)) continue;
                match = i; ++found;
            }
            if (constants != 1 || found != 1) throw new InvalidOperationException("81 Tiles water segment-grid mechanism changed.");
            MethodInfo helper = AccessTools.Method(typeof(EightyOne2SimulationRanges), "SegmentCount", null, null);
            list[map[match]].opcode = OpCodes.Call; list[map[match]].operand = helper;
            return list;
        }

        internal static List<CodeInstruction> WithoutNops(List<CodeInstruction> list, out List<int> map)
        {
            map = new List<int>(); List<CodeInstruction> code = new List<CodeInstruction>(); List<Label> pending = new List<Label>();
            for (int index = 0; index < list.Count; ++index)
            {
                CodeInstruction item = list[index];
                if (item.opcode == OpCodes.Nop) { pending.AddRange(item.labels); continue; }
                CodeInstruction copy = new CodeInstruction(item); copy.labels.AddRange(pending); pending.Clear();
                map.Add(index); code.Add(copy);
            }
            return code;
        }
        internal static int Destination(List<CodeInstruction> code, CodeInstruction branch)
        {
            if (!(branch.operand is Label)) return -1;
            Label label = (Label)branch.operand;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains(label)) return i;
            return -1;
        }
        internal static bool Branch(CodeInstruction code, bool yes) { return code.opcode == (yes ? OpCodes.Brtrue : OpCodes.Brfalse) || code.opcode == (yes ? OpCodes.Brtrue_S : OpCodes.Brfalse_S); }
        internal static bool Unconditional(CodeInstruction code) { return code.opcode == OpCodes.Br || code.opcode == OpCodes.Br_S; }
        internal static bool Call(CodeInstruction code, Type type, string name)
        { MethodInfo method = code.operand as MethodInfo; return code.opcode == OpCodes.Call && method != null && method.DeclaringType == type && method.Name == name; }
        internal static bool SingleRoot(List<CodeInstruction> code, int local, MethodInfo singleton, FieldInfo field)
        {
            int found = 0;
            for (int i = 2; i < code.Count; ++i)
                if (Local(code[i], false) == local && code[i - 1].opcode == OpCodes.Ldfld && field.Equals(code[i - 1].operand)
                    && code[i - 2].opcode == OpCodes.Call && singleton.Equals(code[i - 2].operand)) ++found;
            return found == 1;
        }
        internal static bool OnlyStores(List<CodeInstruction> code, int local, int count)
        { int found = 0; foreach (CodeInstruction c in code) if (Local(c, false) == local) ++found; return found == count; }
        internal static bool Addressed(List<CodeInstruction> code, int local)
        {
            foreach (CodeInstruction c in code) if (c.opcode == OpCodes.Ldloca || c.opcode == OpCodes.Ldloca_S)
            { LocalVariableInfo info = c.operand as LocalVariableInfo; if (info == null || info.LocalIndex == local) return true; }
            return false;
        }
        internal static bool LocalsAgree(MethodBase original, List<CodeInstruction> code)
        {
            IList<LocalVariableInfo> declared = original.GetMethodBody().LocalVariables;
            foreach (CodeInstruction c in code)
            {
                if (!c.opcode.Name.StartsWith("ldloc") && !c.opcode.Name.StartsWith("stloc")) continue;
                LocalVariableInfo info = c.operand as LocalVariableInfo;
                int index = c.opcode == OpCodes.Ldloca || c.opcode == OpCodes.Ldloca_S ? (info == null ? -1 : info.LocalIndex)
                    : Local(c, c.opcode.Name.StartsWith("ldloc"));
                if (index < 0 || index >= declared.Count || info != null && info.LocalType != declared[index].LocalType) return false;
            }
            return true;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            if (original == null || original.Name != "SimulationStepImpl" || original.DeclaringType == null
                || !original.IsStatic || (original.DeclaringType.FullName != "EightyOne2.Patches.ExpandedElectricityManager"
                && original.DeclaringType.FullName != "EightyOne2.Patches.ExpandedWaterManager"))
                throw new InvalidOperationException("Unexpected 81 Tiles simulation target.");
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int phase = -1, phases = 0;
            for (int i = 0; i + 8 < list.Count; ++i)
            {
                if (!Constant(list[i], 255) || list[i + 1].opcode != OpCodes.And
                    || Local(list[i + 2], false) < 0 || Local(list[i + 3], true) != Local(list[i + 2], false)
                    || !Constant(list[i + 4], 128) || list[i + 5].opcode != OpCodes.Clt
                    || Local(list[i + 6], false) < 0 || Local(list[i + 7], true) != Local(list[i + 6], false)
                    || (list[i + 8].opcode != OpCodes.Brfalse && list[i + 8].opcode != OpCodes.Brfalse_S)) continue;
                phase = Local(list[i + 2], false); ++phases;
            }
            int range = -1, ranges = 0, capacities = 0;
            for (int i = 0; i < list.Count; ++i)
            {
                if (!Constant(list[i], Capacity.VanillaNodeCapacity)) continue;
                ++capacities;
                if (i < 1 || i + 14 >= list.Count || Local(list[i - 1], true) != phase
                    || list[i + 1].opcode != OpCodes.Mul || !Constant(list[i + 2], 7) || list[i + 3].opcode != OpCodes.Shr
                    || Local(list[i + 4], false) < 0 || Local(list[i + 5], true) != phase
                    || !Constant(list[i + 6], 1) || list[i + 7].opcode != OpCodes.Add
                    || !Constant(list[i + 8], Capacity.VanillaNodeCapacity) || list[i + 9].opcode != OpCodes.Mul
                    || !Constant(list[i + 10], 7) || list[i + 11].opcode != OpCodes.Shr
                    || !Constant(list[i + 12], 1) || list[i + 13].opcode != OpCodes.Sub
                    || Local(list[i + 14], false) < 0
                    || Local(list[i + 4], false) == Local(list[i + 14], false)) continue;
                range = i; ++ranges;
            }
            bool nodes = false, state = false, update = false;
            bool electricity = original.DeclaringType.Name == "ExpandedElectricityManager";
            foreach (CodeInstruction instruction in list)
            {
                FieldInfo field = instruction.operand as FieldInfo;
                if (instruction.opcode == OpCodes.Ldfld && field != null)
                {
                    nodes |= field.DeclaringType == typeof(NetManager) && field.Name == "m_nodes";
                    state |= field.DeclaringType == (electricity ? typeof(ElectricityManager) : typeof(WaterManager))
                        && field.Name == (electricity ? "m_nodeGroups" : "m_nodeData");
                }
                MethodInfo method = instruction.operand as MethodInfo;
                update |= instruction.opcode == OpCodes.Call && method != null && method.DeclaringType == original.DeclaringType
                    && method.Name == (electricity ? "UpdateNodeElectricity" : "UpdateNodeWater");
            }
            if (phases != 1 || ranges != 1 || capacities != 2 || !nodes || !state || !update)
                throw new InvalidOperationException("81 Tiles node simulation range pattern changed: " + original.DeclaringType.FullName);
            MethodInfo helper = AccessTools.Method(typeof(EightyOne2SimulationRanges), "NodeCount", null, null);
            if (helper == null) throw new InvalidOperationException("81 Tiles node count helper missing.");
            // Change only the two multipliers, preserving labels, EH boundaries,
            // grid slices, pulse queues, the propagation half and all node work.
            list[range].opcode = list[range + 8].opcode = OpCodes.Call;
            list[range].operand = list[range + 8].operand = helper;
            return list;
        }

        private static bool Constant(CodeInstruction instruction, int value)
        {
            if (value == 0 && instruction.opcode == OpCodes.Ldc_I4_0) return true;
            if (value == 7 && instruction.opcode == OpCodes.Ldc_I4_7) return true;
            return NetworkSimulationSlicePatch.Constant(instruction, value);
        }
        internal static int Local(CodeInstruction instruction, bool load) { return NetworkSimulationSlicePatch.Local(instruction, load); }
    }
}
