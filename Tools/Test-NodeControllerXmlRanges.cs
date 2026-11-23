using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Capacity { internal const int VanillaNodeCapacity = 32768, ExpandedNodeCapacity = 65024; }
    internal static class Logging { internal static void Info(string text) { } internal static void Warning(string text) { } internal static void Error(string text) { } }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class NodeControllerFlagChecks { internal static void Reset() { } internal static void Apply(Harmony harmony, Assembly assembly) { } }
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static Assembly FindLoadedAssembly(string name) { return null; }
        internal static Type FindType(Assembly assembly, string name, string mod) { return assembly.GetType(name); }
        internal static FieldInfo FindField(Type type, string name) { return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        internal static FieldInfo FindBackingField(Type type, string name) { return FindField(type, "<" + name + ">k__BackingField"); }
    }
}
internal static class NodeControllerXmlRangeTests
{
    private delegate bool Accept(ushort id);
    private static string[] Paths;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    { foreach (string root in Paths) { string file = Path.Combine(root, new AssemblyName(args.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); } return null; }
    private static List<CodeInstruction> Copy(List<CodeInstruction> original)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in original) result.Add(new CodeInstruction(c)); return result; }
    private static void Refuses(List<CodeInstruction> input, string scenario)
    {
        List<CodeInstruction> before = Copy(input); bool rejected = false;
        try { new List<CodeInstruction>(NodeControllerPatches.FromXmlTranspiler(input)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed XML guard accepted: " + scenario);
        for (int i = 0; i < input.Count; ++i) Check(input[i].opcode == before[i].opcode && object.Equals(input[i].operand, before[i].operand), "Rejected body partly changed");
    }
    public static int Main(string[] args)
    {
        try
        {
            Paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Assembly nc = Assembly.LoadFrom(Path.Combine(args[1], "NodeControllerRenewal.dll"));
            Type manager = nc.GetType("NodeController.Manager", true); MethodInfo method = null;
            foreach (MethodInfo candidate in manager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (candidate.Name == "FromXml" && candidate.GetParameters().Length == 3) { Check(method == null, "Ambiguous XML method"); method = candidate; }
            Check(method != null, "Missing XML method");
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            int guard = original.FindIndex(delegate(CodeInstruction c) { return c.opcode == OpCodes.Ldc_I4 && object.Equals(c.operand, 32768); });
            Check(guard >= 0, "Missing actual XML guard");
            Label label = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            ExceptionBlock block = new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null);
            List<CodeInstruction> input = Copy(original); input[guard].labels.Add(label); input[guard].blocks.Add(block);
            List<CodeInstruction> rewritten = new List<CodeInstruction>(NodeControllerPatches.FromXmlTranspiler(input));
            int changes = 0; for (int i = 0; i < original.Count; ++i) if (!object.Equals(original[i].operand, rewritten[i].operand) || original[i].opcode != rewritten[i].opcode) ++changes;
            Check(changes == 1 && rewritten.Count == original.Count && object.Equals(rewritten[guard].operand, 65023), "Wrong guard rewrite");
            Check(rewritten[guard].labels.Contains(label) && rewritten[guard].blocks.Contains(block), "Guard label/EH lost");
            List<CodeInstruction> broken = Copy(original); broken[guard].operand = 32767; Refuses(broken, "constant");
            broken = Copy(original); broken[guard + 3].operand = typeof(object).GetMethod("ToString", Type.EmptyTypes); Refuses(broken, "node root");
            broken = Copy(original); broken[guard - 12 - 3].operand = "SegmentId"; Refuses(broken, "Id source");
            broken = Copy(original); broken[guard + 1].opcode = OpCodes.Bge; Refuses(broken, "comparison");
            broken = Copy(original); broken[guard - 2].operand = original[guard - 12 + 6].operand; Refuses(broken, "skip target");
            broken = Copy(original); broken.Add(new CodeInstruction(original[guard])); Refuses(broken, "duplicate bound");
            broken = Copy(original); broken.Insert(guard - 12, new CodeInstruction(original[guard - 12])); Refuses(broken, "duplicate source");
            Accept beforeGuard = CompileGuard((int)original[guard].operand, original[guard + 1].opcode);
            Accept afterGuard = CompileGuard((int)rewritten[guard].operand, rewritten[guard + 1].opcode);
            Check(!beforeGuard(0) && !afterGuard(0), "Zero id accepted");
            for (int id = 1; id <= 32768; ++id) Check(beforeGuard((ushort)id) == afterGuard((ushort)id), "Existing low-id semantics changed");
            Check(!beforeGuard(32769) && afterGuard(32769) && afterGuard(65023) && !afterGuard(65024) && !afterGuard(65535), "Expanded inclusive bound incorrect");
            // Buffer expands to65024 for both original and expanded saves. This is
            // intentionally a fixed65023 ceiling, rather than a current vanilla read size.
            Check((int)rewritten[guard].operand == MoreNetworkCapacity.Capacity.ExpandedNodeCapacity - 1, "Vanilla-save load changed NC forced-capacity semantics");
            Console.WriteLine("PASS: actual NC XML ushort Id source/remap/node root/paired skip branches; one inclusive65023 guard, low-id/zero boundaries, labels/EH and atomic changed-pattern refusal."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static Accept CompileGuard(int ceiling, OpCode reject)
    {
        DynamicMethod method = new DynamicMethod("XmlIdGuard", typeof(bool), new Type[] { typeof(ushort) }, typeof(NodeControllerXmlRangeTests).Module, true);
        ILGenerator il = method.GetILGenerator(); Label skip = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, skip); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4, ceiling); il.Emit(reject, skip);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret); il.MarkLabel(skip); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
        return (Accept)method.CreateDelegate(typeof(Accept));
    }
}
