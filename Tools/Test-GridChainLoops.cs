using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class GridChainLoopTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static List<CodeInstruction> Read(MethodBase method)
    {
        DynamicMethod labels = new DynamicMethod("ToolLoopLabels", typeof(void), Type.EmptyTypes);
        return PatchProcessor.GetOriginalInstructions(method, labels.GetILGenerator());
    }
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    {
        List<CodeInstruction> result = new List<CodeInstruction>();
        foreach (CodeInstruction c in code) result.Add(new CodeInstruction(c));
        return result;
    }
    private static int Resolve(MethodBase method, List<CodeInstruction> code, bool accept, string name)
    {
        List<CodeInstruction> before = Clone(code);
        int[] result = GridChainLoopBounds.Resolve(method, code);
        Check(accept ? result != null && result.Length == 1 : result == null, method.Name + ": " + name);
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand), "Input changed.");
            Check(code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count,
                "Control-flow metadata changed.");
            for (int j = 0; j < code[i].labels.Count; ++j) Check(code[i].labels[j].Equals(before[i].labels[j]), "Label changed.");
        }
        return result == null ? -1 : result[0];
    }
    private static int FindField(List<CodeInstruction> code, Type type, string name)
    {
        for (int i = 0; i < code.Count; ++i)
        {
            FieldInfo f = code[i].operand as FieldInfo;
            if (f != null && f.DeclaringType == type && f.Name == name) return i;
        }
        return -1;
    }
    private static void Test(MethodBase method, int expectedIndex)
    {
        Check(GridChainLoopBounds.Handles(method), "Reviewed signature not recognized.");
        Check(method.IsStatic == (method.DeclaringType == typeof(NetTool)), "Actual static/instance identity changed.");
        List<CodeInstruction> original = Read(method);
        int guard = Resolve(method, original, true, "original");
        Check(guard == expectedIndex, "Wrong actual-game occurrence.");
        bool segment = method.Name == "MoveParkSegments";
        int bound = method.DeclaringType == typeof(DistrictManager) ? 36864 : 32768;
        List<CodeInstruction> code = Clone(original);
        code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
        Check(Resolve(method, code, true, "entry NOP") == guard + 1, "Shifted index incorrect.");
        code = Clone(original);
        for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
        Resolve(method, code, true, "interleaved NOPs");
        code = Clone(original);
        for (int i = code.Count - 1; i >= 0; --i)
        {
            CodeInstruction nop = new CodeInstruction(OpCodes.Nop, null);
            nop.labels.AddRange(code[i].labels); code[i].labels.Clear();
            code.Insert(i, nop);
        }
        Resolve(method, code, true, "labels on inserted NOPs");
        code = Clone(original);
        code.Insert(0, new CodeInstruction(OpCodes.Ldc_I4, bound));
        code.Insert(1, new CodeInstruction(OpCodes.Pop, null));
        Check(Resolve(method, code, true, "same-value noise") == guard + 2, "Noise mistaken for guard.");
        code = Clone(original);
        code[guard].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
        Resolve(method, code, true, "EH metadata retained");
        code = Clone(original); code[guard].operand = bound - 1;
        Resolve(method, code, false, "changed bound");
        code.Insert(0, new CodeInstruction(OpCodes.Ldc_I4, bound));
        code.Insert(1, new CodeInstruction(OpCodes.Pop, null));
        Resolve(method, code, false, "noise cannot substitute missing guard");
        code = Clone(original); code[guard + 1].opcode = OpCodes.Bge;
        Resolve(method, code, false, "changed predicate");
        code = Clone(original); code.Insert(guard, new CodeInstruction(OpCodes.Conv_U2, null));
        Resolve(method, code, false, "narrowed counter");
        code = Clone(original); code[0].labels.Add((Label)code[guard + 1].operand);
        Resolve(method, code, false, "ambiguous condition label");
        code = Clone(original); code[guard - 5].opcode = OpCodes.Ldc_I4_0; code[guard - 5].operand = null;
        Resolve(method, code, false, "counter is no longer local increment");
        code = Clone(original);
        int update = guard - 6;
        code[update].opcode = OpCodes.Stloc; code[update].operand = 0;
        Resolve(method, code, false, "changed cursor store");
        code = Clone(original); code.RemoveAt(update);
        Resolve(method, code, false, "deleted cursor update");
        code = Clone(original);
        int condition = -1;
        for (int i = 0; i < code.Count; ++i)
            if (code[i].labels.Contains((Label)code[guard + 1].operand)) condition = i;
        Check(condition >= 0, "Missing condition fixture.");
        code = Clone(original);
        // Truncate to a bare counter/guard body and move its loop-entry label
        // onto index0: malformed input must return null, never index[-1].
        Label bodyLabel = (Label)original[condition + 1].operand;
        code.RemoveRange(0, guard - 5);
        code[0].labels.Add(bodyLabel);
        Resolve(method, code, false, "counter at start without cursor update");
        code = Clone(original);
        code[condition + 1].opcode = OpCodes.Brfalse;
        Resolve(method, code, false, "changed cursor continuation");
        Type item = segment ? typeof(NetSegment) : typeof(NetNode);
        string field = method.DeclaringType == typeof(DamPowerHouseAI) ? "m_nextBuildingNode" : (segment ? "m_nextGridSegment" : "m_nextGridNode");
        int next = guard - 7;
        Check(object.Equals(original[next].operand, AccessTools.Field(item, field)), "Expected cursor field.");
        code = Clone(original); code[next].operand = AccessTools.Field(item, segment ? "m_startNode" : "m_nextLaneNode");
        Resolve(method, code, false, "changed link field");
        code = Clone(original); code[next - 1].operand = segment ? typeof(NetNode) : typeof(NetSegment);
        Resolve(method, code, false, "changed array item");
        code = Clone(original); code[next - 3].operand = AccessTools.Field(typeof(Array16<NetNode>), "m_size");
        Resolve(method, code, false, "changed array field");        code = Clone(original);
        if (method.DeclaringType == typeof(DamPowerHouseAI))
        {
            int root = FindField(code, typeof(Building), "m_netNode");
            code[root].operand = AccessTools.Field(typeof(Building), "m_subBuilding");
        }
        else
        {
            int root = FindField(code, typeof(NetManager), segment ? "m_segmentGrid" : "m_nodeGrid");
            code[root].operand = AccessTools.Field(typeof(NetManager), segment ? "m_nodeGrid" : "m_segmentGrid");
        }
        Resolve(method, code, false, "changed initial cursor root");
        code = Clone(original);
        int counterIndex = original[guard - 1].opcode == OpCodes.Stloc_3 ? 3
            : ((LocalVariableInfo)original[guard - 1].operand).LocalIndex;
        ILGenerator typedGenerator = new DynamicMethod("TypedCounter", typeof(void), Type.EmptyTypes).GetILGenerator();
        LocalBuilder typedCounter = typedGenerator.DeclareLocal(typeof(ushort));
        code[guard - 1].opcode = OpCodes.Stloc; code[guard - 1].operand = typedCounter;
        Resolve(method, code, false, "counter declaration changed to UInt16");
        code = Clone(original);
        code.Insert(guard - 6, new CodeInstruction(OpCodes.Ldloca, counterIndex));
        code.Insert(guard - 5, new CodeInstruction(OpCodes.Pop, null));
        Resolve(method, code, false, "counter address escapes");
        code = Clone(original);
        code[guard + 7].opcode = OpCodes.Ret; code[guard + 7].operand = null;
        Resolve(method, code, false, "changed invalid-list exit");        code = Clone(original);
        // A duplicate complete region has duplicated labels and must never be
        // accepted as one unambiguous reviewed loop.
        code.AddRange(Clone(original));
        Resolve(method, code, false, "duplicated loop region");
        code = Clone(original);
        List<CodeInstruction> extra = Clone(original);
        Dictionary<Label, Label> labels = new Dictionary<Label, Label>();
        ILGenerator generator = new DynamicMethod("IndependentLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
        for (int i = 0; i <= original.Count; ++i) generator.DefineLabel();
        foreach (CodeInstruction c in extra) foreach (Label label in c.labels)
            if (!labels.ContainsKey(label)) labels.Add(label, generator.DefineLabel());
        foreach (CodeInstruction c in extra)
        {
            for (int i = 0; i < c.labels.Count; ++i) c.labels[i] = labels[c.labels[i]];
            if (c.operand is Label) c.operand = labels[(Label)c.operand];
            Label[] oldSwitch = c.operand as Label[];
            if (oldSwitch != null)
            {
                Label[] newSwitch = new Label[oldSwitch.Length];
                for (int i = 0; i < newSwitch.Length; ++i) newSwitch[i] = labels[oldSwitch[i]];
                c.operand = newSwitch;
            }
        }
        // Label values are generator-local integers. Reserve past every label
        // already used in the original region before allocating independent ones.
        bool collision = false;
        foreach (CodeInstruction c in original) foreach (Label label in c.labels)
            foreach (CodeInstruction e in extra) if (e.labels.Contains(label)) collision = true;
        Check(!collision, "Independent-label fixture accidentally reused a label.");
        code.AddRange(extra);
        Resolve(method, code, false, "two independently labelled matching traversals");
        Console.WriteLine("PASS: " + method.DeclaringType.Name + "." + method.Name + " original/NOP/noise and altered-provenance rejection.");
    }
    public static int Main()
    {
        try
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Test(typeof(DamPowerHouseAI).GetMethod("GetRefundAmount", flags), 115);
            Test(typeof(DistrictManager).GetMethod("MoveParkNodes", flags), 144);
            Test(typeof(DistrictManager).GetMethod("MoveParkSegments", flags), 171);
            Test(typeof(ElectricityManager).GetMethod("ConductToNodes", flags), 103);
            Check(!GridChainLoopBounds.Handles(null), "Null target accepted.");            Console.WriteLine("PASS: exact signatures, read-only resolution and atomic rejection.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
