using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace ColossalFramework { public static class Singleton<T> { public static bool exists = true; } }
namespace MoreNetworkCapacity
{
    internal static class Logging { internal static void Info(string text) { Console.WriteLine(text); } }
    internal static class Capacity
    {
        internal const int VanillaNodeCapacity = 32768, VanillaSegmentCapacity = 36864, ExpandedNodeCapacity = 65024;
        internal static int Nodes = 65024, Segments = 65024;
        internal static int GetNodeBufferLength() { return Nodes; }
        internal static int GetSegmentBufferLength() { return Segments; }
    }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    { internal static Assembly FindLoadedAssembly(string name) { return null; } }
}
namespace ModsCommon
{
    public static class SingletonManager<T> where T : new() { private static readonly T Value = new T(); public static T Instance { get { return Value; } } public static bool Exist { get { return ImtFixtures.Exists; } } }
}
namespace ModsCommon.Utilities
{
    public static class NetExtension { public static int GetSegment(ushort id) { return id; } }
}
namespace IMT.Manager
{
    public class Marking { public void RecalculateAllStyleData() { ++ImtFixtures.Visits; } public void Update() { ++ImtFixtures.Visits; } }
    public sealed class NodeMarking : Marking { }
    public sealed class SegmentMarking : Marking { }
    public class MarkingManager<T> where T : Marking, new()
    {
        public bool TryGetMarking(ushort id, out T marking)
        { marking = null; if (id != 12 && id != 65023 && id != (typeof(T) == typeof(NodeMarking) ? 32768 : 36864)) return false; marking = new T(); return true; }
    }
    public sealed class NodeMarkingManager : MarkingManager<NodeMarking> { }
    public sealed class SegmentMarkingManager : MarkingManager<SegmentMarking> { }
    public sealed class ChevronFillerStyle { public bool Invert { get { ++ImtFixtures.Visits; return true; } } }
}
namespace IMT.Tools
{
    public sealed class IntersectionMarkingTool
    { public static void ApplyLinkedMarking(int id) { if (id == 12 || id == 36864 || id == 65023) ++ImtFixtures.Visits; } }
}
public static class ImtFixtures
{
    public static int Visits; public static bool Exists = true;
    public static void Invert()
    {
        for (int i = 0; i < 32768; ++i) { IMT.Manager.NodeMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.NodeMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) { bool value = new IMT.Manager.ChevronFillerStyle().Invert; } }
        for (int i = 0; i < 36864; ++i) { IMT.Manager.SegmentMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.SegmentMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) { bool value = new IMT.Manager.ChevronFillerStyle().Invert; } }
    }
    public static void Recalculate()
    {
        for (int i = 0; i < 32768; ++i) { IMT.Manager.NodeMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.NodeMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) marking.RecalculateAllStyleData(); }
        for (int i = 0; i < 36864; ++i) { IMT.Manager.SegmentMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.SegmentMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) marking.RecalculateAllStyleData(); }
    }
    public static void Theme()
    {
        if (ModsCommon.SingletonManager<IMT.Manager.NodeMarkingManager>.Exist)
            for (int i = 0; i < 32768; ++i) { IMT.Manager.NodeMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.NodeMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) marking.Update(); }
        if (ModsCommon.SingletonManager<IMT.Manager.SegmentMarkingManager>.Exist)
            for (int i = 0; i < 36864; ++i) { IMT.Manager.SegmentMarking marking; if (ModsCommon.SingletonManager<IMT.Manager.SegmentMarkingManager>.Instance.TryGetMarking((ushort)i, out marking)) marking.Update(); }
    }
    public static void Preset() { for (ushort i = 0; i < 36864; ++i) { int segment = ModsCommon.Utilities.NetExtension.GetSegment(i); IMT.Tools.IntersectionMarkingTool.ApplyLinkedMarking(segment); } }
}
internal static class IntersectionMarkingRangeTests
{
    private static string[] Paths;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    { foreach (string root in Paths) { string file = Path.Combine(root, new AssemblyName(args.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); } return null; }
    private static List<CodeInstruction> Copy(List<CodeInstruction> original)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in original) result.Add(new CodeInstruction(c)); return result; }
    public static int Main(string[] args)
    {
        try
        {
            Paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            IntersectionMarkingCompatibility.Reset();
            IntersectionMarkingCompatibility.Apply(new Harmony("mnc.imt.absent-assembly-fixture"));
            Check(!(bool)typeof(IntersectionMarkingCompatibility).GetField("_installed", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null),
                "An absent assembly was marked installed, preventing later lifecycle retry");
            Assembly assembly = Assembly.LoadFrom(Path.Combine(args[1], "IntersectionMarkingTool.dll"));
            string[] fixtures = { "Invert", "Recalculate", "Theme", "Preset" };
            for (int n = 0; n < IntersectionMarkingCompatibility.Targets.Length; ++n)
            {
                IntersectionMarkingCompatibility.Target target = IntersectionMarkingCompatibility.Targets[n];
                MethodInfo method = IntersectionMarkingCompatibility.Resolve(assembly, target);
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                List<CodeInstruction> input = Copy(original);
                int constant = input.FindIndex(delegate(CodeInstruction c) { return c.opcode == OpCodes.Ldc_I4 && object.Equals(c.operand, 36864); });
                ILGenerator labelGenerator = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator();
                Label label = new Label(); for (int j = 0; j < 1024; ++j) label = labelGenerator.DefineLabel();
                ExceptionBlock block = new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null);
                input[constant].labels.Add(label); input[constant].blocks.Add(block);
                List<CodeInstruction> result = IntersectionMarkingCompatibility.ApplyTarget(method, target, input);
                Check(result[constant].labels.Contains(label) && result[constant].blocks.Contains(block), "Boundary lost");
                int changes = 0; for (int i = 0; i < original.Count; ++i) if (original[i].opcode != result[i].opcode) ++changes;
                Check(changes == (n < 3 ? 2 : 1) && result.Count == original.Count, "Wrong change count");
                List<CodeInstruction> broken = Copy(original); broken[constant].operand = 36863;
                Refuses(method, target, broken);
                broken = Copy(original); broken[constant + 1].opcode = OpCodes.Bge_S; Refuses(method, target, broken);
                broken = Copy(original);
                for (int i = 0; i < broken.Count; ++i)
                {
                    MethodInfo member = broken[i].operand as MethodInfo;
                    if (member != null && member.Name == (n < 3 ? "TryGetMarking" : "GetSegment"))
                    { broken[i].operand = typeof(ImtFixtures).GetMethod("Preset"); break; }
                }
                Refuses(method, target, broken);
                int firstBound = original.FindIndex(delegate(CodeInstruction c) { return c.opcode == OpCodes.Ldc_I4 && object.Equals(c.operand, n < 3 ? 32768 : 36864); });
                int counter = Local(original[firstBound - 1], false), seed = -1;
                for (int j = 1; j < firstBound; ++j)
                    if (Local(original[j], true) == counter && original[j - 1].opcode == OpCodes.Ldc_I4_0) { seed = j - 1; break; }
                Check(seed >= 0, "Expected zero seed missing");
                broken = Copy(original); broken[seed].opcode = OpCodes.Ldc_I4_1; Refuses(method, target, broken);
                int increment = firstBound - (n < 3 ? 5 : 6);
                broken = Copy(original); broken.InsertRange(increment, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, counter) }); Refuses(method, target, broken);
                DynamicMethod metadata = new DynamicMethod("WrongCounterMetadata", typeof(void), Type.EmptyTypes);
                LocalBuilder wrong = null; for (int j = 0; j <= counter; ++j) wrong = metadata.GetILGenerator().DeclareLocal(typeof(bool));
                broken = Copy(original); broken[firstBound - 1].opcode = OpCodes.Ldloc_S; broken[firstBound - 1].operand = wrong; Refuses(method, target, broken);
                broken = Copy(original); broken.InsertRange(increment, new[] { new CodeInstruction(OpCodes.Ldloca, method.GetMethodBody().LocalVariables[counter]),
                    new CodeInstruction(OpCodes.Call, typeof(IntersectionMarkingRangeTests).GetMethod(n < 3 ? "MutateCounter" : "MutateShortCounter")) }); Refuses(method, target, broken);
                broken = Copy(original);
                foreach (CodeInstruction c in broken) { MethodInfo member = c.operand as MethodInfo; if (member != null && member.Name == target.Anchor) c.opcode = OpCodes.Ldtoken; }
                Refuses(method, target, broken);
                broken = Copy(original); broken[increment].labels.Add(label); broken.Insert(0, new CodeInstruction(OpCodes.Br, label)); Refuses(method, target, broken);
                broken = Copy(original); broken[increment + 1].labels.Add(label); broken.Insert(increment, new CodeInstruction(OpCodes.Br, label)); Refuses(method, target, broken);
                if (n < 2)
                {
                    MethodInfo operation = null;
                    foreach (CodeInstruction c in original) { MethodInfo member = c.operand as MethodInfo; if (member != null && member.Name == target.Anchor) operation = member; }
                    DynamicMethod noWork = new DynamicMethod("NoWork", operation.ReturnType, new[] { operation.DeclaringType });
                    if (operation.ReturnType == typeof(bool)) noWork.GetILGenerator().Emit(OpCodes.Ldc_I4_0);
                    noWork.GetILGenerator().Emit(OpCodes.Ret);
                    broken = Copy(original);
                    foreach (CodeInstruction c in broken)
                        if (c.operand is MethodInfo && ((MethodInfo)c.operand).Name == target.Anchor) { c.opcode = OpCodes.Call; c.operand = noWork; }
                    Label resume = labelGenerator.DefineLabel();
                    List<CodeInstruction> prelude = new List<CodeInstruction> { new CodeInstruction(OpCodes.Br, resume), new CodeInstruction(OpCodes.Ldnull, null), new CodeInstruction(OpCodes.Callvirt, operation) };
                    if (operation.ReturnType != typeof(void)) prelude.Add(new CodeInstruction(OpCodes.Pop, null));
                    CodeInstruction begin = new CodeInstruction(OpCodes.Nop, null); begin.labels.Add(resume); prelude.Add(begin);
                    broken.InsertRange(0, prelude); Refuses(method, target, broken);
                    // The real operation can also remain physically in the loop
                    // yet be unreachable after an unconditional exit.
                    broken = Copy(original);
                    int operationIndex = broken.FindIndex(delegate(CodeInstruction c) { return object.Equals(c.operand, operation); });
                    Label skipOperation = labelGenerator.DefineLabel();
                    broken[operationIndex + 1].labels.Add(skipOperation);
                    broken.Insert(operationIndex, new CodeInstruction(OpCodes.Br, skipOperation));
                    // Invert has two get_Invert calls per loop; skipping one is
                    // insufficient, so replace every other operation with NoWork.
                    for (int j = operationIndex + 2; j < broken.Count; ++j)
                        if (object.Equals(broken[j].operand, operation)) { broken[j].opcode = OpCodes.Call; broken[j].operand = noWork; }
                    Refuses(method, target, broken);
                }
                MethodInfo fixture = typeof(ImtFixtures).GetMethod(fixtures[n]);
                ImtFixtures.Visits = 0; fixture.Invoke(null, null);
                Check(ImtFixtures.Visits == (n < 3 ? 2 : 1), "Fixture does not reproduce omission");
                Action run = Compile(fixture, method, target);
                ImtFixtures.Visits = 0; run(); Check(ImtFixtures.Visits == (n < 3 ? 6 : 3), "High-ID entries omitted");
                MoreNetworkCapacity.Capacity.Nodes = 32768; MoreNetworkCapacity.Capacity.Segments = 36864;
                ImtFixtures.Visits = 0; run(); Check(ImtFixtures.Visits == (n < 3 ? 2 : 1), "Vanilla changed");
                MoreNetworkCapacity.Capacity.Nodes = MoreNetworkCapacity.Capacity.Segments = 65024;
                if (n == 2) { ImtFixtures.Exists = false; ImtFixtures.Visits = 0; run(); Check(ImtFixtures.Visits == 0, "Theme existence guard lost"); ImtFixtures.Exists = true; }
                Console.WriteLine("PASS actual IL + executable fixture: " + target.Type + "." + target.Method);
            }
            ColossalFramework.Singleton<NetManager>.exists = false;
            Check(IntersectionMarkingCompatibility.NodeCount() == 32768 && IntersectionMarkingCompatibility.SegmentCount() == 36864, "Missing-manager fallback changed");
            ColossalFramework.Singleton<NetManager>.exists = true;
            MoreNetworkCapacity.Capacity.Nodes = 65536; bool rejected = false;
            try { IntersectionMarkingCompatibility.NodeCount(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unsupported ushort-wrap capacity accepted");
            Console.WriteLine("PASS: IMT 4 methods/7 bounds,65023,vanilla,missing manager,theme guard,labels/EH,changed roots/control flow and atomic rejection."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    public static void MutateCounter(ref int value) { value = 0; }
    public static void MutateShortCounter(ref ushort value) { value = 0; }
    private static int Local(CodeInstruction instruction, bool store)
    {
        if (instruction.opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
        if (instruction.opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
        if (instruction.opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
        if (instruction.opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
        if (instruction.opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc) && instruction.opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
        LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
        return variable == null ? Convert.ToInt32(instruction.operand) : variable.LocalIndex;
    }
    private static void Refuses(MethodInfo method, IntersectionMarkingCompatibility.Target target, List<CodeInstruction> input)
    {
        List<CodeInstruction> before = Copy(input); bool rejected = false;
        try { IntersectionMarkingCompatibility.ApplyTarget(method, target, input); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed IL accepted");
        for (int i = 0; i < input.Count; ++i) Check(input[i].opcode == before[i].opcode && object.Equals(input[i].operand, before[i].operand), "Refusal partly mutated input");
    }
    private static Action Compile(MethodInfo fixture, MethodInfo targetMethod, IntersectionMarkingCompatibility.Target target)
    {
        DynamicMethod method = new DynamicMethod("Expanded" + fixture.Name, typeof(void), Type.EmptyTypes, typeof(IntersectionMarkingRangeTests).Module, true);
        // Harmony declares the original locals when supplied the destination generator.
        // Declaring them here again breaks short ldloc/ldloca pairs for out variables.
        ILGenerator generator = method.GetILGenerator();
        List<CodeInstruction> code = IntersectionMarkingCompatibility.ApplyRanges(fixture, target, PatchProcessor.GetOriginalInstructions(fixture, generator));
        foreach (CodeInstruction c in code)
        {
            foreach (Label label in c.labels) generator.MarkLabel(label);
            if (c.operand == null) generator.Emit(c.opcode);
            else if (c.operand is Label) generator.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is LocalBuilder) generator.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) generator.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) generator.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is ConstructorInfo) generator.Emit(c.opcode, (ConstructorInfo)c.operand);
            else if (c.operand is FieldInfo) generator.Emit(c.opcode, (FieldInfo)c.operand);
            else if (c.operand is Type) generator.Emit(c.opcode, (Type)c.operand);
            else if (c.operand is byte) generator.Emit(c.opcode, (byte)c.operand);
            else if (c.operand is sbyte) generator.Emit(c.opcode, (sbyte)c.operand);
            else if (c.operand is short) generator.Emit(c.opcode, (short)c.operand);
            else if (c.operand is int) generator.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected operand " + c.operand);
        }
        return (Action)method.CreateDelegate(typeof(Action));
    }
}
