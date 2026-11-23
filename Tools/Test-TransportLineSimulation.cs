using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

internal static partial class TransportLineLoopTests
{
    private static void VerifySimulation(MethodInfo method, List<CodeInstruction> code, HashSet<CodeInstruction> selected)
    {
        Dictionary<CodeInstruction, CodeInstruction> before = new Dictionary<CodeInstruction, CodeInstruction>();
        foreach (CodeInstruction c in code) before.Add(c, new CodeInstruction(c));
        int errors = MoreNetworkCapacity.Logging.Errors, changes = 0;
        List<CodeInstruction> after = PatchStop(method, code);
        Check(after.Count == before.Count && errors == MoreNetworkCapacity.Logging.Errors, "Simulation layout refused: " + method.Name);
        foreach (CodeInstruction c in after)
        {
            CodeInstruction old = before[c];
            Check(c.labels.Count == old.labels.Count && c.blocks.Count == old.blocks.Count, "Simulation metadata changed.");
            if (c.opcode != old.opcode || !object.Equals(c.operand, old.operand))
            {
                ++changes;
                Check(selected.Contains(c) && c.opcode == OpCodes.Ldsfld
                    && object.Equals(c.operand, AccessTools.Field(typeof(MoreNetworkCapacity.Capacity), "NodeLoopLimit")),
                    "Changed unrelated simulation instruction.");
            }
        }
        Check(changes == selected.Count, "Simulation guard omitted.");
    }
    private static void SimulationLayouts(string name, int[] positions)
    {
        MethodInfo method = AccessTools.Method(typeof(TransportLine), name,
            name == "CalculateTargetVehicleCount" ? Type.EmptyTypes : new[] { typeof(ushort) }, null);
        ILGenerator labels;
        for (int variant = 0; variant < 5; ++variant)
        {
            List<CodeInstruction> code = ReadStop(method, out labels);
            HashSet<CodeInstruction> targets = new HashSet<CodeInstruction>();
            foreach (int index in positions)
            {
                Check(object.Equals(code[index].operand, 32768), "Reviewed simulation guard missing.");
                targets.Add(code[index]);
            }
            if (variant == 1) code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 2) for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 3) StopNoise(code, labels);
            if (variant == 4) code[positions[0]].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
            VerifySimulation(method, code, targets);
        }
        List<CodeInstruction> changed = ReadStop(method, out labels);
        changed[positions[0]].operand = 32767; StopNoise(changed, labels); RejectStop(method, changed);
        changed = ReadStop(method, out labels); changed[positions[0] + 1].opcode = OpCodes.Bge; RejectStop(method, changed);
        changed = ReadStop(method, out labels); changed.Insert(positions[0], new CodeInstruction(OpCodes.Conv_U2, null)); RejectStop(method, changed);
        changed = ReadStop(method, out labels);
        DynamicMethod alteredLocals = new DynamicMethod("NarrowSimulationCounter", typeof(void), Type.EmptyTypes);
        LocalBuilder narrow = alteredLocals.GetILGenerator().DeclareLocal(typeof(ushort));
        changed[positions[0] - 1].opcode = OpCodes.Stloc_S; changed[positions[0] - 1].operand = narrow;
        changed[positions[0] - 5].opcode = OpCodes.Ldloc_S; changed[positions[0] - 5].operand = narrow;
        RejectStop(method, changed);
        changed = ReadStop(method, out labels); changed[0].labels.Add((Label)changed[positions[0] + 1].operand); RejectStop(method, changed);
        changed = ReadStop(method, out labels);
        bool found = false;
        for (int i = 0; i < positions[positions.Length - 1]; ++i)
        {
            FieldInfo field = changed[i].operand as FieldInfo;
            MethodInfo call = changed[i].operand as MethodInfo;
            if (name == "CalculateTargetVehicleCount" && field != null && field.DeclaringType == typeof(NetSegment) && field.Name == "m_endNode")
            { changed[i].operand = typeof(NetSegment).GetField("m_startNode"); found = true; break; }
            if (name != "CalculateTargetVehicleCount" && call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextStop")
            { changed[i].operand = typeof(TransportLine).GetMethod("GetPrevStop", new[] { typeof(ushort) }); found = true; break; }
        }
        Check(found, "Simulation negative traversal fixture missing."); RejectStop(method, changed);
        if (name == "CopyMissingPaths")
        {
            changed = ReadStop(method, out labels);
            Label conditionLabel = (Label)changed[positions[0] + 1].operand;
            int condition = changed.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(conditionLabel); });
            changed[condition + 1].opcode = OpCodes.Brtrue; RejectStop(method, changed);
            changed = ReadStop(method, out labels);
            changed[condition + 2].opcode = changed[condition].opcode;
            changed[condition + 2].operand = changed[condition].operand; RejectStop(method, changed);
            changed = ReadStop(method, out labels);
            for (int i = 0; i < 25; ++i)
            {
                FieldInfo field = changed[i].operand as FieldInfo;
                if (field != null && field.DeclaringType == typeof(TransportLine) && field.Name == "m_stops")
                { changed[i].operand = typeof(TransportLine).GetField("m_vehicles"); break; }
            }
            RejectStop(method, changed);
        }
        Console.WriteLine("PASS: " + name + " selected guards, NOP/noise and altered traversal/predicate/alias rejection.");
    }

    private delegate void CopyPathsReplay(ref TransportLine line, ushort source);
    private static TransportManager pairTransport;
    private static int pairNextCalls;
    public static TransportManager PairTransport() { return pairTransport; }
    public static PathManager PairPaths() { return null; }
    public static ushort PairNext(ushort node) { ++pairNextCalls; return nextStopReplay(node); }
    public static ushort NoPathSegment(ushort node) { return 0; }
    private static CopyPathsReplay CopyReplay(bool patch)
    {
        MethodInfo method = typeof(TransportLine).GetMethod("CopyMissingPaths");
        DynamicMethod replay = new DynamicMethod("ActualCopyMissingPaths", typeof(void),
            new[] { typeof(TransportLine).MakeByRefType(), typeof(ushort) }, typeof(TransportLineLoopTests).Module, true);
        ILGenerator il = replay.GetILGenerator();
        foreach (LocalVariableInfo local in method.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method, il);
        if (patch) code = PatchStop(method, code);
        foreach (CodeInstruction c in code)
        {
            MethodInfo call = c.operand as MethodInfo;
            if (call != null && call.Name == "get_instance")
            {
                if (call.ReturnType == typeof(NetManager)) c.operand = typeof(TransportLineLoopTests).GetMethod("StopNetwork");
                else if (call.ReturnType == typeof(TransportManager)) c.operand = typeof(TransportLineLoopTests).GetMethod("PairTransport");
                else if (call.ReturnType == typeof(PathManager)) c.operand = typeof(TransportLineLoopTests).GetMethod("PairPaths");
                else throw new Exception("Unexpected Copy singleton.");
            }
            else if (call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextStop")
                c.operand = typeof(TransportLineLoopTests).GetMethod("PairNext");
            else if (call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextSegment")
                c.operand = typeof(TransportLineLoopTests).GetMethod("NoPathSegment");
            else if (call != null && call.Name == "Error" && call.DeclaringType.Name == "CODebugBase`1")
                c.operand = typeof(TransportLineLoopTests).GetMethod("InvalidStopList");
            Emit(il, c);
        }
        return (CopyPathsReplay)replay.CreateDelegate(typeof(CopyPathsReplay));
    }
    private static void TestPairedExecution()
    {
        pairTransport = (TransportManager)FormatterServices.GetUninitializedObject(typeof(TransportManager));
        pairTransport.m_lines = new Array16<TransportLine>(256);
        pairTransport.m_lines.m_buffer[1].m_totalLength = 123.5f;
        CopyPathsReplay oldCopy = CopyReplay(false), newCopy = CopyReplay(true);
        for (int i = 1; i <= 40000; ++i) LinkStop((ushort)i, (ushort)(i == 40000 ? 1 : i + 1));
        pairTransport.m_lines.m_buffer[1].m_stops = 1;
        TransportLine line = new TransportLine(); line.m_stops = 1;
        pairNextCalls = 0; invalidLists = 0; oldCopy(ref line, 1);
        Check(pairNextCalls == 65536 && invalidLists == 1 && line.m_totalLength == 123.5f, "Original paired cutoff/tail not reproduced.");
        pairNextCalls = 0; invalidLists = 0; newCopy(ref line, 1);
        Check(pairNextCalls == 80000 && invalidLists == 0 && line.m_totalLength == 123.5f, "Paired long-chain traversal/tail failed.");
        pairNextCalls = 0; line.m_totalLength = 7; newCopy(ref line, 0);
        Check(pairNextCalls == 0 && line.m_totalLength == 7, "Zero source line behavior changed.");
        line.m_stops = 0; pairNextCalls = 0; newCopy(ref line, 1);
        Check(pairNextCalls == 0 && line.m_totalLength == 123.5f, "Empty destination/tail behavior changed.");
        LinkStop(50000, 50001); LinkStop(50001, 65023); LinkStop(65023, 50000);
        line.m_stops = 50000; pairNextCalls = 0; newCopy(ref line, 1);
        Check(pairNextCalls == 6 && line.m_totalLength == 123.5f, "Short destination loop did not stop paired traversal.");
        pairTransport.m_lines.m_buffer[1].m_stops = 50000; line.m_stops = 1; pairNextCalls = 0; newCopy(ref line, 1);
        Check(pairNextCalls == 6, "Short source loop did not stop paired traversal.");
        LinkStop(1, 2); LinkStop(2, 3); LinkStop(3, 2);
        pairTransport.m_lines.m_buffer[1].m_stops = 1; line.m_stops = 1;
        pairNextCalls = 0; invalidLists = 0; newCopy(ref line, 1);
        Check(pairNextCalls == 130048 && invalidLists == 1 && line.m_totalLength == 123.5f, "Malformed paired chain termination/tail failed.");
        Console.WriteLine("PASS: actual CopyMissingPaths no-path branch replay: 40000 pairs, unequal/high-ID chains, empty/zero source and malformed termination; tail length copy retained.");
    }
    private static void TestSimulationFamily()
    {
        SimulationLayouts("UpdatePaths", new[] { 143, 288 });
        SimulationLayouts("CalculateTargetVehicleCount", new[] { 80 });
        SimulationLayouts("CopyMissingPaths", new[] { 192 });
        TestPairedExecution();
    }
}
