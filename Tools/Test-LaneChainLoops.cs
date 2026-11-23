using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class LaneChainLoopTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static List<CodeInstruction> Read(MethodInfo method, out ILGenerator generator)
    {
        DynamicMethod labels = new DynamicMethod("LaneChainLabels", typeof(void), Type.EmptyTypes);
        generator = labels.GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method, generator);
    }
    private static int Bound(List<CodeInstruction> code)
    {
        for (int i = 0; i + 1 < code.Count; ++i)
            if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, 32768)
                && code[i + 1].opcode.FlowControl == FlowControl.Cond_Branch) return i;
        throw new Exception("Original guard missing.");
    }
    private static void Noise(List<CodeInstruction> code, ILGenerator generator)
    {
        Label end = generator.DefineLabel();
        CodeInstruction nop = new CodeInstruction(OpCodes.Nop, null); nop.labels.Add(end);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null),
            new CodeInstruction(OpCodes.Ldc_I4, 32768), new CodeInstruction(OpCodes.Blt, end), nop });
    }
    private static void Verify(MethodInfo method, List<CodeInstruction> code, CodeInstruction expected)
    {
        List<CodeInstruction> before = new List<CodeInstruction>();
        foreach (CodeInstruction c in code) before.Add(new CodeInstruction(c));
        int[] result = LaneChainLoopBounds.Resolve(method, code);
        Check(expected == null ? result == null : result != null && result.Length == 1
            && ReferenceEquals(code[result[0]], expected), "Selection mismatch: " + method.DeclaringType.Name + "." + method.Name);
        Check(before.Count == code.Count, "Input count changed.");
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand)
                && code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count,
                "Input mutated.");
            for (int j = 0; j < code[i].labels.Count; ++j) Check(code[i].labels[j].Equals(before[i].labels[j]), "Label mutated.");
            for (int j = 0; j < code[i].blocks.Count; ++j) Check(ReferenceEquals(code[i].blocks[j], before[i].blocks[j]), "EH mutated.");
        }
    }
    private static void Test(MethodInfo method)
    {
        ILGenerator labels;
        for (int variant = 0; variant < 5; ++variant)
        {
            List<CodeInstruction> code = Read(method, out labels);
            CodeInstruction target = code[Bound(code)];
            if (variant == 1) code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 2) for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 3) Noise(code, labels);
            if (variant == 4) { target.labels.Add(labels.DefineLabel()); target.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null)); }
            Verify(method, code, target);
        }
        List<CodeInstruction> changed = Read(method, out labels); int bound = Bound(changed);
        changed[bound].operand = 32767; Noise(changed, labels); Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        changed[bound + 1].opcode = OpCodes.Bge; Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        changed.Insert(bound, new CodeInstruction(OpCodes.Conv_U2, null)); Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        changed[0].labels.Add((Label)changed[bound + 1].operand); Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        Label condition = (Label)changed[bound + 1].operand;
        int at = changed.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(condition); });
        Check(at >= 0 && changed[at + 1].opcode == OpCodes.Brtrue, "Continuation changed.");
        changed[at + 1].opcode = OpCodes.Brfalse; Verify(method, changed, null);
        changed = Read(method, out labels);
        bool fieldChanged = false;
        for (int i = 0; i < changed.Count; ++i)
        {
            FieldInfo field = changed[i].operand as FieldInfo;
            if (field != null && field.DeclaringType == typeof(NetNode)
                && field.Name == (method.Name == "UpdateLanes" ? "m_nextGridNode" : "m_nextLaneNode"))
            { changed[i].operand = AccessTools.Field(typeof(NetNode), "m_nextBuildingNode"); fieldChanged = true; }
        }
        Check(fieldChanged, "Traversal fixture missing."); Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        for (int i = bound; i < changed.Count; ++i)
            if (changed[i].opcode == OpCodes.Ldstr) { changed[i].operand = "Different list"; break; }
        Verify(method, changed, null);
        changed = Read(method, out labels);
        // Keep local/array sizes and candidate count; change the actual seed source.
        bool seedChanged = false;
        for (int i = 0; i < changed.Count; ++i)
        {
            FieldInfo field = changed[i].operand as FieldInfo;
            if (field != null && (method.Name == "UpdateLanes" ? field.DeclaringType == typeof(NetManager) && field.Name == "m_nodeGrid"
                : field.DeclaringType == typeof(NetLane) && field.Name == "m_nodes"))
            {
                changed[i].operand = method.Name == "UpdateLanes" ? AccessTools.Field(typeof(NetManager), "m_segmentGrid")
                    : AccessTools.Field(typeof(NetLane), "m_segment"); seedChanged = true; break;
            }
        }
        Check(seedChanged, "Seed fixture missing."); Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        // A changed increment is not accepted merely because the bound remains.
        changed[bound - 3].opcode = OpCodes.Sub; Verify(method, changed, null);
        changed = Read(method, out labels); bound = Bound(changed);
        condition = (Label)changed[bound + 1].operand;
        at = changed.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(condition); });
        // Redirect the invalid-list exit to the body instead of the loop exit.
        changed[at - 1].operand = changed[at + 1].operand; Verify(method, changed, null);
        Console.WriteLine("PASS: " + method.DeclaringType.Name + "." + method.Name + " actual/NOP/noise and unsafe-layout refusal.");
    }
    public static int Main()
    {
        try
        {
            int count = 0;
            foreach (Type type in new[] { typeof(CargoPathAI), typeof(ConcourseBaseAI), typeof(FlightPathAI),
                typeof(MetroTrackBaseAI), typeof(PedestrianBridgeAI), typeof(PedestrianPathAI), typeof(PedestrianTunnelAI),
                typeof(PedestrianWayAI), typeof(RoadBaseAI), typeof(RunwayAI), typeof(ShipDockAI), typeof(ShipPathAI),
                typeof(TaxiwayAI), typeof(TrainTrackBaseAI), typeof(TransportLineAI), typeof(TransportPathAI), typeof(NetAI) })
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                    if (LaneChainLoopBounds.Handles(method)) { Test(method); ++count; }
            Check(count == 22, "Expected exactly22 reviewed methods, found " + count);
            Check(!LaneChainLoopBounds.Handles(null), "Null owned.");
            Check(!LaneChainLoopBounds.Handles(AccessTools.Method(typeof(NetAI), "UpdateLanes", null, null)), "Base/inherited target owned.");
            Console.WriteLine("PASS:22 method ownership, semantic guards, immutable resolution."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
