using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class NetworkToolLoopTests
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
        int[] result = NetworkToolLoopBounds.Resolve(method, code);
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
        Check(NetworkToolLoopBounds.Handles(method), "Reviewed signature not recognized.");
        Check(method.IsStatic == (method.DeclaringType == typeof(NetTool)), "Actual static/instance identity changed.");
        List<CodeInstruction> original = Read(method);
        int guard = Resolve(method, original, true, "original");
        Check(guard == expectedIndex, "Wrong actual-game occurrence.");
        bool dictionary = method.DeclaringType == typeof(NetAdjust);
        int bound = dictionary ? 36864 : 32768;
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
        if (!dictionary)
        {
            string field = method.DeclaringType == typeof(BuildingTool) ? "m_nextBuildingNode" : "m_nextGridNode";
            int next = FindField(original, typeof(NetNode), field);
            Check(next >= 0, "Missing real node traversal field.");
            code = Clone(original);
            code[next].operand = AccessTools.Field(typeof(NetNode), field == "m_nextGridNode" ? "m_nextBuildingNode" : "m_nextGridNode");
            Resolve(method, code, false, "different node traversal");
            code = Clone(original);
            // This is the pool immediately supplying the reviewed cursor update,
            // not an unrelated node access elsewhere in the long helper method.
            code[next - 4].operand = AccessTools.Field(typeof(NetManager), "m_segments");
            Resolve(method, code, false, "different pool");
            code = Clone(original); code[next - 3].operand = AccessTools.Field(typeof(Array16<NetSegment>), "m_buffer");
            Resolve(method, code, false, "different array");
            code = Clone(original); code[next - 1].operand = typeof(NetSegment);
            Resolve(method, code, false, "different element type");
        }
        else
        {
            int map = FindField(original, typeof(NetAdjust), "m_segmentData");
            Check(map >= 0, "Missing dictionary fixture.");
            // There are earlier accesses; select the one immediately before the
            // reviewed source-chain update at the end of the method.
            map = guard - 12;
            code = Clone(original); code[map].operand = AccessTools.Field(typeof(NetAdjust), "m_segmentQueue");
            Resolve(method, code, false, "different dictionary source");
            code = Clone(original); code[guard - 7].operand = AccessTools.Field(typeof(NetNode), "m_nextGridNode");
            Resolve(method, code, false, "different predecessor field");
            code = Clone(original); code[guard - 10].opcode = OpCodes.Call;
            Resolve(method, code, false, "different dictionary operation");
            Check(original[191].opcode == OpCodes.Ldc_I4 && object.Equals(original[191].operand, 36864),
                "Expected unrelated routing-score constant.");
            code = Clone(original); code[191].operand = 17;
            Check(Resolve(method, code, true, "unrelated score constant") == guard, "Score treated as loop bound.");
        }
        code = Clone(original);
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
            MethodBase building = typeof(BuildingTool).GetMethod("GetAllZoneBlocks", flags);
            MethodBase adjust = null, helper = null;
            foreach (MethodInfo m in typeof(NetAdjust).GetMethods(flags)) if (NetworkToolLoopBounds.Handles(m)) adjust = m;
            foreach (MethodInfo m in typeof(NetTool).GetMethods(flags)) if (NetworkToolLoopBounds.Handles(m)) helper = m;
            Check(building != null && adjust != null && helper != null, "Actual reviewed targets unavailable.");
            Test(building, 118); Test(adjust, 283); Test(helper, 405);
            Check(!NetworkToolLoopBounds.Handles(null), "Null target accepted.");
            Check(NetworkToolLoopBounds.Resolve(building, null) == null, "Null input accepted.");
            MethodInfo unrelated = typeof(NetAdjust).GetMethod("CalculatePath", flags, null, new Type[] { typeof(ushort), typeof(int) }, null);
            Check(unrelated != null && !NetworkToolLoopBounds.Handles(unrelated), "Different overload handled.");
            Check(NetworkToolLoopBounds.Resolve(unrelated, Read(unrelated)) == null, "Unknown overload resolved.");
            Console.WriteLine("PASS: exact signatures, read-only resolution and atomic rejection.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

