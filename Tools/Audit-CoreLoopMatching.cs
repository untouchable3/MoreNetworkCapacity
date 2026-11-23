using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Capacity { internal static int NodeLoopLimit = 65024, SegmentLoopLimit = 65024; }
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { ++Errors; }
    }
}

internal static class CoreLoopMatchingAudit
{
    private static readonly MethodInfo Transpile = AccessTools.Method(typeof(CoreLoopPatch), "Transpiler", null, null);
    private static readonly MethodInfo GetOccurrences = AccessTools.Method(typeof(CoreLoopPatch), "GetOccurrences", new[] { typeof(MethodBase) }, null);
    private static string Csv(object value) { return "\"" + Convert.ToString(value).Replace("\"", "\"\"") + "\""; }
    private static void Row(StreamWriter writer, params object[] values)
    {
        for (int i = 0; i < values.Length; ++i) { if (i != 0) writer.Write(','); writer.Write(Csv(values[i])); }
        writer.WriteLine();
    }
    private static List<CodeInstruction> Read(MethodBase method, out ILGenerator generator)
    {
        DynamicMethod fixture = new DynamicMethod("AuditLabels", typeof(void), Type.EmptyTypes);
        generator = fixture.GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method, generator);
    }
    private static HashSet<int> Changed(MethodBase method, List<CodeInstruction> input, Dictionary<CodeInstruction, int> origins, out int errors)
    {
        Dictionary<CodeInstruction, CodeInstruction> before = new Dictionary<CodeInstruction, CodeInstruction>();
        foreach (CodeInstruction c in input) before.Add(c, new CodeInstruction(c));
        int previous = MoreNetworkCapacity.Logging.Errors;
        List<CodeInstruction> output = new List<CodeInstruction>((IEnumerable<CodeInstruction>)Transpile.Invoke(null, new object[] { method, input }));
        errors = MoreNetworkCapacity.Logging.Errors - previous;
        HashSet<int> changed = new HashSet<int>();
        foreach (CodeInstruction c in output)
        {
            CodeInstruction old = before[c];
            if (old.opcode != c.opcode || !object.Equals(old.operand, c.operand))
            {
                int origin;
                changed.Add(origins.TryGetValue(c, out origin) ? origin : -1);
            }
        }
        return changed;
    }
    private static Dictionary<CodeInstruction, int> Origins(List<CodeInstruction> code)
    {
        Dictionary<CodeInstruction, int> result = new Dictionary<CodeInstruction, int>();
        for (int i = 0; i < code.Count; ++i) result.Add(code[i], i);
        return result;
    }
    private static bool Equal(HashSet<int> left, HashSet<int> right)
    {
        if (left.Count != right.Count) return false;
        foreach (int value in left) if (!right.Contains(value)) return false;
        return true;
    }
    private static int Candidates(List<CodeInstruction> code, int constant)
    {
        int count = 0;
        for (int i = 0; i + 1 < code.Count; ++i)
            if (code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand, constant)
                && code[i + 1].opcode.FlowControl == FlowControl.Cond_Branch) ++count;
        return count;
    }
    private static void AddNoise(List<CodeInstruction> code, ILGenerator generator, int constant)
    {
        Label target = generator.DefineLabel();
        CodeInstruction end = new CodeInstruction(OpCodes.Nop, null); end.labels.Add(target);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null),
            new CodeInstruction(OpCodes.Ldc_I4, constant), new CodeInstruction(OpCodes.Blt, target), end });
    }
    private static string Scenario(MethodBase method, string name, int constant, HashSet<int> expected)
    {
        bool preserve = CinematicLoopBounds.Handles(method);
        if (preserve)
        {
            expected = new HashSet<int>();
            foreach (string[] occurrence in (List<string[]>)GetOccurrences.Invoke(null, new object[] { method }))
                expected.Add(int.Parse(occurrence[1]));
        }
        ILGenerator generator;
        List<CodeInstruction> code = Read(method, out generator);
        Dictionary<CodeInstruction, int> origins = Origins(code);
        int first = int.MaxValue;
        foreach (int index in expected)
            if (object.Equals(code[index].operand, constant) && index < first) first = index;
        if (first == int.MaxValue) return "not-applicable";
        if (name == "entry-nop") code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
        else if (name == "comparison-nop") code.Insert(first + 1, new CodeInstruction(OpCodes.Nop, null));
        else if (name == "unrelated-narrowing")
        {
            // Stack-balanced and independent of the existing counter value.
            code.InsertRange(first, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null),
                new CodeInstruction(OpCodes.Conv_U2, null), new CodeInstruction(OpCodes.Pop, null) });
        }
        else if (name == "extra-comparison") AddNoise(code, generator, constant);
        else if (name == "local-type-conflict")
        {
            IList<LocalVariableInfo> locals = method.GetMethodBody().LocalVariables;
            if (locals.Count == 0) throw new Exception("No local type fixture for " + method);
            ILGenerator conflict = new DynamicMethod("WrongDeclaredLocal", typeof(void), Type.EmptyTypes).GetILGenerator();
            LocalBuilder local = conflict.DeclareLocal(locals[0].LocalType == typeof(int) ? typeof(ushort) : typeof(int));
            code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldloc, local), new CodeInstruction(OpCodes.Pop, null) });
        }
        else if (name == "same-count-substitution")
        {
            // Synthetic adversarial rewrite: the actual guard changed its
            // bound, while a harmless comparison keeps the old candidate count.
            code[first].operand = constant - 1;
            AddNoise(code, generator, constant);
        }
        else if (name == "changed-predicate")
        {
            // A conditional branch remains conditional, but its semantics differ.
            code[first + 1].opcode = code[first + 1].opcode == OpCodes.Beq ? OpCodes.Bne_Un : OpCodes.Beq;
        }
        int errors;
        HashSet<int> changed = Changed(method, code, origins, out errors);
        if (changed.Count == 0 && errors > 0) return "refused";
        if (preserve) return changed.Count == 0 && errors == 0 ? "verified-preservation" : "unexpected-camera-change";
        if (changed.Contains(-1)) return "patched-noise";
        if (name == "changed-predicate" && changed.Contains(first)) return "accepted-changed-predicate";
        if (name == "same-count-substitution") return "partial-or-unexpected";
        return Equal(changed, expected) && errors == 0 ? "preserved-targets" : "partial-or-unexpected";
    }
    private static string Matcher(MethodBase method)
    {
        if (CinematicLoopBounds.Handles(method)) return "camera-preserve";
        if (TransportLifecycleLoopBounds.Handles(method)) return "transport-lifecycle";
        if (StationLaneLoopBounds.Handles(method)) return "station-lane";
        if (EventRaceLoopBounds.Handles(method)) return "event-race";
        if (NetManagerLoopBounds.Handles(method)) return "netmanager";
        if (RoadAccessLoopBounds.Handles(method)) return "road-access";
        if (ServicePathLoopBounds.Handles(method)) return "service-path";
        if (LaneChainLoopBounds.Handles(method)) return "lane-chain";
        if (BuildingChainLoopBounds.Handles(method)) return "building-chain-or-pool";
        if (GridChainLoopBounds.Handles(method)) return "grid-or-dam-chain";
        if (NetworkToolLoopBounds.Handles(method)) return "network-tool";
        if (TransportLineMaintenanceBounds.Handles(method)) return "station-maintenance";
        int segments, nextStops;
        if (method.DeclaringType == typeof(TransportLine)
            && (method.Name == "CopyMissingPaths" || TransportLineLoopBounds.TryGetReviewedShape(method.Name, out segments, out nextStops)))
            return "station-chain";
        return "legacy";
    }    public static int Main(string[] args)
    {
        try
        {
            string output = args[0]; Directory.CreateDirectory(output);
            MethodInfo targets = AccessTools.Method(typeof(CoreLoopPatch), "TargetMethods", null, null);
            int methods = 0, changes = 0, semanticMethods = 0, semanticBounds = 0;
            using (StreamWriter summary = new StreamWriter(Path.Combine(output, "methods.csv"), false, System.Text.Encoding.UTF8))
            using (StreamWriter cases = new StreamWriter(Path.Combine(output, "scenarios.csv"), false, System.Text.Encoding.UTF8))
            using (StreamWriter camera = new StreamWriter(Path.Combine(output, "camera-window.csv"), false, System.Text.Encoding.UTF8))
            {
                Row(summary, "Type", "Method", "Signature", "ReviewedBounds", "BaselineChanges", "Raw32768", "Reviewed32768", "Raw36864", "Reviewed36864", "ExtraSameValueBranches", "EntryNop", "Matcher");
                Row(cases, "Type", "Method", "Scenario", "Constant", "Outcome");
                Row(camera, "Type", "Method", "BaselineChanges", "PaddedChanges", "Errors");
                foreach (MethodBase method in (IEnumerable<MethodBase>)targets.Invoke(null, null))
                {
                    ILGenerator generator;
                    List<CodeInstruction> code = Read(method, out generator);
                    string matcher = Matcher(method);
                    if (matcher == "legacy") throw new Exception("No semantic owner: " + method);
                    int rawNode = Candidates(code, 32768), rawSegment = Candidates(code, 36864);
                    List<string[]> occurrences = (List<string[]>)GetOccurrences.Invoke(null, new object[] { method });
                    int reviewedNode = 0, reviewedSegment = 0;
                    foreach (string[] occurrence in occurrences)
                        if (occurrence[3] == "32768") ++reviewedNode; else ++reviewedSegment;
                    int errors;
                    HashSet<int> expected = Changed(method, code, Origins(code), out errors);
                    if (errors != 0) throw new Exception("Original target rejected: " + method);
                    int extra = (reviewedNode > 0 ? rawNode - reviewedNode : 0)
                        + (reviewedSegment > 0 ? rawSegment - reviewedSegment : 0);
                    string shifted = Scenario(method, "entry-nop", reviewedNode > 0 ? 32768 : 36864, expected);
                    Row(summary, method.DeclaringType.FullName, method.Name, method.ToString(), occurrences.Count,
                        expected.Count, rawNode, reviewedNode, rawSegment, reviewedSegment, extra, shifted, matcher);
                    if (matcher != "legacy") { ++semanticMethods; semanticBounds += occurrences.Count; }
                    foreach (int constant in new[] { 32768, 36864 })
                    {
                        if (constant == 32768 && reviewedNode == 0 || constant == 36864 && reviewedSegment == 0) continue;
                        foreach (string scenario in new[] { "entry-nop", "comparison-nop", "extra-comparison",
                            "same-count-substitution", "changed-predicate", "unrelated-narrowing", "local-type-conflict" })
                        {
                            string outcome = Scenario(method, scenario, constant, expected);
                            Row(cases, method.DeclaringType.FullName, method.Name, scenario, constant, outcome);
                            if (matcher != "legacy")
                            {
                                bool positive = scenario == "entry-nop" || scenario == "comparison-nop" || scenario == "extra-comparison";
                                if (outcome != (positive ? (matcher == "camera-preserve" ? "verified-preservation" : "preserved-targets") : "refused"))
                                    throw new Exception("Reviewed matcher regression: " + method + " / " + scenario + " / " + outcome);
                            }
                        }
                    }
                    if (method.DeclaringType == typeof(CinematicCameraController))
                    {
                        List<CodeInstruction> padded = Read(method, out generator);
                        Dictionary<CodeInstruction, int> origins = Origins(padded);
                        List<int> indexes = new List<int>();
                        foreach (string[] occurrence in occurrences) indexes.Add(int.Parse(occurrence[1]));
                        indexes.Sort(); indexes.Reverse();
                        foreach (int index in indexes)
                            for (int i = 0; i < 7; ++i) padded.Insert(index, new CodeInstruction(OpCodes.Nop, null));
                        int cameraErrors;
                        HashSet<int> changed = Changed(method, padded, origins, out cameraErrors);
                        Row(camera, method.DeclaringType.FullName, method.Name, expected.Count, changed.Count, cameraErrors);
                        if (changed.Count != 0 || cameraErrors != 0) throw new Exception("Padded camera preservation failed: " + method);
                    }
                    ++methods; changes += expected.Count;
                }
            }
            int expectedMethods = GeneratedPatchTargets.BranchBoundMethods.Length;
            int expectedChanges = GeneratedPatchTargets.BranchBoundOccurrences.Length - 3;
            if (methods != expectedMethods || changes != expectedChanges) throw new Exception("Unexpected original baseline: " + methods + "/" + changes);
            if (semanticMethods != 137 || semanticBounds != 166) throw new Exception("Migration coverage changed: " + semanticMethods + "/" + semanticBounds);
            Console.WriteLine("Semantic matching: " + semanticMethods + " methods / " + semanticBounds + " bounds; all generated targets have reviewed rules; no legacy fallback.");
            Console.WriteLine("Screened " + methods + " actual game targets and " + (expectedChanges + 3)
                + " reviewed bounds; " + changes + " baseline changes/three explicitly preserved camera bounds. Synthetic cases are matcher probes, not observed mod conflicts.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
