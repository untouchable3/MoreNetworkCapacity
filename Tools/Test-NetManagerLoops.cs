using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class NetManagerLoopTests
{
    public static void MutateCandidate(ref ushort candidate) { candidate = 0; }
    public static void MutateSlot(ref int slot) { slot = 0; }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static List<CodeInstruction> Read(MethodInfo method, out ILGenerator generator)
    {
        DynamicMethod fixture = new DynamicMethod("ManagerLabels", typeof(void), Type.EmptyTypes);
        generator = fixture.GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method, generator);
    }
    private static void Noise(List<CodeInstruction> code, ILGenerator generator, int value)
    {
        Label end = generator.DefineLabel(); CodeInstruction nop = new CodeInstruction(OpCodes.Nop, null); nop.labels.Add(end);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, value),
            new CodeInstruction(OpCodes.Blt, end), nop });
    }
    private static void Verify(MethodInfo method, List<CodeInstruction> code, CodeInstruction[] expected)
    {
        List<CodeInstruction> before = new List<CodeInstruction>();
        foreach (CodeInstruction c in code) before.Add(new CodeInstruction(c));
        int[] found = NetManagerLoopBounds.Resolve(method, code);
        Check(expected == null ? found == null : found != null && found.Length == expected.Length,
            "Unexpected match: " + method.Name);
        if (expected != null)
            for (int i = 0; i < expected.Length; ++i) Check(ReferenceEquals(expected[i], code[found[i]]), "Wrong target identity: " + method.Name);
        Check(before.Count == code.Count, "Input length changed.");
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand)
                && code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count, "Input mutated.");
            for (int j = 0; j < code[i].labels.Count; ++j) Check(code[i].labels[j].Equals(before[i].labels[j]), "Labels mutated.");
            for (int j = 0; j < code[i].blocks.Count; ++j) Check(ReferenceEquals(code[i].blocks[j], before[i].blocks[j]), "EH mutated.");
        }
    }
    private static int Local(CodeInstruction c)
    {
        if (c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Stloc_0) return 0;
        if (c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Stloc_1) return 1;
        if (c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Stloc_2) return 2;
        if (c.opcode == OpCodes.Ldloc_3 || c.opcode == OpCodes.Stloc_3) return 3;
        LocalVariableInfo variable = c.operand as LocalVariableInfo;
        if (variable != null) return variable.LocalIndex;
        LocalBuilder builder = c.operand as LocalBuilder;
        if (builder != null) return builder.LocalIndex;
        return Convert.ToInt32(c.operand);
    }
    private static void Cases(MethodInfo method, List<int> indexes)
    {
        ILGenerator labels;
        for (int variant = 0; variant < 6; ++variant)
        {
            List<CodeInstruction> code = Read(method, out labels);
            CodeInstruction[] selected = indexes.ConvertAll(delegate(int n) { return code[n]; }).ToArray();
            if (variant == 1) code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 2) for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 3) { Noise(code, labels, 32768); Noise(code, labels, 36864); }
            if (variant == 4)
                foreach (CodeInstruction c in selected) { c.labels.Add(labels.DefineLabel()); c.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null)); }
            if (variant == 5)
            {
                ILGenerator extra = new DynamicMethod("AddedLocals", typeof(void), Type.EmptyTypes).GetILGenerator();
                foreach (LocalVariableInfo local in method.GetMethodBody().LocalVariables) extra.DeclareLocal(local.LocalType);
                LocalBuilder added = extra.DeclareLocal(typeof(string));
                code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldnull, null), new CodeInstruction(OpCodes.Stloc, added) });
            }
            Verify(method, code, selected);
        }
        {
            List<CodeInstruction> code = Read(method, out labels);
            ILGenerator a = new DynamicMethod("ExtraString", typeof(void), Type.EmptyTypes).GetILGenerator();
            ILGenerator b = new DynamicMethod("ExtraInt", typeof(void), Type.EmptyTypes).GetILGenerator();
            foreach (LocalVariableInfo local in method.GetMethodBody().LocalVariables)
            { a.DeclareLocal(local.LocalType); b.DeclareLocal(local.LocalType); }
            LocalBuilder text = a.DeclareLocal(typeof(string)), integer = b.DeclareLocal(typeof(int));
            Check(text.LocalIndex == integer.LocalIndex, "Extra-type fixture must reuse one index.");
            code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldnull, null), new CodeInstruction(OpCodes.Stloc, text),
                new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, integer) });
            Verify(method, code, null);
        }
        foreach (OpCode malformed in new[] { OpCodes.Ldloc, OpCodes.Stloc, OpCodes.Ldloca })
        {
            List<CodeInstruction> code = Read(method, out labels);
            code.Insert(0, new CodeInstruction(malformed, new object()));
            Verify(method, code, null);
        }
        bool arrays = method.Name == "OnLocaleChanged" || method.Name == "UpdateData" || method.Name == "LateUpdateData";
        foreach (int bound in indexes)
        {
            List<CodeInstruction> code = Read(method, out labels);
            int literal = (int)code[bound].operand;
            code[bound].operand = literal - 1; Noise(code, labels, literal); Verify(method, code, null);
            code = Read(method, out labels); code[bound + 1].opcode = OpCodes.Bge; Verify(method, code, null);
            code = Read(method, out labels); code.Insert(bound, new CodeInstruction(OpCodes.Conv_U2, null)); Verify(method, code, null);
            code = Read(method, out labels);
            int access = bound - (arrays ? 1 : 5), narrowedIndex = Local(code[access]);
            ILGenerator narrow = new DynamicMethod("ChangedLocalType", typeof(void), Type.EmptyTypes).GetILGenerator();
            for (int i = 0; i < narrowedIndex; ++i) narrow.DeclareLocal(typeof(int));
            LocalBuilder small = narrow.DeclareLocal(typeof(ushort));
            code[access].opcode = OpCodes.Ldloc; code[access].operand = small; Verify(method, code, null);
            code = Read(method, out labels); code[0].labels.Add((Label)code[bound + 1].operand); Verify(method, code, null);
            code = Read(method, out labels);
            int counter = Local(code[bound - 1]);
            Label branch = (Label)code[bound + 1].operand;
            int condition = code.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(branch); });
            int start;
            if (arrays) start = condition;
            else
            {
                Label back = (Label)code[condition + 1].operand;
                start = code.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(back); });
            }
            code.InsertRange(start + 1, new[] { new CodeInstruction(OpCodes.Ldloca, counter), new CodeInstruction(OpCodes.Pop, null) });
            Verify(method, code, null);
            code = Read(method, out labels);
            if (!arrays) { code[condition + 1].opcode = OpCodes.Brfalse; Verify(method, code, null); }
            code = Read(method, out labels);
            int pool = -1;
            if (arrays || method.Name == "ReleaseLaneImplementation")
            {
                for (int i = start; i < bound; ++i)
                {
                    FieldInfo f = code[i].operand as FieldInfo;
                    if (f != null && f.DeclaringType == typeof(NetManager) && (f.Name == "m_nodes" || f.Name == "m_segments")) { pool = i; break; }
                }
            }
            else
                for (int i = bound - 1; i >= start; --i)
                {
                    FieldInfo f = code[i].operand as FieldInfo;
                    if (f != null && f.DeclaringType == typeof(NetManager) && (f.Name == "m_nodes" || f.Name == "m_segments")) { pool = i; break; }
                }
            Check(pool > 0 && code[pool - 1].opcode == OpCodes.Ldarg_0, "Root fixture missing: " + method.Name);
            code[pool - 1].opcode = OpCodes.Call;
            code[pool - 1].operand = AccessTools.PropertyGetter(typeof(ColossalFramework.Singleton<NetManager>), "instance");
            Verify(method, code, null);
            code = Read(method, out labels);
            // Change the actual int increment, preserving bound/candidate totals.
            code[bound - 3].opcode = OpCodes.Sub;
            Verify(method, code, null);
            if (!arrays)
            {
                code = Read(method, out labels);
                code[condition - 1].operand = code[condition + 1].operand;
                Verify(method, code, null);
                code = Read(method, out labels);
                bool changedSource = false;
                if (method.Name == "IsPriorityRoad")
                {
                    for (int i = start - 1; i >= 0; --i)
                    {
                        FieldInfo f = code[i].operand as FieldInfo;
                        if (f != null && f.DeclaringType == typeof(NetSegment) && (f.Name == "m_startNode" || f.Name == "m_endNode"))
                        {
                            code[i].operand = AccessTools.Field(typeof(NetSegment), f.Name == "m_startNode" ? "m_endNode" : "m_startNode");
                            changedSource = true; break;
                        }
                    }
                }
                else
                    for (int i = start - 1; i >= 0; --i)
                    {
                        FieldInfo f = code[i].operand as FieldInfo;
                        if (f == null || code[i].opcode != OpCodes.Ldfld) continue;
                        if (f.DeclaringType == typeof(NetManager) && (f.Name == "m_nodeGrid" || f.Name == "m_segmentGrid"))
                        {
                            code[i].operand = AccessTools.Field(typeof(NetManager), f.Name == "m_nodeGrid" ? "m_segmentGrid" : "m_nodeGrid");
                            changedSource = true; break;
                        }
                        if (method.Name == "ReleaseLaneImplementation" && f.DeclaringType == typeof(NetLane) && f.Name == "m_nodes")
                        { code[i].operand = AccessTools.Field(typeof(NetLane), "m_segment"); changedSource = true; break; }
                    }
                Check(changedSource, "Seed mutation missing: " + method.Name); Verify(method, code, null);
                if (method.Name != "IsPriorityRoad")
                {
                    code = Read(method, out labels); changedSource = false;
                    for (int i = bound - 1; i >= start; --i)
                    {
                        FieldInfo f = code[i].operand as FieldInfo;
                        if (code[i].opcode == OpCodes.Ldfld && f != null && f.DeclaringType == typeof(NetNode) && (f.Name == "m_nextGridNode" || f.Name == "m_nextLaneNode"))
                        {
                            code[i].operand = AccessTools.Field(typeof(NetNode), "m_nextBuildingNode"); changedSource = true; break;
                        }
                        if (f != null && f.DeclaringType == typeof(NetSegment) && f.Name == "m_nextGridSegment")
                        { code[i].operand = AccessTools.Field(typeof(NetSegment), "m_startNode"); changedSource = true; break; }
                    }
                    Check(changedSource, "Link mutation missing: " + method.Name); Verify(method, code, null);
                }
            }
        }
        if (method.Name == "IsPriorityRoad")
            foreach (int selected in indexes)
            {
                List<CodeInstruction> code = Read(method, out labels);
                int get = -1;
                for (int i = selected - 1; i >= 0; --i)
                {
                    MethodInfo call = code[i].operand as MethodInfo;
                    if (call != null && call.DeclaringType == typeof(NetNode) && call.Name == "GetSegment") { get = i; break; }
                }
                Check(get > 0, "Candidate fixture missing.");
                int candidate = Local(code[get + 1]), slot = Local(code[get - 1]);
                foreach (int local in new[] { candidate, slot })
                {
                    code = Read(method, out labels);
                    code.InsertRange(get + 2, new[] { new CodeInstruction(OpCodes.Ldloca, local),
                        new CodeInstruction(OpCodes.Call, typeof(NetManagerLoopTests).GetMethod(local == candidate ? "MutateCandidate" : "MutateSlot")) });
                    Verify(method, code, null);
                    code = Read(method, out labels);
                    code.InsertRange(get + 2, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, local) });
                    Verify(method, code, null);
                }
            }
        // Duplicate all semantic roles with independent branch labels: ambiguity
        // must reject the complete method, not select either copy by position.
        List<CodeInstruction> doubled = Read(method, out labels);
        List<CodeInstruction> copy = new List<CodeInstruction>();
        Dictionary<Label,Label> remap = new Dictionary<Label,Label>();
        foreach (CodeInstruction c in doubled) foreach (Label label in c.labels) if (!remap.ContainsKey(label)) remap.Add(label, labels.DefineLabel());
        foreach (CodeInstruction c in doubled)
        {
            CodeInstruction item = new CodeInstruction(c); item.labels.Clear();
            foreach (Label label in c.labels) item.labels.Add(remap[label]);
            if (item.operand is Label) item.operand = remap[(Label)item.operand];
            Label[] table = item.operand as Label[];
            if (table != null) { Label[] clone = new Label[table.Length]; for(int i = 0; i < table.Length; ++i) clone[i] = remap[table[i]]; item.operand = clone; }
            copy.Add(item);
        }
        doubled.AddRange(copy); Verify(method, doubled, null);
        Console.WriteLine("PASS: " + method.Name + " actual/NOP/noise and per-bound atomic rejection, roots, counters, metadata and ambiguity.");
    }
    public static int Main(string[] args)
    {
        try
        {
            Dictionary<string,List<int>> expected = new Dictionary<string,List<int>>();
            foreach (string line in File.ReadAllLines(args[0]))
            {
                string[] cells = line.Split(new[] { "\",\"" }, StringSplitOptions.None);
                if (cells.Length < 9 || cells[0] != "\"NetManager") continue;
                List<int> indexes;
                if (!expected.TryGetValue(cells[1], out indexes)) { indexes = new List<int>(); expected.Add(cells[1], indexes); }
                indexes.Add(int.Parse(cells[4]));
            }
            int methods = 0, bounds = 0;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(NetManager)))
            {
                if (!NetManagerLoopBounds.Handles(method)) continue;
                ILGenerator generator;
                List<CodeInstruction> code = Read(method, out generator);
                int[] result = NetManagerLoopBounds.Resolve(method, code);
                Console.WriteLine(method.Name + ": " + (result == null ? "REFUSED" : string.Join(",", Array.ConvertAll(result, delegate(int x) { return x.ToString(); }))));
                Check(result != null && result.Length == expected[method.Name].Count, "Original method rejected: " + method.Name);
                for (int i = 0; i < result.Length; ++i) Check(result[i] == expected[method.Name][i], "Wrong bound: " + method.Name);
                Cases(method, expected[method.Name]);
                ++methods; bounds += result.Length;
            }
            Check(methods == 14 && bounds == 24, "Wrong target count.");
            return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
