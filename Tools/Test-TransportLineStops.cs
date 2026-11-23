using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

internal static partial class TransportLineLoopTests
{
    private static List<CodeInstruction> ReadStop(MethodInfo method, out ILGenerator labels)
    {
        DynamicMethod fixture = new DynamicMethod("StopLabels", typeof(void), Type.EmptyTypes);
        labels = fixture.GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method, labels);
    }
    private static List<CodeInstruction> PatchStop(MethodInfo method, List<CodeInstruction> code)
    {
        return new List<CodeInstruction>((IEnumerable<CodeInstruction>)Transpiler.Invoke(null, new object[] { method, code }));
    }
    private static void VerifyStop(MethodInfo method, List<CodeInstruction> code, CodeInstruction target)
    {
        Dictionary<CodeInstruction, CodeInstruction> before = new Dictionary<CodeInstruction, CodeInstruction>();
        foreach (CodeInstruction c in code) before.Add(c, new CodeInstruction(c));
        int errors = MoreNetworkCapacity.Logging.Errors, changes = 0;
        List<CodeInstruction> after = PatchStop(method, code);
        Check(after.Count == before.Count && MoreNetworkCapacity.Logging.Errors == errors, "Stop layout rejected: " + method.Name);
        foreach (CodeInstruction c in after)
        {
            CodeInstruction old = before[c];
            Check(c.labels.Count == old.labels.Count && c.blocks.Count == old.blocks.Count, "Stop metadata changed.");
            if (c.opcode != old.opcode || !object.Equals(c.operand, old.operand))
            {
                ++changes;
                Check(object.ReferenceEquals(c, target) && c.opcode == OpCodes.Ldsfld
                    && object.Equals(c.operand, AccessTools.Field(typeof(MoreNetworkCapacity.Capacity), "NodeLoopLimit")),
                    "Changed unrelated/citizen guard in " + method.Name);
            }
        }
        Check(changes == 1, "Expected exactly one station guard in " + method.Name);
    }
    private static void RejectStop(MethodInfo method, List<CodeInstruction> code)
    {
        List<CodeInstruction> before = Clone(code);
        int errors = MoreNetworkCapacity.Logging.Errors;
        List<CodeInstruction> after = PatchStop(method, code);
        Check(MoreNetworkCapacity.Logging.Errors == errors + 1, "Unsafe stop layout accepted: " + method.Name);
        for (int i = 0; i < before.Count; ++i)
            Check(before[i].opcode == after[i].opcode && object.Equals(before[i].operand, after[i].operand), "Partial stop mutation.");
    }
    private static void StopNoise(List<CodeInstruction> code, ILGenerator labels)
    {
        Label end = labels.DefineLabel();
        CodeInstruction nop = new CodeInstruction(OpCodes.Nop, null); nop.labels.Add(end);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, 32768),
            new CodeInstruction(OpCodes.Blt, end), nop });
    }
    private static void StopLayouts(string name, Type parameter, int selected)
    {
        MethodInfo method = AccessTools.Method(typeof(TransportLine), name, new[] { parameter }, null);
        ILGenerator labels;
        List<CodeInstruction> original = ReadStop(method, out labels);
        Check(object.Equals(original[selected].operand, 32768), "Reviewed station position missing.");
        if (name == "GetDebugString") Check(object.Equals(original[115].operand, 32768) && selected == 136,
            "Original nested citizen/outer station distinction changed.");
        foreach (int variant in new[] { 0, 1, 2, 3, 4 })
        {
            List<CodeInstruction> code = ReadStop(method, out labels);
            CodeInstruction target = code[selected];
            if (variant == 1) code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 2) for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 3) StopNoise(code, labels);
            if (variant == 4)
            {
                target.labels.Add(labels.DefineLabel());
                target.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
            }
            VerifyStop(method, code, target);
        }
        List<CodeInstruction> changed = ReadStop(method, out labels);
        changed[selected].operand = 32767; StopNoise(changed, labels);
        RejectStop(method, changed); // Same candidate count is not enough.
        changed = ReadStop(method, out labels);
        changed[selected + 1].opcode = OpCodes.Bge; RejectStop(method, changed);
        changed = ReadStop(method, out labels);
        changed.Insert(selected, new CodeInstruction(OpCodes.Conv_U2, null)); RejectStop(method, changed);
        changed = ReadStop(method, out labels);
        bool replaced = false;
        for (int i = 0; i < selected; ++i)
        {
            FieldInfo field = changed[i].operand as FieldInfo;
            MethodInfo call = changed[i].operand as MethodInfo;
            if (name == "GetDebugString" && field != null && field.DeclaringType == typeof(NetSegment) && field.Name == "m_endNode")
            { changed[i].operand = typeof(NetSegment).GetField("m_startNode"); replaced = true; }
            if (name != "GetDebugString" && call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextStop")
            { changed[i].operand = typeof(TransportLine).GetMethod("GetPrevStop", new[] { typeof(ushort) }); replaced = true; }
        }
        Check(replaced, "Traversal negative fixture missing."); RejectStop(method, changed);
        changed = ReadStop(method, out labels);
        changed[0].labels.Add((Label)changed[selected + 1].operand); RejectStop(method, changed);
        Console.WriteLine("PASS: " + name + " station selection, shifted/noisy layouts, citizen preservation and unsafe-layout rejection.");
    }

    private delegate int CountStopsReplay(ref TransportLine line, ushort lineId);
    private delegate ushort GetStopReplay(ref TransportLine line, int index);
    private delegate ushort LastStopReplay(ref TransportLine line);
    private static Func<ushort, ushort> nextStopReplay;
    private static LastStopReplay lastStopReplay;
    private static NetManager stopNetwork;
    private static int invalidLists;
    public static NetManager StopNetwork() { return stopNetwork; }
    public static ushort NextStopFixture(ushort node) { return nextStopReplay(node); }
    public static ushort LastStopFixture(ref TransportLine line) { return lastStopReplay(ref line); }
    public static void InvalidStopList(LogChannel channel, string message) { ++invalidLists; }

    private static Delegate ReplayStop(MethodInfo method, bool patch, Type delegateType)
    {
        List<Type> parameters = new List<Type>();
        if (!method.IsStatic) parameters.Add(typeof(TransportLine).MakeByRefType());
        foreach (ParameterInfo parameter in method.GetParameters()) parameters.Add(parameter.ParameterType);
        DynamicMethod replay = new DynamicMethod("ActualStop_" + method.Name, method.ReturnType,
            parameters.ToArray(), typeof(TransportLineLoopTests).Module, true);
        ILGenerator il = replay.GetILGenerator();
        foreach (LocalVariableInfo local in method.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method, il);
        if (patch) code = PatchStop(method, code);
        foreach (CodeInstruction c in code)
        {
            MethodInfo call = c.operand as MethodInfo;
            if (call != null && call.Name == "get_instance" && call.ReturnType == typeof(NetManager))
                c.operand = typeof(TransportLineLoopTests).GetMethod("StopNetwork");
            else if (call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetNextStop")
                c.operand = typeof(TransportLineLoopTests).GetMethod("NextStopFixture");
            else if (call != null && call.DeclaringType == typeof(TransportLine) && call.Name == "GetLastStop")
                c.operand = typeof(TransportLineLoopTests).GetMethod("LastStopFixture");
            else if (call != null && call.Name == "Error" && call.DeclaringType.Name == "CODebugBase`1")
                c.operand = typeof(TransportLineLoopTests).GetMethod("InvalidStopList");
            ParameterInfo argument = c.operand as ParameterInfo;
            if (argument != null) c.operand = (short)(argument.Position + (method.IsStatic ? 0 : 1));
            Emit(il, c);
        }
        return replay.CreateDelegate(delegateType);
    }
    private static void LinkStop(ushort from, ushort to)
    {
        stopNetwork.m_nodes.m_buffer[from].m_segment0 = from;
        stopNetwork.m_segments.m_buffer[from].m_startNode = from;
        stopNetwork.m_segments.m_buffer[from].m_endNode = to;
    }
    private static void TestStopExecution()
    {
        stopNetwork = (NetManager)FormatterServices.GetUninitializedObject(typeof(NetManager));
        stopNetwork.m_nodes = new Array16<NetNode>(65024);
        stopNetwork.m_segments = new Array16<NetSegment>(65024);
        nextStopReplay = (Func<ushort, ushort>)ReplayStop(typeof(TransportLine).GetMethod("GetNextStop"), false, typeof(Func<ushort, ushort>));
        lastStopReplay = (LastStopReplay)ReplayStop(typeof(TransportLine).GetMethod("GetLastStop"), true, typeof(LastStopReplay));
        MethodInfo countMethod = typeof(TransportLine).GetMethod("CountStops"), getMethod = typeof(TransportLine).GetMethod("GetStop");
        CountStopsReplay oldCount = (CountStopsReplay)ReplayStop(countMethod, false, typeof(CountStopsReplay));
        CountStopsReplay newCount = (CountStopsReplay)ReplayStop(countMethod, true, typeof(CountStopsReplay));
        GetStopReplay oldGet = (GetStopReplay)ReplayStop(getMethod, false, typeof(GetStopReplay));
        GetStopReplay newGet = (GetStopReplay)ReplayStop(getMethod, true, typeof(GetStopReplay));
        TransportLine line = new TransportLine();
        invalidLists = 0;
        Check(newCount(ref line, 1) == 0 && newGet(ref line, 0) == 0 && invalidLists == 0, "Empty line behavior changed.");
        LinkStop(50000, 50001); LinkStop(50001, 65023); LinkStop(65023, 50000); line.m_stops = 50000;
        Check(newCount(ref line, 1) == 3 && newGet(ref line, 0) == 50000 && newGet(ref line, 2) == 65023
            && newGet(ref line, 3) == 0 && invalidLists == 0, "Ordinary high-ID line behavior changed.");
        for (int i = 1; i <= 40000; ++i) LinkStop((ushort)i, (ushort)(i == 40000 ? 1 : i + 1));
        line.m_stops = 1; invalidLists = 0;
        Check(oldCount(ref line, 1) == 32768 && oldGet(ref line, 39999) == 0 && invalidLists == 2,
            "Original long-chain cutoff not reproduced.");
        invalidLists = 0;
        Check(newCount(ref line, 1) == 40000 && newGet(ref line, 39999) == 40000
            && newGet(ref line, 40000) == 0 && invalidLists == 0, "Expanded long-chain traversal failed.");
        Check(newGet(ref line, -1) == lastStopReplay(ref line), "Special -1 index branch changed.");
        MoreNetworkCapacity.Capacity.NodeLoopLimit = 32768; invalidLists = 0;
        Check(newCount(ref line, 1) == 32768 && invalidLists == 1, "Vanilla activation limit not respected.");
        MoreNetworkCapacity.Capacity.NodeLoopLimit = 65024;
        LinkStop(1, 2); LinkStop(2, 3); LinkStop(3, 2); invalidLists = 0;
        Check(newCount(ref line, 1) == 65024 && newGet(ref line, int.MaxValue) == 0 && invalidLists == 2,
            "Malformed chain failed to terminate at the active bound.");
        Console.WriteLine("PASS: actual CountStops/GetStop/GetNextStop replay: empty/high-ID/40000-stop lines, vanilla activation and malformed-chain termination.");
    }
    private static void TestStopFamily()
    {
        StopLayouts("GetDebugString", typeof(ushort), 136);
        StopLayouts("CountStops", typeof(ushort), 26);
        StopLayouts("GetStop", typeof(int), 34);
        TestStopExecution();
    }
}
