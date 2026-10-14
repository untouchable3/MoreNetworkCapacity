using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Logging { internal static void Info(string message) { Console.WriteLine(message); } }
}

internal static class NodeControllerFlagTests
{
    private static string[] _paths;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static List<CodeInstruction> Copy(List<CodeInstruction> original)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in original) result.Add(new CodeInstruction(c)); return result; }
    private static Assembly LoadDependency(object sender, ResolveEventArgs args)
    {
        string file = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in _paths)
        { string path = Path.Combine(directory, file); if (File.Exists(path)) return Assembly.LoadFrom(path); }
        return null;
    }
    public static int Main(string[] paths)
    {
        try
        {
            _paths = paths; AppDomain.CurrentDomain.AssemblyResolve += LoadDependency;
            Assembly nc = Assembly.LoadFrom(Path.Combine(paths[1], "NodeControllerRenewal.dll"));
            MethodInfo target = NodeControllerFlagChecks.Resolve(nc);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null);
            int call = original.FindIndex(delegate(CodeInstruction c) {
                MethodInfo method = c.operand as MethodInfo;
                return method != null && method.DeclaringType == typeof(ColossalFramework.EnumExtensions) && method.Name == "IsFlagSet";
            });
            Check(call >= 0, "Actual corner flag check missing");
            List<CodeInstruction> input = Copy(original);
            Label label = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            ExceptionBlock block = new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null);
            input[call].labels.Add(label); input[call].blocks.Add(block);
            List<CodeInstruction> patched = new List<CodeInstruction>(NodeControllerFlagChecks.Transpiler(target, input));
            Check(patched.Count == original.Count, "Instruction count changed");
            int changes = 0;
            for (int i = 0; i < original.Count; ++i)
                if (!object.Equals(original[i].operand, patched[i].operand) || original[i].opcode != patched[i].opcode) ++changes;
            Check(changes == 1 && patched[call].opcode == OpCodes.Call, "More than exact call changed");
            Check(patched[call].labels.Contains(label) && patched[call].blocks.Contains(block), "Label or exception boundary lost");
            List<CodeInstruction> broken = Copy(original);
            broken[call - 1].operand = (sbyte)16;
            List<CodeInstruction> before = Copy(broken); bool rejected = false;
            try { new List<CodeInstruction>(NodeControllerFlagChecks.Transpiler(target, broken)); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Changed Middle constant accepted");
            for (int i = 0; i < before.Count; ++i)
                Check(before[i].opcode == broken[i].opcode && object.Equals(before[i].operand, broken[i].operand), "Refusal mutated body");
            broken = Copy(original); broken.Insert(call, new CodeInstruction(original[call])); rejected = false;
            try { new List<CodeInstruction>(NodeControllerFlagChecks.Transpiler(target, broken)); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Duplicate generic check accepted");
            // Compare the real game extension, including zero, mixed masks and the high bit.
            uint[] bits = { 0, 1, 16, 32, 48, 32768, 65536, 0x80000000u, 0xffffffffu };
            foreach (uint value in bits) foreach (uint mask in bits)
            {
                NetNode.Flags flags = (NetNode.Flags)value, flag = (NetNode.Flags)mask;
                Check(ColossalFramework.EnumExtensions.IsFlagSet<NetNode.Flags>(flags, flag)
                    == NodeControllerFlagChecks.AnyNodeFlag(flags, flag), "Real game flag semantics changed");
            }
            for (int bit = 0; bit < 32; ++bit)
                Check(NodeControllerFlagChecks.AnyNodeFlag((NetNode.Flags)(1u << bit), (NetNode.Flags)(1u << bit)), "Single bit lost");
            Console.WriteLine("PASS: actual NC corner IL, exactly one typed replacement, any-bit game equivalence, labels/EH preservation and atomic pattern refusal.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
