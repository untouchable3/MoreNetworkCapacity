using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Patches;
using MoreNetworkCapacity.Compatibility;

public sealed class LightSaveFixture
{
    public bool[] Values = new bool[65024];
    public bool[] TrafficLightSimulations { get { return Values; } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int SaveData(ref bool success)
    {
        int count = 0;
        for (uint i = 0; i < 32768; ++i) if (TrafficLightSimulations[i]) ++count;
        return count;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void OnLevelUnloading()
    {
        for (uint i = 0; i < 32768; ++i) TrafficLightSimulations[i] = false;
    }
}

public static class HighIdRangeTests
{
    private delegate int SaveFixture(LightSaveFixture instance, ref bool success);
    private delegate void UnloadFixture(LightSaveFixture instance);
    private static string[] paths;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in paths)
        {
            string file = Path.Combine(directory, name);
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Run(); TMPEArrayTests.Run(paths);
            Console.WriteLine("PASS: real game/TMPE/AR IL, atomic mismatch rejection, complete range coverage, executable high-ID saving, unloading and TMPE array lifecycles.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static List<CodeInstruction> Original(MethodBase method)
    { return PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null); }
    private static List<CodeInstruction> Copy(List<CodeInstruction> list)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in list) result.Add(new CodeInstruction(c)); return result; }
    private static void RejectWithoutMutation(List<CodeInstruction> broken, Func<List<CodeInstruction>, List<CodeInstruction>> apply)
    {
        List<CodeInstruction> before = Copy(broken); bool rejected = false;
        try { apply(broken); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed pattern was accepted");
        Check(before.Count == broken.Count, "Rejected input length changed");
        for (int i = 0; i < before.Count; ++i)
            Check(before[i].opcode == broken[i].opcode && object.Equals(before[i].operand, broken[i].operand), "Rejected input mutated");
    }
    private static void Cover(int length, int frames, int slice)
    {
        int[] seen = new int[length];
        for (int f = 0; f < frames; ++f)
            for (int i = f * slice; i < (f + 1) * slice; ++i) { Check(i < length, "Slice exceeds buffer"); ++seen[i]; }
        foreach (int count in seen) Check(count == 1, "Skipped or double simulated ID");
    }
    private static Delegate CompileFixture(string name, Type delegateType)
    {
        MethodInfo original = typeof(LightSaveFixture).GetMethod(name);
        Type[] parameters = name == "SaveData" ? new Type[] { typeof(LightSaveFixture), typeof(bool).MakeByRefType() }
            : new Type[] { typeof(LightSaveFixture) };
        DynamicMethod method = new DynamicMethod(name + "Expanded", original.ReturnType, parameters, typeof(HighIdRangeTests).Module, true);
        ILGenerator generator = method.GetILGenerator();
        foreach (LocalVariableInfo local in original.GetMethodBody().LocalVariables) generator.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = new List<CodeInstruction>(TMPETrafficLightRanges.Transpiler(original,
            PatchProcessor.GetOriginalInstructions(original, generator)));
        foreach (CodeInstruction c in code)
        {
            foreach (Label label in c.labels) generator.MarkLabel(label);
            if (c.operand == null) generator.Emit(c.opcode);
            else if (c.operand is Label) generator.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is Label[]) generator.Emit(c.opcode, (Label[])c.operand);
            else if (c.operand is LocalBuilder) generator.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) generator.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) generator.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is FieldInfo) generator.Emit(c.opcode, (FieldInfo)c.operand);
            else if (c.operand is Type) generator.Emit(c.opcode, (Type)c.operand);
            else if (c.operand is byte) generator.Emit(c.opcode, (byte)c.operand);
            else if (c.operand is sbyte) generator.Emit(c.opcode, (sbyte)c.operand);
            else if (c.operand is short) generator.Emit(c.opcode, (short)c.operand);
            else if (c.operand is int) generator.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected fixture operand " + c.operand);
        }
        return method.CreateDelegate(delegateType);
    }
    private static int[] Refreshed;
    private static bool ValidRefresh(ushort segment) { return segment != 0; }
    private static bool AdaptiveRefresh(NetInfo info) { return true; }
    private static void UpdateRefresh(NetManager manager, ushort segment) { ++Refreshed[segment]; }
    private static void IgnoreLog(object[] data) { }
    private static NetManager NoManager() { return null; }
    private static NetInfo RefreshInfo(ushort segment) { return null; }
    private static Action CompileRefresh(MethodInfo target, List<CodeInstruction> code)
    {
        DynamicMethod method = new DynamicMethod("ActualARRefresh", typeof(void), Type.EmptyTypes, typeof(HighIdRangeTests).Module, true);
        ILGenerator il = method.GetILGenerator();
        foreach (LocalVariableInfo local in target.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        Dictionary<Label, Label> labels = new Dictionary<Label, Label>();
        foreach (CodeInstruction instruction in code)
            foreach (Label label in instruction.labels) if (!labels.ContainsKey(label)) labels.Add(label, il.DefineLabel());
        // Keep all actual loop/control-flow instructions. Stub only the
        // external logger, validity/query and Unity update calls.
        for (int position = 0; position < code.Count; ++position)
        {
            CodeInstruction c = code[position];
            foreach (Label label in c.labels) il.MarkLabel(labels[label]);
            MethodInfo call = c.operand as MethodInfo;
            if (call != null)
            {
                string fake = call.Name == "Called" ? "IgnoreLog" : call.Name == "IsSegmentValid" ? "ValidRefresh"
                    : call.Name == "IsAdaptive" ? "AdaptiveRefresh" : call.Name == "UpdateSegment" ? "UpdateRefresh"
                    : call.Name == "get_instance" && call.ReturnType == typeof(NetManager) ? "NoManager" : null;
                if (fake != null) { il.Emit(OpCodes.Call, typeof(HighIdRangeTests).GetMethod(fake, BindingFlags.Static | BindingFlags.NonPublic)); continue; }
                if (call.Name == "ToSegment")
                {
                    Check(position + 1 < code.Count && ((MethodInfo)code[position + 1].operand).Name == "get_Info"
                        && code[position + 1].labels.Count == 0, "Changed external segment-info fixture calls");
                    il.Emit(OpCodes.Call, typeof(HighIdRangeTests).GetMethod("RefreshInfo", BindingFlags.Static | BindingFlags.NonPublic));
                    ++position; continue;
                }
            }
            if (c.operand == null) il.Emit(c.opcode);
            else if (c.operand is Label) il.Emit(c.opcode, labels[(Label)c.operand]);
            else if (c.operand is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) il.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) il.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is Type) il.Emit(c.opcode, (Type)c.operand);
            else if (c.operand is int) il.Emit(c.opcode, (int)c.operand);
            else if (c.operand is sbyte) il.Emit(c.opcode, (sbyte)c.operand);
            else throw new Exception("Unexpected actual AR refresh operand.");
        }
        return (Action)method.CreateDelegate(typeof(Action));
    }
    private static void TestRefresh(Assembly assembly)
    {
        foreach (string name in new[] { "PWSelector", "PWModifier" })
        {
            MethodInfo target = assembly.GetType("AdaptiveRoads.UI.Debug." + name, true).GetMethod("Refresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            DynamicMethod labels = new DynamicMethod("RefreshLabels", typeof(void), Type.EmptyTypes);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target, labels.GetILGenerator());
            List<CodeInstruction> patched = new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, Copy(original)));
            int changed = 0, bound = -1;
            for (int i = 0; i < original.Count; ++i)
            {
                if (original[i].opcode != patched[i].opcode || !object.Equals(original[i].operand, patched[i].operand)) { ++changed; bound = i; }
                Check(original[i].labels.Count == patched[i].labels.Count && original[i].blocks.Count == patched[i].blocks.Count, "AR refresh lost metadata");
            }
            Check(changed == 1 && bound >= 0, "AR refresh altered unrelated operations");
            Refreshed = new int[65024]; CompileRefresh(target, original)();
            Check(Refreshed[1] == 1 && Refreshed[40000] == 0, "Original AR omission not reproduced");
            Refreshed = new int[65024]; Capacity.SegmentLoopLimit = 65024; CompileRefresh(target, patched)();
            Check(Refreshed[0] == 0, "Invalid zero segment updated");
            for (int i = 1; i < Refreshed.Length; ++i) Check(Refreshed[i] == 1, "High adaptive segment skipped or repeated");
            List<CodeInstruction> broken = Copy(original); broken[bound].operand = 36865;
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            broken = Copy(original); broken[bound - 1].opcode = OpCodes.Ldloc_1;
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            broken = Copy(original); broken[bound + 4].operand = labels.GetILGenerator().DefineLabel();
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            broken = Copy(original);
            foreach (CodeInstruction c in broken)
                if (c.operand is MethodInfo && ((MethodInfo)c.operand).Name == "UpdateSegment")
                { c.operand = typeof(NetManager).GetMethod("UpdateNode", new[] { typeof(ushort) }); break; }
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            broken = Copy(original);
            for (int i = 1; i < broken.Count; ++i)
                if (broken[i].opcode == OpCodes.Stloc_0) { broken[i - 1].opcode = OpCodes.Ldc_I4_1; break; }
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            broken = Copy(original);
            foreach (CodeInstruction c in broken) if (c.opcode == OpCodes.Conv_U2) { c.opcode = OpCodes.Conv_I4; break; }
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, il)); });
            List<CodeInstruction> safe = Copy(original); safe.InsertRange(bound, new[] { new CodeInstruction(OpCodes.Nop, null), new CodeInstruction(OpCodes.Nop, null) });
            new List<CodeInstruction>(AdaptiveRoadsCompatibility.RefreshSegmentsTranspiler(target, safe));
            Console.WriteLine("PASS actual AR " + name + ": all65023 valid adaptive IDs updated, unsafe graph refused and NOPs preserved.");
        }
    }
    private static void Run()
    {
        MethodInfo net = typeof(NetManager).GetMethod("SimulationStepImpl", BindingFlags.Instance | BindingFlags.NonPublic);
        List<CodeInstruction> original = Original(net), patched = new List<CodeInstruction>(NetworkSimulationSlicePatch.Transpiler(Copy(original)));
        int changed = 0;
        for (int i = 0; i < original.Count; ++i)
            if (original[i].opcode != patched[i].opcode) { ++changed; Check(patched[i].opcode == OpCodes.Call, "Non-slice instruction changed"); }
        Check(changed == 4 && patched.Count == original.Count, "Unexpected network changes");
        List<CodeInstruction> broken = Copy(original);
        foreach (CodeInstruction c in broken) if (NetworkSimulationSlicePatch.Constant(c, 128)) { c.operand = 127; break; }
        RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(NetworkSimulationSlicePatch.Transpiler(il)); });
        Capacity.NodeLoopLimit = 32768; Capacity.SegmentLoopLimit = 36864;
        Check(NetworkSimulationSlicePatch.NodeSlice() == 128 && NetworkSimulationSlicePatch.SegmentSlice() == 144, "Vanilla schedule changed");
        Cover(32768, 256, NetworkSimulationSlicePatch.NodeSlice()); Cover(36864, 256, NetworkSimulationSlicePatch.SegmentSlice());
        Capacity.NodeLoopLimit = Capacity.SegmentLoopLimit = 65024;
        Cover(65024, 256, NetworkSimulationSlicePatch.NodeSlice()); Cover(65024, 256, NetworkSimulationSlicePatch.SegmentSlice());
        Cover(32768, 64, 32768 >> 6); Cover(65024, 64, 65024 >> 6);
        Console.WriteLine("Network slices: vanilla 128/144, expanded 254/254; all IDs covered once per 256 frames.");

        Assembly tmpe = Assembly.LoadFrom(Path.Combine(paths[1], "TrafficManager.dll"));
        Type lights = tmpe.GetType("TrafficManager.Manager.Impl.TrafficLightSimulationManager", true);
        foreach (string name in new string[] { "SimulationStep", "SaveData", "OnLevelUnloading" })
        {
            MethodInfo method = lights.GetMethod(name);
            original = Original(method); patched = new List<CodeInstruction>(TMPETrafficLightRanges.Transpiler(method, Copy(original)));
            int size = name == "SimulationStep" ? 512 : 32768;
            foreach (CodeInstruction c in patched) Check(!NetworkSimulationSlicePatch.Constant(c, size), "TMPE fixed bound remains");
            broken = Copy(original);
            foreach (CodeInstruction c in broken) if (NetworkSimulationSlicePatch.Constant(c, size)) { c.operand = size - 1; break; }
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(TMPETrafficLightRanges.Transpiler(method, il)); });
            // Incoming branch labels must stay attached to the replacement start.
            List<CodeInstruction> labelled = Copy(original);
            Label label = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            int index = labelled.FindIndex(delegate(CodeInstruction c) { return NetworkSimulationSlicePatch.Constant(c, size); });
            labelled[index].labels.Add(label);
            patched = new List<CodeInstruction>(TMPETrafficLightRanges.Transpiler(method, labelled));
            Check(patched[index].opcode == OpCodes.Ldarg_0 && patched[index].labels.Contains(label), "TMPE label lost");
            Console.WriteLine("TMPE " + name + ": original IL matched, changed pattern rejected.");
        }

        Assembly ar = Assembly.LoadFrom(Path.Combine(paths[2], "AdaptiveRoads.dll"));
        TestRefresh(ar);
        foreach (string name in new string[] { "AdaptiveRoads.Manager.NetLaneExt", "AdaptiveRoads.Data.NetworkExtensions.LaneTransition" })
        {
            MethodInfo method = ar.GetType(name, true).GetMethod("GenerateRenderData");
            original = Original(method); patched = new List<CodeInstruction>(AdaptiveRoadsCompatibility.TrackNodeColorsTranspiler(method, Copy(original)));
            int calls = 0;
            foreach (CodeInstruction c in patched)
            {
                Check(!NetworkSimulationSlicePatch.Constant(c, 86016), "AR old node base remains");
                MethodInfo operand = c.operand as MethodInfo;
                if (operand != null && operand.DeclaringType == typeof(Capacity) && operand.Name == "GetNodeColorIndexBase") ++calls;
            }
            Check(calls == (name.EndsWith("NetLaneExt") ? 2 : 1), "AR wrong replacement count");
            broken = Copy(original);
            foreach (CodeInstruction c in broken) if (NetworkSimulationSlicePatch.Constant(c, 86016)) { c.operand = 86015; break; }
            RejectWithoutMutation(broken, delegate(List<CodeInstruction> il) { return new List<CodeInstruction>(AdaptiveRoadsCompatibility.TrackNodeColorsTranspiler(method, il)); });
            Console.WriteLine(name + ": validated node-base replacements=" + calls);
        }
        Check(Capacity.GetNodeColorIndexBase() == 114176, "Expanded color base wrong");

        LightSaveFixture fixture = new LightSaveFixture(); fixture.Values[1] = fixture.Values[32768] = fixture.Values[64512] = true;
        bool success = true;
        Check(fixture.SaveData(ref success) == 1, "Fixture did not reproduce high-ID omission");
        SaveFixture save = (SaveFixture)CompileFixture("SaveData", typeof(SaveFixture));
        UnloadFixture unload = (UnloadFixture)CompileFixture("OnLevelUnloading", typeof(UnloadFixture));
        Check(save(fixture, ref success) == 3, "Patched save skipped high-ID data");
        unload(fixture); Check(!fixture.Values[1] && !fixture.Values[32768] && !fixture.Values[64512], "Unload skipped high IDs");
        fixture.Values = new bool[32768]; fixture.Values[1] = true;
        Check(save(fixture, ref success) == 1, "Vanilla array save failed after resize");
        unload(fixture); Check(!fixture.Values[1], "Vanilla unload failed");
    }
}
