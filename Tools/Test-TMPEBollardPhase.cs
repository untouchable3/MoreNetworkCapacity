using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    public static class Capacity
    {
        public static int NodeLoopLimit = 32768, SegmentLoopLimit = 36864;
        public static int GetNodeSimulationPhaseDivisor() { return NodeLoopLimit; }
    }
    internal static class Logging { internal static void Info(string text) { } }
}
public static class TMPEPhaseFixture
{
    public static uint LastAge;
    [MethodImpl(MethodImplOptions.NoInlining)] public static ushort Node(ushort node) { return node; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static uint Frame(uint frame) { return frame; }
    public static RoadBaseAI.TrafficLightState Read(ushort id, ref NetSegment segment, uint frame)
    {
        ushort node = Node(id); uint now = Frame(frame);
        uint phase = (uint)(node << 8) / 32768;
        uint age = (now - phase) & 255;
        LastAge = age;
        RoadBaseAI.TrafficLightState enterState, exitState; bool enter, exit;
        RoadBaseAI.GetBollardState(node, ref segment, now - phase, out enterState, out exitState, out enter, out exit);
        if (enter && exit) RoadBaseAI.SetBollardState(node, ref segment, now - phase, enterState, exitState, enter, exit);
        return enterState;
    }
}
public static class TMPEBollardPhaseTests
{
    private static string[] paths;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string path in paths)
        {
            string candidate = Path.Combine(path, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }
    private static List<CodeInstruction> Copy(List<CodeInstruction> input)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in input) result.Add(new CodeInstruction(c)); return result; }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Assembly tmpe = Assembly.LoadFrom(Path.Combine(paths[1], "TrafficManager.dll"));
            MethodInfo original = TMPEBollardPhase.Resolve(tmpe);
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(original, (ILGenerator)null);
            List<CodeInstruction> patched = TMPEBollardPhase.Transform(original, Copy(code));
            int changed = 0, constant = -1;
            for (int i = 0; i < code.Count; ++i)
            {
                if (NetworkSimulationSlicePatch.Constant(code[i], 32768)) constant = i;
                if (code[i].opcode != patched[i].opcode) ++changed;
            }
            Check(changed == 1 && patched.Count == code.Count && patched[constant].opcode == OpCodes.Call, "Unexpected phase mutation");
            foreach (int offset in new int[] { -3, -1, 0, 1, 4, 6, 7 })
            {
                List<CodeInstruction> broken = Copy(code); broken[constant + offset].opcode = OpCodes.Nop;
                List<CodeInstruction> before = Copy(broken); bool rejected = false;
                try { TMPEBollardPhase.Transform(original, broken); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Changed phase shape accepted");
                for (int i = 0; i < code.Count; ++i) Check(before[i].opcode == broken[i].opcode && Object.Equals(before[i].operand, broken[i].operand), "Mismatch partially mutated input");
            }
            DynamicMethod carrier = new DynamicMethod("label", typeof(void), Type.EmptyTypes);
            Label incoming = carrier.GetILGenerator().DefineLabel();
            List<CodeInstruction> labelled = Copy(code); labelled[constant - 5].labels.Add(incoming);
            patched = TMPEBollardPhase.Transform(original, labelled);
            Check(patched[constant - 5].labels.Contains(incoming), "Whole expression entry label lost");
            Check(patched[constant].blocks.Count == code[constant].blocks.Count, "Exception metadata changed");
            foreach (int offset in new[] { -4, -3, 0, 1, 8 })
            { List<CodeInstruction> inner = Copy(code); inner[constant + offset].labels.Add(incoming); Reject(original, inner); }
            List<CodeInstruction> unrelated = Copy(code);
            Reject(typeof(TMPEPhaseFixture).GetMethod("Read"), unrelated);
            foreach (int slot in new[] { 4, 15, 16, 17 })
                foreach (object operand in new object[] { slot, (byte)slot, (short)slot, (ushort)slot })
                {
                    List<CodeInstruction> escaped = Copy(code);
                    escaped.InsertRange(constant + 9, new[] { new CodeInstruction(OpCodes.Ldloca_S, operand), new CodeInstruction(OpCodes.Pop, null) });
                    Reject(original, escaped);
                }
            foreach (int slot in new[] { 4, 15, 16, 17 })
            {
                List<CodeInstruction> written = Copy(code);
                written.InsertRange(constant + 9, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc_S, slot) });
                Reject(original, written);
            }
            List<CodeInstruction> source = Copy(code);
            source[constant - 5].opcode = OpCodes.Call; source[constant - 5].operand = typeof(TMPEBollardPhaseTests).GetMethod("WrongFrame");
            Reject(original, source);
            ILGenerator wrongLocals = new DynamicMethod("locals", typeof(void), Type.EmptyTypes).GetILGenerator();
            LocalBuilder wrong = null;
            for (int i = 0; i <= 15; ++i) wrong = wrongLocals.DeclareLocal(i == 15 ? typeof(int) : original.GetMethodBody().LocalVariables[i].LocalType);
            List<CodeInstruction> mismatch = Copy(code); mismatch[constant + 3].opcode = OpCodes.Ldloc_S; mismatch[constant + 3].operand = wrong;
            Reject(original, mismatch);
            List<CodeInstruction> padded = Copy(code); padded.Insert(constant, new CodeInstruction(OpCodes.Nop, null));
            TMPEBollardPhase.Transform(original, padded);
            Func<ushort, uint> calculate = Compile((MethodInfo)patched[constant].operand);
            foreach (int capacity in new int[] { 32768, 65024, 32768, 65024 })
            {
                Capacity.NodeLoopLimit = capacity;
                int slice = capacity >> 8;
                foreach (ushort node in new ushort[] { 1, 10000, 32767, 32768, 40000, 64512, 65023 })
                {
                    if (node >= capacity) continue;
                    uint phase = (uint)(node / slice), frame = 512 + phase;
                    Check(((uint)(node << 8) / capacity) == phase, "Divisor does not match exact simulation slices");
                    NetSegment segment = new NetSegment(); segment.m_startNode = node;
                    RoadBaseAI.SetBollardState(node, ref segment, 0, RoadBaseAI.TrafficLightState.Red, RoadBaseAI.TrafficLightState.Red, false, false);
                    RoadBaseAI.SetBollardState(node, ref segment, 256, RoadBaseAI.TrafficLightState.Red, RoadBaseAI.TrafficLightState.Red, false, false);
                    RoadBaseAI.SetBollardState(node, ref segment, frame, RoadBaseAI.TrafficLightState.Green, RoadBaseAI.TrafficLightState.Red, false, false);
                    RoadBaseAI.TrafficLightState old = TMPEPhaseFixture.Read(node, ref segment, frame);
                    uint correctedPhase = calculate(node);
                    RoadBaseAI.TrafficLightState current, currentExit; bool enter, exit;
                    RoadBaseAI.GetBollardState(node, ref segment, frame - correctedPhase, out current, out currentExit, out enter, out exit);
                    Check(current == RoadBaseAI.TrafficLightState.Green && ((frame - correctedPhase) & 255) == 0, "Patched reader sampled stale bollard bank");
                    if (capacity == 32768) Check(old == RoadBaseAI.TrafficLightState.Green, "Vanilla phase changed");
                    else if (node >= 10000) Check(old == RoadBaseAI.TrafficLightState.Red, "Fixture failed to reproduce shifted phase");
                }
                for (uint node = 0; node < capacity; ++node)
                    Check((node << 8) / (uint)capacity == node / (uint)slice, "Phase mismatch at node " + node);
            }
            Console.WriteLine("PASS: actual TMPE bollard phase IL, exact scope/locals/source/write/byref/branch refusals, atomic mismatch handling, vanilla/expanded full phase equivalence and executed actual game bank writes/reads.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    public static uint WrongFrame(SimulationManager manager) { return 0; }
    private static void Reject(MethodBase method, List<CodeInstruction> broken)
    {
        List<CodeInstruction> before = Copy(broken); bool rejected = false;
        try { TMPEBollardPhase.Transform(method, broken); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unsafe metadata/control-flow/local mutation accepted");
        for (int i = 0; i < broken.Count; ++i) Check(before[i].opcode == broken[i].opcode && Object.Equals(before[i].operand, broken[i].operand), "Partial mutation on refusal");
    }
    private static Func<ushort, uint> Compile(MethodInfo helper)
    {
        // Execute the exact replacement helper emitted into the real prefix;
        // the surrounding actual game bank accessors need no Unity managers.
        DynamicMethod result = new DynamicMethod("PhaseWithReplacement", typeof(uint), new Type[] { typeof(ushort) });
        ILGenerator il = result.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Shl);
        il.Emit(OpCodes.Call, helper); il.Emit(OpCodes.Div_Un); il.Emit(OpCodes.Ret);
        return (Func<ushort, uint>)result.CreateDelegate(typeof(Func<ushort, uint>));
    }
}
