using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal const int VanillaNodeCapacity = 32768, ExpandedNodeCapacity = 65024;
        internal static int NodeLoopLimit = 32768, SegmentLoopLimit = 36864;
    }
    internal static class Logging { internal static void Info(string text) { Console.WriteLine(text); } }
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
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            Assembly assembly = Assembly.LoadFrom(Path.Combine(paths[1], "EightyOne2.dll"));
            Console.WriteLine("Target: " + assembly.FullName + " MVID=" + assembly.ManifestModule.ModuleVersionId);
            foreach (MethodInfo target in EightyOne2SimulationRanges.Resolve(assembly)) Test(target);
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
