using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    public static class Capacity
    {
        public const int VanillaNodeCapacity = 32768;
        public static int NodeLoopLimit = 65024, SegmentLoopLimit = 65024;
        public static int GetNodeSimulationPhaseDivisor() { return NodeLoopLimit; }
    }
    internal static class Logging { internal static void Info(string value) { } }
}
internal static class NodeSimulationPhaseTests
{
    private static string[] paths;
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string dir in paths) { string p = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
        return null;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static uint WrongFrame(SimulationManager manager) { return 42; }
    public static void EscapeFrame(ref uint frame) { frame = 42; }
    private static List<CodeInstruction> Copy(List<CodeInstruction> input)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in input) result.Add(new CodeInstruction(c)); return result; }
    private static void Reject(MethodBase method, List<CodeInstruction> code)
    {
        List<CodeInstruction> before = Copy(code); bool rejected = false;
        try { new List<CodeInstruction>(NodeSimulationPhasePatch.Transpiler(method, code)); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unsafe phase IL accepted: " + method.Name);
        for (int i = 0; i < code.Count; ++i)
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand), "Partial phase mutation");
    }
    private static void Run()
    {
        int methods = 0;
        foreach (MethodBase method in NodeSimulationPhasePatch.TargetMethods())
        {
            DynamicMethod carrier = new DynamicMethod("phase_labels", typeof(void), Type.EmptyTypes);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, carrier.GetILGenerator());
            List<CodeInstruction> code = Copy(original);
            int index = code.FindIndex(delegate(CodeInstruction c) { return NetworkSimulationSlicePatch.Constant(c, 32768); });
            Check(index >= 0, "Missing original phase divisor");
            List<CodeInstruction> patched = new List<CodeInstruction>(NodeSimulationPhasePatch.Transpiler(method, code));
            int changes = 0;
            for (int i = 0; i < code.Count; ++i)
            {
                if (original[i].opcode != patched[i].opcode || !object.Equals(original[i].operand, patched[i].operand)) ++changes;
                Check(original[i].labels.Count == patched[i].labels.Count && original[i].blocks.Count == patched[i].blocks.Count,
                    "Phase labels or exception blocks lost");
            }
            Check(changes == 1 && patched[index].opcode == OpCodes.Call, "Phase changed unrelated instructions");
            MethodInfo helper = (MethodInfo)patched[index].operand;
            DynamicMethod phase = new DynamicMethod("phase", typeof(uint), new[] { typeof(ushort) });
            ILGenerator il = phase.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Call, helper); il.Emit(OpCodes.Div_Un); il.Emit(OpCodes.Ret);
            Func<ushort, uint> execute = (Func<ushort, uint>)phase.CreateDelegate(typeof(Func<ushort, uint>));
            foreach (int size in new[] { 32768, 65024, 32768 })
            {
                MoreNetworkCapacity.Capacity.NodeLoopLimit = size;
                for (int id = 0; id < size; ++id) Check(execute((ushort)id) == (uint)(id / (size / 256)), "Signal phase differs from scheduling");
            }
            code = Copy(original); code.InsertRange(index, new[] { new CodeInstruction(OpCodes.Nop, null), new CodeInstruction(OpCodes.Nop, null), new CodeInstruction(OpCodes.Nop, null) });
            new List<CodeInstruction>(NodeSimulationPhasePatch.Transpiler(method, code));
            code = Copy(original); code[index - 2].opcode = OpCodes.Ldc_I4_7; Reject(method, code);
            code = Copy(original); code[index + 1].opcode = OpCodes.Div; Reject(method, code);
            code = Copy(original); code[index + 6].operand = 511; Reject(method, code);
            code = Copy(original); code[index - 3].opcode = OpCodes.Ldc_I4_1; code[index - 3].operand = null; Reject(method, code);
            code = Copy(original); code.Add(new CodeInstruction(OpCodes.Ldc_I4, 32768)); code.Add(new CodeInstruction(OpCodes.Pop, null)); Reject(method, code);
            code = Copy(original); code[index].labels.Add(carrier.GetILGenerator().DefineLabel()); Reject(method, code);
            code = Copy(original); code[index - 3].labels.Add(carrier.GetILGenerator().DefineLabel()); Reject(method, code);
            int phaseSlot = NetworkSimulationSlicePatch.Local(original[index + 2], false);
            int frameSlot = NetworkSimulationSlicePatch.Local(original[index - 4], false);
            foreach (int slot in new[] { phaseSlot, frameSlot })
                foreach (object operand in new object[] { (byte)slot, slot, (short)slot, (ushort)slot })
                {
                    code = Copy(original);
                    code.InsertRange(index + 9, new[] { new CodeInstruction(OpCodes.Ldloca_S, operand),
                        new CodeInstruction(OpCodes.Call, typeof(NodeSimulationPhaseTests).GetMethod("EscapeFrame")) });
                    Reject(method, code);
                }
            code = Copy(original);
            for (int j = index - 1; j >= 0; --j)
            {
                FieldInfo field = code[j].operand as FieldInfo;
                if (field == null || field.DeclaringType != typeof(SimulationManager)) continue;
                code[j].opcode = OpCodes.Call; code[j].operand = typeof(NodeSimulationPhaseTests).GetMethod("WrongFrame"); break;
            }
            Reject(method, code);
            if (NetworkSimulationSlicePatch.Local(original[index - 3], true) >= 0)
            {
                int nodeSlot = NetworkSimulationSlicePatch.Local(original[index - 3], true);
                code = Copy(original);
                code.InsertRange(index + 9, new[] { new CodeInstruction(OpCodes.Ldc_I4_1, null),
                    new CodeInstruction(OpCodes.Stloc_S, (byte)nodeSlot) });
                Reject(method, code);
            }
            ++methods;
        }
        Check(methods == 9, "Incomplete game phase target list");
        // Execute the real game's bank helpers without any Unity/manager calls.
        ushort node = 64512; NetSegment segment = new NetSegment(); segment.m_startNode = node;
        RoadBaseAI.SetBollardState(node, ref segment, 0, RoadBaseAI.TrafficLightState.Red, RoadBaseAI.TrafficLightState.Red, false, false);
        RoadBaseAI.SetBollardState(node, ref segment, 256, RoadBaseAI.TrafficLightState.Red, RoadBaseAI.TrafficLightState.Red, false, false);
        uint tick = (uint)(node / 254);
        RoadBaseAI.SetBollardState(node, ref segment, tick, RoadBaseAI.TrafficLightState.Green, RoadBaseAI.TrafficLightState.Green, false, false);
        RoadBaseAI.TrafficLightState enter, exit;
        RoadBaseAI.GetBollardState(node, ref segment, unchecked(tick - ((uint)node << 8) / 32768), out enter, out exit);
        Check(exit == RoadBaseAI.TrafficLightState.Red, "Old phase mismatch no longer reproduced");
        RoadBaseAI.GetBollardState(node, ref segment, unchecked(tick - ((uint)node << 8) / 65024), out enter, out exit);
        Check(exit == RoadBaseAI.TrafficLightState.Green, "Correct phase did not read current signal bank");
        Console.WriteLine("PASS: nine real game phase targets, exact single changes, full-ID scheduling equivalence, vanilla/reset, malformed IL atomic refusal, metadata and real bollard bank regression.");
    }
    private static int Main(string[] args)
    { try { paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve; Run(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}
