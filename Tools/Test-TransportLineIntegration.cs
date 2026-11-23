using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static partial class TransportLineLoopTests
{
    public static MethodInfo EconomyFetch, EconomyReplacement;
    public static void IgnoreDebugPrint(List<CodeInstruction> code, bool force) { }
    private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs request)
    {
        string name = new AssemblyName(request.Name).Name + ".dll";
        string path = Path.Combine(Path.Combine(Environment.GetEnvironmentVariable("MNC_STEAM_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"), "steamapps/common/Cities_Skylines/Cities_Data/Managed"), name);
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
    private static void Emit(ILGenerator il, CodeInstruction c)
    {
        foreach (Label label in c.labels) il.MarkLabel(label);
        object value = c.operand;
        if (value == null) il.Emit(c.opcode);
        else if (value is MethodInfo) il.Emit(c.opcode, (MethodInfo)value);
        else if (value is ConstructorInfo) il.Emit(c.opcode, (ConstructorInfo)value);
        else if (value is FieldInfo) il.Emit(c.opcode, (FieldInfo)value);
        else if (value is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)value);
        else if (value is Label) il.Emit(c.opcode, (Label)value);
        else if (value is int) il.Emit(c.opcode, (int)value);
        else if (value is byte) il.Emit(c.opcode, (byte)value);
        else if (value is sbyte) il.Emit(c.opcode, (sbyte)value);
        else if (value is short) il.Emit(c.opcode, (short)value);
        else if (value is string) il.Emit(c.opcode, (string)value);
        else if (value is Type) il.Emit(c.opcode, (Type)value);
        else throw new Exception("Unexpected TLM transpiler operand: " + value.GetType());
    }
    private static void TestTlm(string path)
    {
        Assembly tlm = Assembly.LoadFrom(path);
        Check(tlm.GetName().Version.ToString() == "14.7.0.37334", "Unexpected local TLM version.");
        Type type = tlm.GetType("TransportLinesManager.Overrides.TransportLineOverrides", true);
        MethodInfo rewrite = AccessTools.Method(type, "TranspileSimulationStep", null, null);
        EconomyFetch = typeof(EconomyManager).GetMethod("FetchResource",
            new[] { typeof(EconomyManager.Resource), typeof(int), typeof(ItemClass) });
        EconomyReplacement = AccessTools.Method(type, "DoTransportLineEconomyManagement", null, null);
        Check(EconomyFetch != null && EconomyReplacement != null && rewrite != null, "TLM anchors missing.");
        DynamicMethod replay = new DynamicMethod("ActualTlmTranspiler", typeof(IEnumerable<CodeInstruction>),
            new[] { typeof(IEnumerable<CodeInstruction>) }, typeof(TransportLineLoopTests).Module, true);
        ILGenerator il = replay.GetILGenerator();
        foreach (LocalVariableInfo local in rewrite.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        int substitutions = 0;
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(rewrite, il))
        {
            // Supply reflection constants and suppress optional debug output;
            // execute no game managers/settings or TLM lifecycle callbacks.
            FieldInfo field = instruction.operand as FieldInfo;
            if (instruction.opcode == OpCodes.Ldsfld && field != null && field.DeclaringType == type)
            {
                if (field.Name == "m_economyManagerCallFetch") instruction.operand = typeof(TransportLineLoopTests).GetField("EconomyFetch");
                else if (field.Name == "m_doTransportLineEconomyManagement") instruction.operand = typeof(TransportLineLoopTests).GetField("EconomyReplacement");
                else throw new Exception("Unexpected TLM static field: " + field.Name);
                ++substitutions;
            }
            MethodInfo call = instruction.operand as MethodInfo;
            if (instruction.opcode == OpCodes.Call && call != null && call.Name == "PrintMethodIL"
                && call.DeclaringType.FullName == "Commons.Utils.LogUtils")
            {
                instruction.operand = typeof(TransportLineLoopTests).GetMethod("IgnoreDebugPrint"); ++substitutions;
            }
            Emit(il, instruction);
        }
        Check(substitutions == 3, "Actual TLM replay substitutions changed.");
        Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> transform =
            (Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>)replay.CreateDelegate(
                typeof(Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>));
        List<CodeInstruction> original = Read();
        List<CodeInstruction> tlmFirst = new List<CodeInstruction>(transform(Clone(original)));
        Check(tlmFirst.Count < original.Count && Guards(tlmFirst).Count == 4, "TLM rewrite did not shift reviewed IL.");
        Check(AccessTools.Method(typeof(CoreLoopPatch), "FindFallbackIndexes", null, null) == null,
            "Legacy positional/candidate fallback must be removed.");
        Check(AccessTools.Method(typeof(CoreLoopPatch), "HasNarrowingConversionBefore", null, null) == null
            && AccessTools.Method(typeof(CoreLoopPatch), "FindBranchBoundCandidates", null, null) == null,
            "Six-instruction narrowing window and generic branch-candidate lookup must be removed.");
        Verify(tlmFirst);
        List<CodeInstruction> mncFirst = new List<CodeInstruction>(transform(Patch(Clone(original))));
        Check(PatchedCount(mncFirst) == 3 && Guards(mncFirst).Count == 1, "TLM-after-MNC lost bounds or changed citizens.");
        Console.WriteLine("PASS: actual TLM transpiler replay, removed legacy fallback and both MNC/TLM rewrite orders.");
        TestAllTlmOrders(tlm);
    }
    private static void TestAllOriginalTargets()
    {
        int errors = MoreNetworkCapacity.Logging.Errors, methods = 0, replacements = 0, preserved = 0;
        MethodInfo targets = AccessTools.Method(typeof(CoreLoopPatch), "TargetMethods", null, null);
        foreach (MethodBase method in (IEnumerable<MethodBase>)targets.Invoke(null, null))
        {
            DynamicMethod fixture = new DynamicMethod("OriginalLoopLabels", typeof(void), Type.EmptyTypes);
            List<CodeInstruction> input = PatchProcessor.GetOriginalInstructions(method, fixture.GetILGenerator());
            List<CodeInstruction> before = Clone(input);
            MethodInfo occurrences = AccessTools.Method(typeof(CoreLoopPatch), "GetOccurrences", new[] { typeof(MethodBase) }, null);
            List<string[]> expected = (List<string[]>)occurrences.Invoke(null, new object[] { method });
            int previousIndex = -1;
            foreach (string[] occurrence in expected)
            {
                int originalIndex = int.Parse(occurrence[1]);
                Check(originalIndex > previousIndex, "Generated occurrences must follow numeric IL order.");
                previousIndex = originalIndex;
            }
            List<CodeInstruction> output = new List<CodeInstruction>((IEnumerable<CodeInstruction>)Transpiler.Invoke(
                null, new object[] { method, input }));
            Check(output.Count == before.Count, "Original target instruction count changed.");
            for (int i = 0; i < output.Count; ++i)
            {
                Check(output[i].labels.Count == before[i].labels.Count && output[i].blocks.Count == before[i].blocks.Count,
                    "Original target control-flow metadata count changed.");
                for (int j = 0; j < before[i].labels.Count; ++j)
                    Check(output[i].labels[j].Equals(before[i].labels[j]), "Original target label changed.");
                for (int j = 0; j < before[i].blocks.Count; ++j)
                    Check(ReferenceEquals(output[i].blocks[j], before[i].blocks[j]), "Original target EH marker changed.");
            }            for (int i = 0; i < output.Count; ++i)
                if (output[i].opcode != before[i].opcode || !object.Equals(output[i].operand, before[i].operand))
                {
                    ++replacements;
                    FieldInfo field = output[i].operand as FieldInfo;
                    bool matches = false;
                    foreach (string[] occurrence in expected)
                        if (int.Parse(occurrence[1]) == i && object.Equals(before[i].operand, int.Parse(occurrence[3]))
                            && field != null && field.Name == (occurrence[4] == "node" ? "NodeLoopLimit" : "SegmentLoopLimit")) matches = true;
                    Check(output[i].opcode == OpCodes.Ldsfld && field != null
                        && field.DeclaringType == typeof(MoreNetworkCapacity.Capacity)
                        && matches,
                        "Unrelated original target instruction changed: " + method.DeclaringType.FullName + "." + method.Name
                        + " @" + i + " " + before[i].opcode + " " + before[i].operand + " -> "
                        + output[i].opcode + " " + output[i].operand);
                }
            foreach (string[] occurrence in expected)
            {
                int index = int.Parse(occurrence[1]);
                if (before[index].opcode == output[index].opcode && object.Equals(before[index].operand, output[index].operand))
                {
                    Check(method.DeclaringType == typeof(CinematicCameraController)
                        && (method.Name == "GetRandomRoad" && (index == 20 || index == 90)
                            || method.Name == "GetRoadWithName" && index == 40),
                        "Unexpected unpatched reviewed occurrence.");
                    ++preserved;
                }
            }
            ++methods;
        }
        Check(MoreNetworkCapacity.Logging.Errors == errors && methods == 137 && replacements == 163 && preserved == 3,
            "Original game target regression: methods=" + methods + ", replacements=" + replacements);
        Console.WriteLine("PASS: all 137 actual game targets, 163 reviewed bound changes and three explicitly reviewed camera bounds preserved.");
        MethodInfo finder = AccessTools.Method(typeof(CoreLoopPatch), "FindTargetMethod", null, null);
        Check(finder.Invoke(null, new object[] { typeof(BuildingTool), "GetAllZoneBlocks", "100667077",
            "System.UInt16,System.Collections.Generic.HashSet`1<System.UInt32>" }) == null,
            "Wrong closed generic argument resolved to the real target.");
    }
}
