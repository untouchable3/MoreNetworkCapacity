using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal static int NodeLoopLimit = 65024, SegmentLoopLimit = 65024;
    }
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string message) { }
        internal static void Warning(string message)
        {
            if (message.StartsWith("Could not find") || message.StartsWith("Multiple")
                || message.StartsWith("Skipping")) Console.WriteLine(message);
        }
        internal static void Error(string message) { ++Errors; Console.WriteLine("Expected rejection: " + message); }
    }
}

internal static partial class TransportLineLoopTests
{
    private static readonly MethodInfo Original = typeof(TransportLine).GetMethod("SimulationStep", new[] { typeof(ushort) });
    private static readonly MethodInfo Transpiler = AccessTools.Method(typeof(CoreLoopPatch), "Transpiler", null, null);
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    private static List<CodeInstruction> Read()
    {
        DynamicMethod labels = new DynamicMethod("TransportLineLabels", typeof(void), Type.EmptyTypes);
        return PatchProcessor.GetOriginalInstructions(Original, labels.GetILGenerator());
    }
    private static List<CodeInstruction> Clone(List<CodeInstruction> original)
    {
        List<CodeInstruction> copy = new List<CodeInstruction>();
        foreach (CodeInstruction c in original) copy.Add(new CodeInstruction(c));
        return copy;
    }
    private static List<CodeInstruction> Patch(List<CodeInstruction> code)
    {
        return new List<CodeInstruction>((IEnumerable<CodeInstruction>)Transpiler.Invoke(null, new object[] { Original, code }));
    }
    private static int PatchedCount(List<CodeInstruction> code)
    {
        int count = 0;
        FieldInfo limit = AccessTools.Field(typeof(MoreNetworkCapacity.Capacity), "NodeLoopLimit");
        foreach (CodeInstruction c in code) if (c.opcode == OpCodes.Ldsfld && limit.Equals(c.operand)) ++count;
        return count;
    }
    private static List<int> Guards(List<CodeInstruction> code)
    {
        List<int> result = new List<int>();
        for (int i = 0; i + 1 < code.Count; ++i)
            if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, 32768)
                && code[i + 1].opcode.FlowControl == FlowControl.Cond_Branch) result.Add(i);
        return result;
    }
    private static void Verify(List<CodeInstruction> input)
    {
        List<CodeInstruction> before = Clone(input);
        List<CodeInstruction> output = Patch(input);
        Check(output.Count == before.Count && PatchedCount(output) == 3, "Expected exactly three node guards.");
        int changed = 0, citizen = 0;
        for (int i = 0; i < before.Count; ++i)
        {
            Check(output[i].labels.Count == before[i].labels.Count && output[i].blocks.Count == before[i].blocks.Count,
                "Control-flow metadata changed.");
            if (output[i].opcode != before[i].opcode || !object.Equals(output[i].operand, before[i].operand))
            {
                ++changed;
                Check(before[i].opcode == OpCodes.Ldc_I4 && object.Equals(before[i].operand, 32768)
                    && output[i].opcode == OpCodes.Ldsfld, "Unrelated instruction changed.");
            }
            else if (before[i].opcode == OpCodes.Ldc_I4 && object.Equals(before[i].operand, 32768)) ++citizen;
        }
        Check(changed == 3 && citizen == 1, "Citizen guard changed or node guard omitted.");
    }
    private static void Reject(List<CodeInstruction> input, string reason)
    {
        List<CodeInstruction> before = Clone(input);
        int errors = MoreNetworkCapacity.Logging.Errors;
        List<CodeInstruction> output = Patch(input);
        Check(MoreNetworkCapacity.Logging.Errors > errors && PatchedCount(output) == 0, reason);
        for (int i = 0; i < before.Count; ++i)
            Check(before[i].opcode == output[i].opcode && object.Equals(before[i].operand, output[i].operand),
                "Rejected layout partially patched.");
    }
    public static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
            List<CodeInstruction> original = Read();
            List<int> guards = Guards(original);
            Check(guards.Count == 4, "Actual game must contain three stop guards and one citizen guard.");
            Verify(Clone(original));
            List<CodeInstruction> shifted = Clone(original);
            shifted.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (args.Length > 0 && args[0] == "--expect-legacy")
            {
                Check(PatchedCount(Patch(shifted)) == 0 && MoreNetworkCapacity.Logging.Errors == 1,
                    "Legacy failure not reproduced.");
                Console.WriteLine("PASS: original three guards patched; one harmless NOP reproduces shifted-loop failure.");
                return 0;
            }
            Verify(shifted);
            List<CodeInstruction> padded = Clone(original);
            for (int i = padded.Count - 1; i >= 0; --i) padded.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            Verify(padded);
            List<CodeInstruction> changed = Clone(original);
            changed[guards[2]].operand = 32767;
            Reject(changed, "Missing stop guard accepted.");
            changed = Clone(original);
            changed[guards[1] + 1].opcode = OpCodes.Bge;
            Reject(changed, "Changed branch semantics accepted.");
            changed = Clone(original);
            int getNext = -1;
            for (int i = 0; i < changed.Count; ++i)
            {
                MethodInfo call = changed[i].operand as MethodInfo;
                if (call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextStop") getNext = i;
            }
            Check(getNext >= 0, "GetNextStop anchor missing.");
            MethodInfo previousStop = typeof(TransportLine).GetMethod("GetPrevStop", new[] { typeof(ushort) });
            Check(previousStop != null, "Alternative traversal fixture missing.");
            changed[getNext].operand = previousStop;
            Reject(changed, "Changed stop traversal accepted.");
            changed = Clone(original);
            changed.Insert(guards[1], new CodeInstruction(OpCodes.Conv_U2, null));
            Reject(changed, "Narrowed loop counter accepted.");
            changed = Clone(original);
            changed[guards[1]].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
            Verify(changed);
            changed = Clone(original);
            changed[0].labels.Add((Label)changed[guards[1] + 1].operand);
            Reject(changed, "Ambiguous branch label accepted.");
            foreach (string arg in args) if (arg.EndsWith(".dll")) TestTlm(arg);
            TestStopFamily();
            TestSimulationFamily();
            TestAllOriginalTargets();
            Check(MoreNetworkCapacity.Capacity.NodeLoopLimit == 65024, "Unexpected capacity.");
            Console.WriteLine("PASS: actual TransportLine IL, shifted/NOP layouts, citizen preservation, metadata and unsafe-layout rejection.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
