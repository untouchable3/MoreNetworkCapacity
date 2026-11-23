using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class EventRaceTests
{
    private static int assertions;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); ++assertions; }
    private static List<CodeInstruction> Read(MethodInfo method, out ILGenerator il)
    { il = new DynamicMethod("EventLabels", typeof(void), Type.EmptyTypes).GetILGenerator(); return PatchProcessor.GetOriginalInstructions(method, il); }
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in code) result.Add(new CodeInstruction(c)); return result; }
    private static int[] Resolve(MethodInfo method, List<CodeInstruction> code)
    {
        List<CodeInstruction> before = Clone(code); int[] result = EventRaceLoopBounds.Resolve(method, code);
        Check(before.Count == code.Count, "Resolver changed count");
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand), "Resolver changed IL");
            Check(code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count, "Resolver changed metadata");
            for (int j = 0; j < code[i].labels.Count; ++j) Check(code[i].labels[j].Equals(before[i].labels[j]), "Label changed");
            for (int j = 0; j < code[i].blocks.Count; ++j) Check(ReferenceEquals(code[i].blocks[j], before[i].blocks[j]), "EH changed");
        }
        return result;
    }
    private static void Reject(MethodInfo method, List<CodeInstruction> code, string reason)
    { Check(Resolve(method, code) == null, method.DeclaringType.Name + "." + method.Name + " accepted " + reason); }
    private static MethodInfo Method(string type, string name)
    {
        foreach (MethodInfo m in typeof(NetManager).Assembly.GetType(type, true).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            if (m.Name == name && EventRaceLoopBounds.Handles(m)) return m;
        throw new Exception("Reviewed target missing: " + type + "." + name);
    }
    private static int Local(CodeInstruction c)
    {
        if (c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Stloc_0) return 0;
        if (c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Stloc_1) return 1;
        if (c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Stloc_2) return 2;
        if (c.opcode == OpCodes.Ldloc_3 || c.opcode == OpCodes.Stloc_3) return 3;
        LocalVariableInfo v = c.operand as LocalVariableInfo; if (v != null) return v.LocalIndex;
        if (c.operand is int) return (int)c.operand; if (c.operand is byte) return (byte)c.operand; return -1;
    }
    private static void Noise(List<CodeInstruction> code, ILGenerator il)
    {
        Label end = il.DefineLabel(); CodeInstruction last = new CodeInstruction(OpCodes.Nop, null); last.labels.Add(end);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, 32768),
            new CodeInstruction(OpCodes.Ble, end), new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Ldc_I4, 36864), new CodeInstruction(OpCodes.Blt, end), last });
        for (int i = code.Count - 1; i >= 7; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
    }
    private static void Duplicate(List<CodeInstruction> code, ILGenerator il)
    {
        Dictionary<Label, Label> labels = new Dictionary<Label, Label>();
        foreach (CodeInstruction c in code) foreach (Label label in c.labels) if (!labels.ContainsKey(label)) labels.Add(label, il.DefineLabel());
        List<CodeInstruction> copy = Clone(code);
        foreach (CodeInstruction c in copy)
        {
            for (int i = 0; i < c.labels.Count; ++i) c.labels[i] = labels[c.labels[i]];
            if (c.operand is Label) c.operand = labels[(Label)c.operand];
            Label[] targets = c.operand as Label[];
            if (targets != null) { Label[] mapped = new Label[targets.Length]; for (int i = 0; i < targets.Length; ++i) mapped[i] = labels[targets[i]]; c.operand = mapped; }
        }
        code.AddRange(copy);
    }
    private static void Test(string type, string name, params int[] expected)
    {
        MethodInfo m = Method(type, name); ILGenerator il; List<CodeInstruction> original = Read(m, out il);
        int[] found = Resolve(m, original);
        Check(found != null && found.Length == expected.Length, "Original mismatch: " + type + "." + name);
        for (int i = 0; i < expected.Length; ++i) Check(found[i] == expected[i], "Wrong original bound " + name);
        List<CodeInstruction> changed = Clone(original); Noise(changed, il); found = Resolve(m, changed);
        Check(found != null && found.Length == expected.Length, "Noise/NOP mismatch " + name);
        changed = Clone(original); Duplicate(changed, il); Reject(m, changed, "two independently matching layouts");
        foreach (int guard in expected)
        {
            changed = Clone(original); changed[guard].operand = 12345; Reject(m, changed, "missing bound");
            changed = Clone(original); changed[guard + 1].opcode = OpCodes.Bge; Reject(m, changed, "changed predicate");
            changed = Clone(original); changed.Insert(guard, new CodeInstruction(OpCodes.Conv_U2, null)); Reject(m, changed, "narrowed counter");
            changed = Clone(original); changed[0].labels.Add((Label)changed[guard + 1].operand); Reject(m, changed, "ambiguous label");
            changed = Clone(original); changed[guard + 7].operand = original[guard + 1].operand; Reject(m, changed, "changed error exit");
            int counter = Local(original[guard - 1]); Check(counter >= 0, "No counter fixture");
            DynamicMethod narrow = new DynamicMethod("Narrow", typeof(void), Type.EmptyTypes); ILGenerator ni = narrow.GetILGenerator();
            for (int i = 0; i < counter; ++i) ni.DeclareLocal(typeof(int)); LocalBuilder small = ni.DeclareLocal(typeof(ushort));
            changed = Clone(original); changed[guard - 5].opcode = OpCodes.Ldloc; changed[guard - 5].operand = small; Reject(m, changed, "wrong typed counter read");
            changed = Clone(original); changed[guard - 1].opcode = OpCodes.Stloc; changed[guard - 1].operand = small; Reject(m, changed, "wrong typed counter store");
        }
        int relevantRoot = -1;
        for (int i = 0; i < original.Count; ++i)
                if (original[i].opcode == OpCodes.Ldfld && AccessTools.Field(typeof(NetManager), "m_nodes").Equals(original[i].operand)) { relevantRoot = i; if (name != "InvertRoadDirection") break; }
        if (name != "InvertRoadDirection" && name != "FindStartSegment")
            for (int i = 0; i < original.Count; ++i)
                if (original[i].opcode == OpCodes.Ldfld && AccessTools.Field(typeof(NetManager), "m_segments").Equals(original[i].operand)) { relevantRoot = i; break; }
        Check(relevantRoot >= 0, "No root fixture " + name); changed = Clone(original);
        changed[relevantRoot].operand = AccessTools.Field(typeof(NetManager), name == "FindStartSegment" || name == "InvertRoadDirection" ? "m_segments" : "m_nodes");
        Reject(m, changed, "wrong source pool");
        // A body counter address escape is unsupported even though its local
        // declaration remains Int32 and the original guard tail is unchanged.
        int last = expected[expected.Length - 1], target = -1; Label condition = (Label)original[last + 1].operand;
        for (int i = 0; i < original.Count; ++i) if (original[i].labels.Contains(condition)) target = i;
        Label back = (Label)original[target + 1].operand; int start = -1;
        for (int i = 0; i < original.Count; ++i) if (original[i].labels.Contains(back)) start = i;
        changed = Clone(original); changed.InsertRange(start + 2, new[] { new CodeInstruction(OpCodes.Ldloca, Local(original[last - 1])), new CodeInstruction(OpCodes.Pop, null) });
        Reject(m, changed, "counter address escape");
        DynamicMethod unrelated = new DynamicMethod("Unrelated", typeof(void), Type.EmptyTypes); ILGenerator ui = unrelated.GetILGenerator();
        for (int i = 0; i < m.GetMethodBody().LocalVariables.Count; ++i) ui.DeclareLocal(typeof(int)); LocalBuilder added = ui.DeclareLocal(typeof(int));
        changed = Clone(original); changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, added), new CodeInstruction(OpCodes.Ldloc, added), new CodeInstruction(OpCodes.Pop, null) });
        Check(Resolve(m, changed) != null, "Typed unrelated local rejected " + name);
        changed = Clone(original); changed.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldloc, added.LocalIndex), new CodeInstruction(OpCodes.Pop, null) });
        Reject(m, changed, "new local without type");
        int completion = name == "BreakEventRoute" ? 376 : name == "BuildEventRoute" ? 685
            : name == "CalculateEventRoute" ? 200 : name == "UpdateEventRoute" ? 280
            : name == "UpdateEventRouteRoadWarnings" ? 105 : name == "GenerateTrackPath" ? 134
            : name == "GenerateTrackSpline" ? 120 : name == "CalculatePosition" ? 337 : -1;
        if (completion >= 0)
        {
            changed = Clone(original); changed[completion].opcode = OpCodes.Beq;
            Reject(m, changed, "reversed ring completion predicate");
            changed = Clone(original); changed[completion].operand = original[last + 1].operand;
            Reject(m, changed, "ring continuation redirected to counter condition");
        }
        if (name == "BuildEventRoute")
        {
            changed = Clone(original); changed[57].operand = AccessTools.Field(typeof(EventRouteData), "m_endNode");
            Reject(m, changed, "generated companion rooted at route end node");
            changed = Clone(original); changed.InsertRange(247, new[] {new CodeInstruction(OpCodes.Ldloca, 16), new CodeInstruction(OpCodes.Pop, null)});
            Reject(m, changed, "generated current node address escape");
            changed = Clone(original); changed.InsertRange(683, new[] {new CodeInstruction(OpCodes.Ldloca, 44), new CodeInstruction(OpCodes.Pop, null)});
            Reject(m, changed, "generated saved node address escape");
            changed = Clone(original); changed.InsertRange(247, new[] {new CodeInstruction(OpCodes.Ldc_I4_0, null), new CodeInstruction(OpCodes.Stloc, 16)});
            Reject(m, changed, "generated companion extra assignment");
        }
        if (name == "UpdateEventRoute")
        {
            changed = Clone(original); changed[273].opcode = OpCodes.Brfalse;
            Reject(m, changed, "zero next segment cleanup predicate reversed");
            changed = Clone(original); changed[277].operand = original[last + 1].operand;
            Reject(m, changed, "zero next segment cleanup exit redirected");
        }
        int nodeSeed = name == "CalculateEventRoute" ? 56 : name == "UpdateEventRoute" ? 154
            : name == "UpdateEventRouteRoadWarnings" ? 57 : name == "GenerateTrackPath" ? 49
            : name == "GenerateTrackSpline" ? 49 : name == "CalculatePosition" ? 84 : -1;
        if (nodeSeed >= 0)
        {
            changed = Clone(original); changed[nodeSeed].operand = AccessTools.Field(typeof(EventRouteData), "m_endNode");
            Reject(m, changed, "route companion rooted at end node");
        }
        int sentinelSeed = name == "UpdateEventRouteRoadWarnings" ? 63 : name == "GenerateTrackPath" ? 55 : name == "GenerateTrackSpline" ? 55 : -1;
        if (sentinelSeed >= 0)
        {
            changed = Clone(original); changed[sentinelSeed].operand = AccessTools.Field(typeof(EventRouteData), "m_startNode");
            Reject(m, changed, "route completion sentinel rooted at start node");
            int compare = name == "UpdateEventRouteRoadWarnings" ? 34 : 26;
            changed = Clone(original); changed[compare].opcode = OpCodes.Beq;
            Reject(m, changed, "reversed initial opposite endpoint predicate");
        }
        if (name == "BreakEventRoute")
        {
            DynamicMethod fork = new DynamicMethod("UnrelatedSegments", typeof(void), Type.EmptyTypes); ILGenerator fi = fork.GetILGenerator();
            for (int i = 0; i < m.GetMethodBody().LocalVariables.Count; ++i) fi.DeclareLocal(typeof(int));
            LocalBuilder otherSegments = fi.DeclareLocal(typeof(NetSegment[]));
            changed = Clone(original); changed[71] = new CodeInstruction(OpCodes.Ldloc, otherSegments);
            changed.InsertRange(0, new[] {new CodeInstruction(OpCodes.Ldc_I4, 65024), new CodeInstruction(OpCodes.Newarr, typeof(NetSegment)), new CodeInstruction(OpCodes.Stloc, otherSegments)});
            Reject(m, changed, "unlink writes to independent segment array");
        }
        Console.WriteLine("PASS: " + type + "." + name + " (" + expected.Length + " guards)");
    }
    public static int Main()
    {
        try
        {
            Test("EventManager", "BreakEventRoute", 84, 395);
            Test("EventManager", "BuildEventRoute", 761);
            Test("EventManager", "CalculateEventRoute", 211);
            Test("EventManager", "UpdateEventRoute", 305);
            Test("EventManager", "UpdateEventRouteRoadWarnings", 112);
            Test("EventManager", "UpdateProp", 163);
            Test("RaceEventAI", "GenerateTrackPath", 141);
            Test("RaceEventAI", "GenerateTrackSpline", 127);
            Test("RaceRoadBaseAI", "FindStartSegment", 112);
            Test("RaceStartBuildingAI", "CalculatePosition", 346);
            Test("RaceStartBuildingAI", "InvertRoadDirection", 137);
            Check(!EventRaceLoopBounds.Handles(typeof(EventRaceTests).GetMethod("Main")), "Unknown method accepted");
            Console.WriteLine("PASS: 11 actual Event/Race methods, 12 guards; semantic selection, noise/NOP, ambiguity, provenance, local types, predicates, exits, addresses, metadata and atomic rejection (" + assertions + " assertions).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
