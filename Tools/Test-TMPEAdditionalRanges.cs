using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

public struct AdditionalGridSegment { public ushort m_nextGridSegment; }
public sealed class TMPEAdditionalFixture
{
    public bool[] records = new bool[65024];
    public int buildings;
    public ushort[] m_segmentGrid = new ushort[1];
    public AdditionalGridSegment[] segments = new AdditionalGridSegment[65024];
    public bool AddCompleteSegment(ushort id) { return records[id]; }
    public int RecordAll()
    {
        int count = 0;
        for (ushort id = 0; id < 36864; ++id) if (AddCompleteSegment(id)) ++count;
        return count;
    }
    public int GetPathsNetworkIDs()
    {
        buildings = 0;
        for (ushort id = 1; id < 49152; ++id) ++buildings;
        int count = 0;
        for (ushort id = 0; id < 36864; ++id) if (AddCompleteSegment(id)) ++count;
        return count;
    }
    public int Traverse()
    {
        ushort id = m_segmentGrid[0]; int steps = 0;
        while (id != 0)
        {
            id = segments[id].m_nextGridSegment;
            if (++steps >= 36864) { Console.Error.WriteLine("Invalid list detected!"); return -steps; }
        }
        return steps;
    }
}
public static class TMPEAdditionalTests
{
    private delegate int Run(TMPEAdditionalFixture fixture);
    private static string[] paths;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string directory in paths)
        {
            string path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    private static List<CodeInstruction> Copy(List<CodeInstruction> input)
    { List<CodeInstruction> output = new List<CodeInstruction>(); foreach (CodeInstruction c in input) output.Add(new CodeInstruction(c)); return output; }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Assembly assembly = Assembly.LoadFrom(Path.Combine(paths[1], "TrafficManager.dll"));
            foreach (TMPEAdditionalRanges.Target target in TMPEAdditionalRanges.Targets)
            {
                MethodInfo method = TMPEAdditionalRanges.Resolve(assembly, target);
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                List<CodeInstruction> patched = TMPEAdditionalRanges.ApplyTarget(method, target, Copy(original));
                int changed = 0, constant = -1;
                for (int i = 0; i < original.Count; ++i)
                {
                    if (NetworkSimulationSlicePatch.Constant(original[i], 36864)) constant = i;
                    if (original[i].opcode != patched[i].opcode) ++changed;
                    if (NetworkSimulationSlicePatch.Constant(original[i], 49152))
                        Check(NetworkSimulationSlicePatch.Constant(patched[i], 49152), "Building capacity changed");
                }
                Check(changed == 1 && patched.Count == original.Count, "Unexpected change count");
                List<CodeInstruction> broken = Copy(original); broken[constant].operand = 36863;
                Reject(method, target, broken);
                broken = Copy(original); broken[constant + 1].opcode = OpCodes.Bge;
                Reject(method, target, broken);
                if (target.Guard)
                {
                    broken = Copy(original); broken[constant - 2].opcode = OpCodes.Pop;
                    Reject(method, target, broken);
                }
                Label incoming = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
                List<CodeInstruction> labelled = Copy(original); labelled[constant].labels.Add(incoming);
                patched = TMPEAdditionalRanges.ApplyTarget(method, target, labelled);
                Check(patched[constant].labels.Contains(incoming), "Label lost");
                Console.WriteLine("Matched: " + target.Type + "." + target.Method);
            }
            Run records = Compile("RecordAll", false), pathsExport = Compile("GetPathsNetworkIDs", false), traverse = Compile("Traverse", true);
            TMPEAdditionalFixture fixture = new TMPEAdditionalFixture();
            fixture.records[1] = fixture.records[40000] = fixture.records[65023] = true;
            Check(fixture.RecordAll() == 1, "Asset omission fixture changed");
            Capacity.SegmentLoopLimit = 65024;
            Check(records(fixture) == 3 && pathsExport(fixture) == 3 && fixture.buildings == 49151, "High asset metadata skipped or building loop changed");
            Capacity.SegmentLoopLimit = 36864;
            Check(records(fixture) == 1 && pathsExport(fixture) == 1, "Vanilla asset range changed");
            foreach (int count in new int[] { 36863, 65023 })
            {
                Capacity.SegmentLoopLimit = count + 1;
                fixture.m_segmentGrid[0] = 1;
                for (int id = 1; id <= count; ++id) fixture.segments[id].m_nextGridSegment = (ushort)(id == count ? 0 : id + 1);
                Check(traverse(fixture) == count, "Valid expanded chain terminated early");
                fixture.segments[count].m_nextGridSegment = 1;
                Check(traverse(fixture) == -(count + 1), "Cycle guard removed or wrong cap");
            }
            Capacity.SegmentLoopLimit = 65024;
            fixture.m_segmentGrid[0] = 65023; fixture.segments[65023].m_nextGridSegment = 0;
            Check(fixture.Traverse() == 1 && traverse(fixture) == 1, "Guard incorrectly treated as ID limit");
            Console.WriteLine("PASS: 4 real methods, atomic pattern rejection/labels, high-ID asset exports, unchanged building pool, expanded linked chains and retained corruption guard.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Reject(MethodBase method, TMPEAdditionalRanges.Target target, List<CodeInstruction> broken)
    {
        List<CodeInstruction> before = Copy(broken); bool rejected = false;
        try { TMPEAdditionalRanges.ApplyTarget(method, target, broken); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Broken pattern accepted");
        for (int i = 0; i < broken.Count; ++i) Check(before[i].opcode == broken[i].opcode && Object.Equals(before[i].operand, broken[i].operand), "Rejected code partially modified");
    }
    private static Run Compile(string name, bool guard)
    {
        MethodInfo original = typeof(TMPEAdditionalFixture).GetMethod(name);
        DynamicMethod method = new DynamicMethod(name + "Expanded", typeof(int), new Type[] { typeof(TMPEAdditionalFixture) }, typeof(TMPEAdditionalTests).Module, true);
        ILGenerator il = method.GetILGenerator();
        foreach (LocalVariableInfo local in original.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        TMPEAdditionalRanges.Target target = new TMPEAdditionalRanges.Target("fixture", name, 0, guard ? "m_nextGridSegment" : "AddCompleteSegment", guard);
        List<CodeInstruction> code = TMPEAdditionalRanges.ApplyTarget(original, target, PatchProcessor.GetOriginalInstructions(original, il));
        foreach (CodeInstruction c in code)
        {
            foreach (Label label in c.labels) il.MarkLabel(label);
            if (c.operand == null) il.Emit(c.opcode);
            else if (c.operand is Label) il.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) il.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) il.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is FieldInfo) il.Emit(c.opcode, (FieldInfo)c.operand);
            else if (c.operand is Type) il.Emit(c.opcode, (Type)c.operand);
            else if (c.operand is string) il.Emit(c.opcode, (string)c.operand);
            else if (c.operand is byte) il.Emit(c.opcode, (byte)c.operand);
            else if (c.operand is sbyte) il.Emit(c.opcode, (sbyte)c.operand);
            else if (c.operand is short) il.Emit(c.opcode, (short)c.operand);
            else if (c.operand is int) il.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected operand " + c.operand);
        }
        return (Run)method.CreateDelegate(typeof(Run));
    }
}
