using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class ServicePathTests
{
    private static int tests;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); ++tests; }
    private static List<CodeInstruction> Read(MethodInfo m, out ILGenerator il)
    {
        il = new DynamicMethod("ServiceLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(m, il);
    }
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in code) result.Add(new CodeInstruction(c)); return result; }
    private static void Duplicate(List<CodeInstruction> code, ILGenerator il)
    {
        Dictionary<Label, Label> labels = new Dictionary<Label, Label>();
        foreach (CodeInstruction c in code) foreach (Label old in c.labels) if (!labels.ContainsKey(old)) labels.Add(old, il.DefineLabel());
        List<CodeInstruction> copy = Clone(code);
        foreach (CodeInstruction c in copy)
        {
            for (int j = 0; j < c.labels.Count; ++j) c.labels[j] = labels[c.labels[j]];
            if (c.operand is Label) c.operand = labels[(Label)c.operand];
            Label[] targets = c.operand as Label[];
            if (targets != null) { Label[] remap = new Label[targets.Length]; for (int j = 0; j < targets.Length; ++j) remap[j] = labels[targets[j]]; c.operand = remap; }
        }
        code.AddRange(copy);
    }
    private static int[] Resolve(MethodInfo m, List<CodeInstruction> code)
    {
        List<CodeInstruction> before = Clone(code);
        int[] result = ServicePathLoopBounds.Resolve(m, code);
        Check(before.Count == code.Count, "Resolver changed count");
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand), "Resolver changed instruction");
            Check(code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count, "Resolver changed metadata");
            for (int j = 0; j < code[i].labels.Count; ++j) Check(code[i].labels[j].Equals(before[i].labels[j]), "Resolver moved label");
            for (int j = 0; j < code[i].blocks.Count; ++j) Check(ReferenceEquals(code[i].blocks[j], before[i].blocks[j]), "Resolver changed EH");
        }
        return result;
    }
    private static MethodInfo Target(string type, string name, int[] expected)
    {
        Type t = typeof(NetManager).Assembly.GetType(type, true);
        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            if (m.Name == name && ServicePathLoopBounds.Handles(m)) return m;
        throw new Exception("No reviewed target: " + type + "." + name);
    }
    private static int Local(CodeInstruction c)
    {
        if (c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Stloc_0) return 0;
        if (c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Stloc_1) return 1;
        if (c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Stloc_2) return 2;
        if (c.opcode == OpCodes.Ldloc_3 || c.opcode == OpCodes.Stloc_3) return 3;
        LocalBuilder b = c.operand as LocalBuilder; if (b != null) return b.LocalIndex;
        LocalVariableInfo v = c.operand as LocalVariableInfo; if (v != null) return v.LocalIndex;
        if (c.operand is int) return (int)c.operand; if (c.operand is byte) return (byte)c.operand;
        return -1;
    }
    private static void Reject(MethodInfo m, List<CodeInstruction> code, string reason)
    { Check(Resolve(m, code) == null, m.DeclaringType.Name + "." + m.Name + ": accepted " + reason); }
    private static void Test(string type, string name, params int[] expected)
    {
        MethodInfo m = Target(type, name, expected); ILGenerator il;
        List<CodeInstruction> original = Read(m, out il);
        int[] actual = Resolve(m, original);
        Check(actual != null && actual.Length == expected.Length, "Original mismatch " + type + "." + name);
        for (int i = 0; i < expected.Length; ++i) Check(actual[i] == expected[i], "Wrong original guard " + name);
        List<CodeInstruction> changed = Clone(original);
        Label after = il.DefineLabel(); CodeInstruction marker = new CodeInstruction(OpCodes.Nop, null); marker.labels.Add(after);
        changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, 32768),
            new CodeInstruction(OpCodes.Blt, after), new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, 36864),
            new CodeInstruction(OpCodes.Blt, after), marker });
        // NOPs throughout retain labels on original instructions and exercise
        // every adjacency/provenance operation without changing branch bodies.
        for (int i = changed.Count - 1; i >= 7; --i) changed.Insert(i, new CodeInstruction(OpCodes.Nop, null));
        actual = Resolve(m, changed);
        Check(actual != null && actual.Length == expected.Length, "Noise/NOP mismatch " + name);
        for (int i = 0; i < expected.Length; ++i) Check(object.Equals(changed[actual[i]].operand, original[expected[i]].operand), "Decoy selected");
        changed = Clone(original); Duplicate(changed, il); Reject(m, changed, "two independently labeled matching traversals");
        foreach (int selected in expected)
        {
            changed = Clone(original); changed[selected].operand = 12345; Reject(m, changed, "missing bound");
            changed = Clone(original); changed[selected + 1].opcode = OpCodes.Bge; Reject(m, changed, "wrong predicate");
            changed = Clone(original); changed.Insert(selected, new CodeInstruction(OpCodes.Conv_U2, null)); Reject(m, changed, "narrow counter");
            changed = Clone(original);
            if (changed[selected + 1].operand is Label)
            { changed[0].labels.Add((Label)changed[selected + 1].operand); Reject(m, changed, "ambiguous label"); }
            int counterAccess = selected - 1;
            // Array for loops compare the counter immediately before bound;
            // chains store it there. Both must reject a changed local type.
            int counter = Local(original[counterAccess]);
            Check(counter >= 0, "Counter fixture absent " + name);
            DynamicMethod narrow = new DynamicMethod("Narrow", typeof(void), Type.EmptyTypes); ILGenerator ni = narrow.GetILGenerator();
            for (int i = 0; i < counter; ++i) ni.DeclareLocal(typeof(int));
            LocalBuilder small = ni.DeclareLocal(typeof(ushort));
            changed = Clone(original);
            changed[counterAccess].opcode = original[counterAccess].opcode.Name.StartsWith("stloc") ? OpCodes.Stloc : OpCodes.Ldloc;
            changed[counterAccess].operand = small; Reject(m, changed, "changed declared counter type");
            changed = Clone(original);
            changed[selected - 5].opcode = OpCodes.Ldloc; changed[selected - 5].operand = small;
            Reject(m, changed, "same-index narrowed increment counter read");
        }
        DynamicMethod extra = new DynamicMethod("UnreviewedLocal", typeof(void), Type.EmptyTypes); ILGenerator ei = extra.GetILGenerator();
        for (int i = 0; i < m.GetMethodBody().LocalVariables.Count; ++i) ei.DeclareLocal(typeof(int));
        LocalBuilder added = ei.DeclareLocal(typeof(int)); changed = Clone(original);
        changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, added),
            new CodeInstruction(OpCodes.Ldloc, added), new CodeInstruction(OpCodes.Pop, null) });
        actual = Resolve(m, changed); Check(actual != null && actual.Length == expected.Length, "Typed unrelated added local rejected " + name);
        changed = Clone(original);
        changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldloc, added.LocalIndex), new CodeInstruction(OpCodes.Pop, null) });
        Reject(m, changed, "added local without declared type");
        DynamicMethod extraWrong = new DynamicMethod("ConflictingNewLocal", typeof(void), Type.EmptyTypes); ILGenerator ew = extraWrong.GetILGenerator();
        for (int i = 0; i < m.GetMethodBody().LocalVariables.Count; ++i) ew.DeclareLocal(typeof(int));
        LocalBuilder addedSmall = ew.DeclareLocal(typeof(ushort)); changed = Clone(original);
        changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, added),
            new CodeInstruction(OpCodes.Ldloc, addedSmall), new CodeInstruction(OpCodes.Pop, null) });
        Reject(m, changed, "inconsistent declared types for same added index");
        changed = Clone(original); int moved = Local(original[expected[expected.Length - 1] - 1]);
        foreach (CodeInstruction c in changed)
        {
            if ((!c.opcode.Name.StartsWith("ldloc") && !c.opcode.Name.StartsWith("stloc")) || Local(c) != moved) continue;
            c.opcode = c.opcode.Name.StartsWith("stloc") ? OpCodes.Stloc : c.opcode.Name.StartsWith("ldloca") ? OpCodes.Ldloca : OpCodes.Ldloc;
            c.operand = added;
        }
        Reject(m, changed, "critical counter moved to unreviewed new slot");
        // Root: an exactly typed but unrelated NetManager field is not a match.
        int root = -1;
        for (int i = 0; i < original.Count; ++i)
        {
            FieldInfo f = original[i].operand as FieldInfo;
            if (original[i].opcode != OpCodes.Ldfld || f == null) continue;
            if (type == "CoverageManager" && name == "UpdateCoverage")
            { if (f.DeclaringType == typeof(CoverageManager) && f.Name == "m_distances") { root = i; break; } continue; }
            if (type == "PathFind")
            { if (f.DeclaringType == typeof(NetManager) && f.Name == "m_nodes") { root = i; break; } continue; }
            if (name == "FindFireCoverage" || name == "DestroyNetSegments" || name == "FindPathPosition" || name == "UpdateGrid" || name == "CalculateBlock1")
            { if (f.DeclaringType == typeof(NetManager) && f.Name == "m_segmentGrid") { root = i; break; } continue; }
            if (f.DeclaringType == typeof(NetManager)
                && (f.Name == "m_segmentGrid" || f.Name == "m_nodes" || f.Name == "m_segments")) { root = i; break; }
        }
        Check(root >= 0, "Missing root fixture " + name);
        changed = Clone(original); FieldInfo rootField = (FieldInfo)changed[root].operand;
        changed[root].operand = rootField.DeclaringType == typeof(CoverageManager)
            ? AccessTools.Field(typeof(CoverageManager), "m_nodes")
            : AccessTools.Field(typeof(NetManager), rootField.Name == "m_segmentGrid" ? "m_nodeGrid" : rootField.Name == "m_nodes" ? "m_segments" : "m_nodes");
        Reject(m, changed, "changed root");
        // Inject a valid address read of the loop counter in the body. Resolving
        // does not execute it; allowing such escapes would hide by-ref mutation.
        int guard = expected[expected.Length - 1]; int branchTarget = -1;
        Label label = (Label)original[guard + 1].operand;
        for (int i = 0; i < original.Count; ++i) if (original[i].labels.Contains(label)) branchTarget = i;
        int start = branchTarget;
        if (branchTarget > guard)
        {
            Label back = (Label)original[branchTarget + 1].operand;
            for (int i = 0; i < original.Count; ++i) if (original[i].labels.Contains(back)) start = i;
        }
        changed = Clone(original); int counterLocal = Local(original[guard - 1]);
        changed.InsertRange(start + 1, new[] { new CodeInstruction(OpCodes.Ldloca, counterLocal), new CodeInstruction(OpCodes.Pop, null) });
        Reject(m, changed, "counter address escape");
        // Redirect the guard's failure/exit edge while retaining valid bound,
        // counter and cursor provenance. Never guess an altered exit.
        if (branchTarget > guard)
        {
            changed = Clone(original); int exitJump = guard + (type == "PathFind" ? 2 : 7);
            Check(changed[exitJump].opcode == OpCodes.Br, "Unexpected handler fixture " + name);
            changed[exitJump].operand = original[guard + 1].operand; Reject(m, changed, "changed handler exit");
        }
        if (type == "LegacyMetroUtils" || type == "DisasterHelpers")
        {
            int savedStore = -1;
            for (int i = 0; i < original.Count - 1; ++i)
            {
                FieldInfo f = original[i].operand as FieldInfo;
                if (original[i].opcode == OpCodes.Ldfld && f != null
                    && f.Name == (type == "LegacyMetroUtils" ? "m_nextLaneNode" : "m_nextGridSegment"))
                { savedStore = i + 1; break; }
            }
            Check(savedStore >= 0, "Saved-next fixture absent");
            changed = Clone(original);
            changed.InsertRange(savedStore + 1, new[] { new CodeInstruction(OpCodes.Ldloca, Local(original[savedStore])), new CodeInstruction(OpCodes.Pop, null) });
            Reject(m, changed, "saved-next address escape");
            changed = Clone(original);
            changed[savedStore - 1].operand = AccessTools.Field(type == "LegacyMetroUtils" ? typeof(NetNode) : typeof(NetSegment),
                type == "LegacyMetroUtils" ? "m_nextGridNode" : "m_endNode");
            Reject(m, changed, "saved-next wrong link provenance");
        }
        if (type == "DisasterHelpers" || type == "WaterManager")
        {
            FieldInfo gridField = AccessTools.Field(typeof(NetManager), "m_segmentGrid"); int gridStore = -1;
            for (int i = 0; i < original.Count - 1; ++i) if (gridField.Equals(original[i].operand)) { gridStore = i + 1; break; }
            Check(gridStore >= 0, "Cached grid fixture absent");
            changed = Clone(original);
            changed.InsertRange(start + 20, new[] { new CodeInstruction(OpCodes.Ldloca, Local(original[gridStore])), new CodeInstruction(OpCodes.Pop, null) });
            Reject(m, changed, "cached grid address escape inside loop");
        }
        if (type == "DisasterHelpers")
        { changed = Clone(original); changed[231].opcode = OpCodes.Brtrue; Reject(m, changed, "inverted secondary nonzero exit"); }
        if (type == "PathFind")
        {
            DynamicMethod wrongItem = new DynamicMethod("WrongItem", typeof(void), Type.EmptyTypes); ILGenerator wi = wrongItem.GetILGenerator();
            for (int i = 0; i < 16; ++i) wi.DeclareLocal(typeof(int)); LocalBuilder notItem = wi.DeclareLocal(typeof(int));
            changed = Clone(original); changed[688].operand = notItem; Reject(m, changed, "changed BufferItem source type");
        }
        Console.WriteLine("PASS: " + type + "." + name + " (" + expected.Length + " reviewed bounds)");
    }
    public static int Main()
    {
        try
        {
            Test("CoverageManager", "FindFireCoverage", 166);
            Test("CoverageManager", "SetMode", 33);
            Test("CoverageManager", "UpdateCoverage", 62, 546, 629);
            Test("DisasterHelpers", "DestroyNetSegments", 254);
            Test("LegacyMetroUtils", "PatchSegment", 57);
            Test("PathFind", "PathFindImplementation", 798);
            Test("PathManager", "FindPathPosition", 495);
            Test("UnlockManager", "FixMilestones", 41);
            Test("WaterManager", "UpdateGrid", 488);
            Test("ZoneBlock", "CalculateBlock1", 456);
            Check(!ServicePathLoopBounds.Handles(typeof(ServicePathTests).GetMethod("Main")), "Unknown owner accepted");
            Console.WriteLine("PASS: 10 actual service/path methods, 12 bounds; original/NOP/decoy/provenance/type/address/predicate/exit negatives and resolver input immutability (" + tests + " assertions).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
