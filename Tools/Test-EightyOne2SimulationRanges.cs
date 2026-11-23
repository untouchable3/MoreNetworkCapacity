using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal const int VanillaNodeCapacity = 32768, ExpandedNodeCapacity = 65024;
        internal const int VanillaSegmentCapacity = 36864, ExpandedSegmentCapacity = 65024;
        internal static int NodeLoopLimit = 32768, SegmentLoopLimit = 36864;
    }
    internal static class Logging
    {
        internal static void Info(string text) { Console.WriteLine(text); }
        internal static void Warning(string text) { Console.WriteLine(text); }
    }
}

internal static class EightyOne2SimulationRangeTests
{
    private static string[] paths;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in paths)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    private static List<CodeInstruction> Copy(List<CodeInstruction> code)
    {
        List<CodeInstruction> result = new List<CodeInstruction>();
        foreach (CodeInstruction item in code) result.Add(new CodeInstruction(item));
        return result;
    }
    private static void Reject(MethodInfo target, List<CodeInstruction> code)
    {
        List<CodeInstruction> before = Copy(code); bool failed = false;
        try { new List<CodeInstruction>(EightyOne2SimulationRanges.Transpiler(target, code)); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Changed range pattern accepted.");
        Check(before.Count == code.Count, "Rejected pattern changed length.");
        for (int i = 0; i < code.Count; ++i)
            Check(before[i].opcode == code[i].opcode && object.Equals(before[i].operand, code[i].operand), "Rejected pattern mutated.");
    }
    private static Func<int, int> CompileFormula(List<CodeInstruction> code, int range, bool end)
    {
        // Execute the actual transformed multiplier/shift/subtraction sequence
        // without invoking Unity managers, simulation AI or native detours.
        DynamicMethod method = new DynamicMethod("NodeRange", typeof(int), new Type[] { typeof(int) }, typeof(EightyOne2SimulationRangeTests).Module, true);
        ILGenerator il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
        int first = end ? range + 6 : range;
        int last = end ? range + 13 : range + 3;
        for (int i = first; i <= last; ++i)
        {
            CodeInstruction instruction = code[i];
            if (instruction.operand == null) il.Emit(instruction.opcode);
            else if (instruction.operand is MethodInfo) il.Emit(instruction.opcode, (MethodInfo)instruction.operand);
            else if (instruction.operand is int) il.Emit(instruction.opcode, (int)instruction.operand);
            else if (instruction.operand is sbyte) il.Emit(instruction.opcode, (sbyte)instruction.operand);
            else throw new Exception("Unexpected formula operand.");
        }
        il.Emit(OpCodes.Ret);
        return (Func<int, int>)method.CreateDelegate(typeof(Func<int, int>));
    }
    private static void Cover(Func<int, int> start, Func<int, int> end, int size)
    {
        Capacity.NodeLoopLimit = size;
        int[] visits = new int[size];
        for (int frame = 0; frame < 256; ++frame)
        {
            int phase = frame & 255;
            // The unchanged original guard reserves the second half for
            // propagation. The tested formulas run only in the first half.
            if (phase >= 128) continue;
            for (int node = start(phase); node <= end(phase); ++node)
            {
                Check(node >= 0 && node < size, "Range escapes active pool.");
                ++visits[node];
            }
        }
        foreach (int count in visits) Check(count == 1, "Skipped or repeated node in complete 256-frame cycle.");
        Check(start(0) == 0 && end(127) == size - 1, "Boundary nodes missing.");
    }
    private static void Test(MethodInfo target)
    {
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null);
        int range = -1;
        for (int i = 0; i < original.Count; ++i)
            if (NetworkSimulationSlicePatch.Constant(original[i], 32768)) { range = i; break; }
        Check(range >= 0, "Expected original node multiplier missing.");
        // Explicit metadata is retained on the exact original instructions.
        DynamicMethod labels = new DynamicMethod("Labels", typeof(void), Type.EmptyTypes);
        original[range].labels.Add(labels.GetILGenerator().DefineLabel());
        original[range].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
        List<CodeInstruction> code = new List<CodeInstruction>(EightyOne2SimulationRanges.Transpiler(target, Copy(original)));
        Check(code.Count == original.Count, "Instructions inserted into node routine.");
        int changed = 0;
        for (int i = 0; i < code.Count; ++i)
        {
            bool same = original[i].opcode == code[i].opcode && object.Equals(original[i].operand, code[i].operand);
            if (!same)
            {
                ++changed; Check(i == range || i == range + 8, "Non-multiplier instruction altered.");
                Check(code[i].opcode == OpCodes.Call && ((MethodInfo)code[i].operand).Name == "NodeCount", "Wrong multiplier replacement.");
            }
            Check(original[i].labels.Count == code[i].labels.Count && original[i].blocks.Count == code[i].blocks.Count, "Labels/EH not preserved.");
            for (int j = 0; j < original[i].labels.Count; ++j) Check(original[i].labels[j].Equals(code[i].labels[j]), "Label identity changed.");
        }
        Check(changed == 2, "Wrong replacement count.");
        Func<int, int> start = CompileFormula(code, range, false), end = CompileFormula(code, range, true);
        Cover(start, end, 32768); Cover(start, end, 65024); Cover(start, end, 32768);
        List<CodeInstruction> broken = Copy(original); broken[range + 8].operand = 32769; Reject(target, broken);
        broken = Copy(original); broken[range + 2].opcode = OpCodes.Ldc_I4_6; Reject(target, broken);
        broken = Copy(original);
        for (int i = 0; i < broken.Count; ++i)
            if (NetworkSimulationSlicePatch.Constant(broken[i], 255)) { broken[i].operand = 127; break; }
        Reject(target, broken);
        broken = Copy(original); broken.Add(new CodeInstruction(OpCodes.Ldc_I4, 32768)); Reject(target, broken);
        Console.WriteLine("PASS actual IL: " + target.DeclaringType.FullName + "::" + target.Name);
    }
    private static Func<int, bool> GridPredicate(List<CodeInstruction> code, int index)
    {
        DynamicMethod method = new DynamicMethod("GridGuard", typeof(bool), new[] { typeof(int) }, typeof(EightyOne2SimulationRangeTests).Module, true);
        ILGenerator il = method.GetILGenerator(); il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_0);
        for (int i = index - 4; i <= index + 1; ++i)
        {
            CodeInstruction c = code[i];
            if (c.opcode.Name.StartsWith("stloc")) il.Emit(OpCodes.Stloc_0);
            else if (c.operand == null) il.Emit(c.opcode);
            else if (c.operand is MethodInfo) il.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is int) il.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected actual grid predicate operand.");
        }
        il.Emit(OpCodes.Ret); return (Func<int, bool>)method.CreateDelegate(typeof(Func<int, bool>));
    }
    private static void RejectGrid(MethodInfo target, List<CodeInstruction> broken)
    {
        List<CodeInstruction> before = Copy(broken); bool rejected = false;
        try { new List<CodeInstruction>(EightyOne2SimulationRanges.WaterGridTranspiler(target, broken)); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unsafe water-grid pattern accepted.");
        for (int i = 0; i < before.Count; ++i)
            Check(before[i].opcode == broken[i].opcode && object.Equals(before[i].operand, broken[i].operand), "Rejected water grid mutated.");
    }
    private static void TestGrid(Assembly assembly)
    {
        MethodInfo target = EightyOne2SimulationRanges.ResolveWaterGrid(assembly);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null);
        List<CodeInstruction> code = new List<CodeInstruction>(EightyOne2SimulationRanges.WaterGridTranspiler(target, Copy(original)));
        int index = -1, changed = 0;
        for (int i = 0; i < code.Count; ++i)
        {
            if (original[i].opcode != code[i].opcode || !object.Equals(original[i].operand, code[i].operand)) { ++changed; index = i; }
            Check(original[i].labels.Count == code[i].labels.Count && original[i].blocks.Count == code[i].blocks.Count, "Grid metadata lost.");
        }
        Check(changed == 1, "Water grid changed unrelated instructions.");
        Func<int, bool> old = GridPredicate(original, index), expanded = GridPredicate(code, index);
        Capacity.SegmentLoopLimit = 65024;
        int oldVisits = 0, newVisits = 0;
        for (int i = 0; i < 40000; ++i) { ++oldVisits; if (!old(i)) break; }
        for (int i = 0; i < 40000; ++i) { ++newVisits; if (!expanded(i)) break; }
        Check(oldVisits == 32768 && newVisits == 40000, "Actual predicate did not remove valid long-chain truncation.");
        Check(expanded(65022) && !expanded(65023), "Corrupt-cycle guard no longer terminates at active capacity.");
        Capacity.SegmentLoopLimit = 36864; Check(expanded(36862) && !expanded(36863), "Vanilla segment guard incorrect.");
        List<CodeInstruction> safe = Copy(original); safe.InsertRange(index, new[] { new CodeInstruction(OpCodes.Nop, null), new CodeInstruction(OpCodes.Nop, null) });
        new List<CodeInstruction>(EightyOne2SimulationRanges.WaterGridTranspiler(target, safe));
        List<CodeInstruction> broken = Copy(original); broken[index].operand = 32769; RejectGrid(target, broken);
        broken = Copy(original); broken[index - 7].operand = typeof(NetSegment).GetField("m_nextLane"); RejectGrid(target, broken);
        broken = Copy(original); broken[index - 1].operand = original[index - 6].operand; RejectGrid(target, broken);
        broken = Copy(original); broken[index + 6].operand = original[index + 12].operand; RejectGrid(target, broken);
        broken = Copy(original); broken[index - 7].operand = typeof(NetNode).GetField("m_nextGridNode"); RejectGrid(target, broken);
        broken = Copy(original);
        for (int i = 0; i + 1 < index; ++i)
            if (broken[i].opcode == OpCodes.Ldc_I4_0 && broken[i + 1].opcode == original[index - 1].opcode
                && object.Equals(broken[i + 1].operand, original[index - 1].operand)) { broken[i].opcode = OpCodes.Ldc_I4_1; break; }
        RejectGrid(target, broken);
        broken = Copy(original);
        for (int i = index + 7; i < broken.Count; ++i)
            if (broken[i].opcode == OpCodes.Cgt_Un) { broken[i].opcode = OpCodes.Ceq; break; }
        RejectGrid(target, broken);
        broken = Copy(original); broken.Add(new CodeInstruction(OpCodes.Ldc_I4, 32768)); RejectGrid(target, broken);
        Console.WriteLine("PASS actual 81 Tiles water-grid traversal: 40000 visits, corrupt-cycle bound, NOP preservation and atomic unsafe-pattern refusal.");
    }
    private static Exception RootFailure(ushort group, WaterManager.PulseGroup[] groups, int count)
    {
        try { EightyOne2WaterRecovery.FindRoot(group, groups, count); }
        catch (Exception exception) { return exception; }
        throw new Exception("An invalid pulse chain was accepted.");
    }

    private static void TestPulseRecovery(MethodInfo simulation)
    {
        MethodInfo[] targets = EightyOne2WaterRecovery.Resolve(simulation);
        for (int i = 0; i < targets.Length; ++i)
        {
            MethodInfo target = targets[i];
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null);
            List<CodeInstruction> patched = new List<CodeInstruction>(EightyOne2WaterRecovery.Transpiler(target, original));
            int lookups = 0;
            for (int j = 0; j < patched.Count; ++j)
            {
                MethodInfo call = patched[j].operand as MethodInfo;
                if (call == null || call.DeclaringType != typeof(EightyOne2WaterRecovery) || call.Name != "FindRoot") continue;
                int argument = i == 0 ? 3 + 3 * lookups : 5;
                Check(patched[j - (i == 0 ? 2 : 1)].opcode == OpCodes.Ldarg_S
                    && (byte)patched[j - (i == 0 ? 2 : 1)].operand == argument, "Wrong active pulse group count passed.");
                if (i == 0) Check(patched[j - 1].opcode == OpCodes.Ldind_I4, "Group count ref was not read.");
                ++lookups;
            }
            Check(lookups == (i == 0 ? 3 : 1), "Actual pulse lookup coverage changed.");
        }

        WaterManager.PulseGroup[][] services = { new WaterManager.PulseGroup[1024], new WaterManager.PulseGroup[1024], new WaterManager.PulseGroup[1024] };
        WaterManager manager = (WaterManager)FormatterServices.GetUninitializedObject(typeof(WaterManager));
        manager.m_nodeData = new WaterManager.Node[65024];
        WaterManager.Cell[] cells = new WaterManager.Cell[2];
        for (int i = 0; i < services.Length; ++i)
        {
            WaterManager.PulseGroup[] groups = services[i];
            groups[0].m_mergeIndex = 1;
            groups[1].m_mergeIndex = ushort.MaxValue;
            Check(EightyOne2WaterRecovery.FindRoot(0, groups, 2) == 1, "Valid merged group changed.");
            RootFailure(1024, groups, groups.Length);
            RootFailure(ushort.MaxValue, groups, 2);
            RootFailure(0, groups, 1);
            groups[1].m_mergeIndex = 0;
            RootFailure(0, groups, 2);
            groups[1].m_mergeIndex = 1024;
            Exception invalid = RootFailure(0, groups, groups.Length);

            manager.m_nodeData[65023].m_curWaterPressure = 42;
            manager.m_nodeData[65023].m_waterPulseGroup = 9;
            cells[1].m_hasWater = true;
            cells[1].m_tmpHasSewage = true;
            cells[1].m_currentWaterPressure = -11;
            int waterCount = 2, waterStart = 1, waterEnd = 3, sewageCount = 3, sewageStart = 2, sewageEnd = 4;
            int heatingCount = 4, heatingStart = 3, heatingEnd = 5, processed = 5, conductive = 6;
            bool canContinue = true;
            Exception unrelated = new IndexOutOfRangeException();
            Check(ReferenceEquals(EightyOne2WaterRecovery.Finalizer(unrelated, manager, cells,
                ref waterCount, ref waterStart, ref waterEnd, ref sewageCount, ref sewageStart, ref sewageEnd,
                ref heatingCount, ref heatingStart, ref heatingEnd, ref processed, ref conductive, ref canContinue,
                services[0], services[1], services[2]), unrelated) && waterCount == 2 && canContinue,
                "An unrelated simulation exception or its state was changed.");
            Check(EightyOne2WaterRecovery.Finalizer(invalid, manager, cells,
                ref waterCount, ref waterStart, ref waterEnd, ref sewageCount, ref sewageStart, ref sewageEnd,
                ref heatingCount, ref heatingStart, ref heatingEnd, ref processed, ref conductive, ref canContinue,
                services[0], services[1], services[2]) == null, "Invalid pulse chain did not trigger recovery.");
            Check(waterCount == 0 && waterStart == 0 && waterEnd == 0 && sewageCount == 0 && sewageStart == 0 && sewageEnd == 0
                && heatingCount == 0 && heatingStart == 0 && heatingEnd == 0 && processed == 0 && conductive == 0 && !canContinue,
                "Recovery left a partial simulation cycle active.");
            Check(manager.m_nodeData[65023].m_waterPulseGroup == ushort.MaxValue && manager.m_nodeData[65023].m_curWaterPressure == 42
                && cells[1].m_sewagePulseGroup == ushort.MaxValue && cells[1].m_hasWater && cells[1].m_tmpHasWater
                && !cells[1].m_tmpHasSewage && cells[1].m_currentWaterPressure == -11,
                "Recovery changed pressure or last published supply.");
            Check(EightyOne2WaterRecovery.Prefix(0, cells) && EightyOne2WaterRecovery.Prefix(1, new WaterManager.Cell[1]),
                "Recovery state leaked into another water grid.");
        }
        Check(!EightyOne2WaterRecovery.CanRestart(255) && EightyOne2WaterRecovery.CanRestart(256)
            && !EightyOne2WaterRecovery.CanRestart(257), "Recovery did not align with the native cycle.");
        Console.WriteLine("PASS actual 81 Tiles utility lookup sites, invalid entry/parent/cycle, and isolated transient recovery.");
    }

    private static Exception ElectricityFailure(Action operation, string expected)
    {
        try { operation(); }
        catch (Exception exception)
        {
            Check(exception.GetType().DeclaringType == typeof(EightyOne2ElectricityRecovery) && exception.Message.Contains(expected),
                "Unexpected failure from the electricity pulse check: " + exception);
            return exception;
        }
        throw new Exception("An invalid electricity pulse state was accepted.");
    }

    private static void TestElectricityRecovery(MethodInfo simulation)
    {
        MethodInfo root = EightyOne2ElectricityRecovery.ResolveRoot(simulation);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(simulation, (ILGenerator)null);
        List<CodeInstruction> patched = new List<CodeInstruction>(EightyOne2ElectricityRecovery.Transpiler(simulation,
            EightyOne2SimulationRanges.Transpiler(simulation, Copy(original))));
        Dictionary<string, int> checks = new Dictionary<string, int>();
        foreach (CodeInstruction instruction in patched)
        {
            MethodInfo call = instruction.operand as MethodInfo;
            if (call == null || call.DeclaringType != typeof(EightyOne2ElectricityRecovery)) continue;
            int count;
            checks.TryGetValue(call.Name, out count);
            checks[call.Name] = count + 1;
        }
        Check(checks["ValidateIndex"] == 3 && checks["ValidateParent"] == 1 && checks["ValidateQueueIndex"] == 4,
            "The electricity simulation checks did not match the installed assembly.");
        EightyOne2ElectricityRecovery.ValidateQueueIndex(32767, 32768);
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateQueueIndex(32768, 32768); }, "pulse queue index value 32768");
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateQueueIndex(-1, 32768); }, "pulse queue index value -1");
        ReplayLoadedElectricity(simulation.DeclaringType);
        EightyOne2ElectricityRecovery.ValidateLoadedParents(new ushort[] { 2, 0, ushort.MaxValue, 2 });
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateLoadedParents(new ushort[]
            { 2, 0, 1, 5, 3, 4, 8, 6, 7, 10, 1, 1 }); }, "saved cyclic merge group value 0");
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateLoadedParents(new ushort[]
            { 2, ushort.MaxValue }); }, "saved merge parent value 2");

        ILGenerator generator;
        List<CodeInstruction> rootCode = PatchProcessor.GetOriginalInstructions(root, out generator);
        List<CodeInstruction> guardedRoot = new List<CodeInstruction>(EightyOne2ElectricityRecovery.RootTranspiler(root, rootCode, generator));
        Check(guardedRoot.Count == rootCode.Count + 8, "Root traversal checks did not cover both reads.");

        Array groups = new ushort[65024], units = new ushort[65024];
        EightyOne2ElectricityRecovery.ValidateState(groups.Length, units.Length, 1024, 65023, 0);
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateState(groups.Length, units.Length, 1025, 0, 0); }, "group count");
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateState(groups.Length, units.Length, 1, 65024, 0); }, "queue start");
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateState(groups.Length, units.Length, 1, 0, -1); }, "queue end");
        EightyOne2ElectricityRecovery.ValidateIndex(65023, units);
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateIndex(65024, units); }, "65024");
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateParent(8, 8); }, "merge parent");
        int hops = 0;
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateGroup(49249, ref hops); }, "root input group value 49249");
        EightyOne2ElectricityRecovery.ValidateGroup(1023, ref hops);
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateGroup(38767, ref hops); }, "root merge parent after 1 links value 38767");
        hops = 1024;
        ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateGroup(0, ref hops); }, "cyclic");

        ElectricityManager manager = (ElectricityManager)FormatterServices.GetUninitializedObject(typeof(ElectricityManager));
        manager.m_nodeGroups = new ushort[65024];
        ElectricityManager.Cell[] cells = new ElectricityManager.Cell[462 * 462];
        int last = cells.Length - 1;
        cells[last].m_electrified = true;
        cells[last].m_currentCharge = 7;
        cells[last].m_extraCharge = 18;
        Exception failure = ElectricityFailure(delegate { EightyOne2ElectricityRecovery.ValidateIndex(cells.Length, cells); }, "Cell");
        int groupCount = 1, start = 3, end = 4, processed = 5, conductive = 6;
        bool canContinue = true;
        Exception unrelated = new IndexOutOfRangeException();
        Check(ReferenceEquals(EightyOne2ElectricityRecovery.Finalizer(unrelated, manager, cells,
            ref groupCount, ref start, ref end, ref processed, ref conductive, ref canContinue), unrelated)
            && groupCount == 1 && canContinue, "An unrelated exception was intercepted.");
        Check(EightyOne2ElectricityRecovery.Finalizer(failure, manager, cells,
            ref groupCount, ref start, ref end, ref processed, ref conductive, ref canContinue) == null
            && groupCount == 0 && start == 0 && end == 0 && processed == 0 && conductive == 0 && !canContinue,
            "Electricity recovery did not invalidate the entire temporary cycle.");
        Check(manager.m_nodeGroups[65023] == ushort.MaxValue && cells[last].m_pulseGroup == ushort.MaxValue
            && cells[last].m_electrified && cells[last].m_tmpElectrified
            && cells[last].m_currentCharge == 7 && cells[last].m_extraCharge == 18, "Recovery lost the published service or charge.");
        Console.WriteLine("PASS actual 81 Tiles electricity IL, corrupt group/queue/cell/node bounds, and isolated cycle recovery.");
    }

    private static void ReplayLoadedElectricity(Type owner)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo groupsField = owner.GetField("s_pulseGroups", flags);
        FieldInfo unitsField = owner.GetField("s_pulseUnits", flags);
        Array groups = Array.CreateInstance(groupsField.FieldType.GetElementType(), 65024);
        Array units = Array.CreateInstance(unitsField.FieldType.GetElementType(), 65024);
        groupsField.SetValue(null, groups);
        unitsField.SetValue(null, units);
        FieldInfo parent = groups.GetType().GetElementType().GetField("m_mergeIndex");
        ushort[] savedParents = { 2, 0, 1, 5, 3, 4, 8, 6, 7, 10, 1, 1 };
        for (int i = 0; i < savedParents.Length; ++i)
        {
            object group = groups.GetValue(i);
            parent.SetValue(group, savedParents[i]);
            groups.SetValue(group, i);
        }

        ElectricityManager manager = (ElectricityManager)FormatterServices.GetUninitializedObject(typeof(ElectricityManager));
        manager.m_nodeGroups = new ushort[65024];
        ElectricityManager.Cell[] grid = new ElectricityManager.Cell[2];
        grid[1].m_electrified = true;
        int count = 12, start = 0, end = 12, processed = 5, conductive = 6;
        bool canContinue = true;
        EightyOne2ElectricityRecovery.Reset();
        Check(!EightyOne2ElectricityRecovery.Prefix(manager, 1, grid, ref count, ref start, ref end,
            ref processed, ref conductive, ref canContinue), "Corrupt saved cycle entered propagation.");
        Check(count == 0 && start == 0 && end == 0 && processed == 0 && conductive == 0 && !canContinue
            && grid[1].m_electrified && grid[1].m_tmpElectrified, "Loaded cycle recovery lost state.");
        Check(EightyOne2ElectricityRecovery.Prefix(manager, 1, new ElectricityManager.Cell[2], ref count, ref start,
            ref end, ref processed, ref conductive, ref canContinue), "A new grid inherited the abandoned cycle.");
        EightyOne2ElectricityRecovery.Reset();
    }

    public static int Main(string[] args)
    {
        try
        {
            NetInfo.LodValue lod = (NetInfo.LodValue)FormatterServices.GetUninitializedObject(typeof(NetInfo.LodValue));
            lod.m_lodCount = 3;
            lod.m_leftMatrices = new UnityEngine.Matrix4x4[8];
            MethodInfo matrix = typeof(UnityEngine.MaterialPropertyBlock).GetMethod("SetMatrixArray",
                new[] { typeof(int), typeof(UnityEngine.Matrix4x4[]) });
            Exception unrelated = new IndexOutOfRangeException();
            Exception missing = new NullReferenceException();
            Check(ReferenceEquals(NetworkLodRenderPatch.FinishFailedBatch(unrelated, lod, matrix), unrelated)
                && ReferenceEquals(NetworkLodRenderPatch.FinishFailedBatch(missing, lod, typeof(object).GetMethod("ToString")), missing)
                && lod.m_lodCount == 3, "Unrelated rendering failure was suppressed.");
            Check(NetworkLodRenderPatch.FinishFailedBatch(missing, lod, matrix) == null && lod.m_lodCount == 0
                && lod.m_leftMatrices.Length == 8, "Failed road batch did not preserve reusable buffers.");
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            Assembly assembly = Assembly.LoadFrom(Path.Combine(paths[1], "EightyOne2.dll"));
            Console.WriteLine("Target: " + assembly.FullName + " MVID=" + assembly.ManifestModule.ModuleVersionId);
            MethodInfo[] targets = EightyOne2SimulationRanges.Resolve(assembly);
            foreach (MethodInfo target in targets) Test(target);
            TestGrid(assembly);
            TestPulseRecovery(targets[1]);
            TestElectricityRecovery(targets[0]);
            Capacity.NodeLoopLimit = 65536; bool rejected = false;
            try { EightyOne2SimulationRanges.NodeCount(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unknown active capacity accepted.");
            Capacity.NodeLoopLimit = 32768;
            Console.WriteLine("PASS: original 81 Tiles node ranges, executable coverage, unchanged propagation/grid/queue instructions, mismatch refusal and metadata preservation.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
