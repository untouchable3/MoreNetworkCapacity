using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class TransportLineMaintenanceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static List<CodeInstruction> Read(MethodInfo method, out ILGenerator generator)
    {
        DynamicMethod labels = new DynamicMethod("MaintenanceLabels", typeof(void), Type.EmptyTypes);
        generator = labels.GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method, generator);
    }

    private static void Noise(List<CodeInstruction> code, ILGenerator generator)
    {
        Label end = generator.DefineLabel();
        CodeInstruction nop = new CodeInstruction(OpCodes.Nop, null);
        nop.labels.Add(end);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldc_I4_0, null),
            new CodeInstruction(OpCodes.Ldc_I4, 32768), new CodeInstruction(OpCodes.Blt, end), nop });
    }

    private static void Resolve(MethodInfo method, List<CodeInstruction> code,
        CodeInstruction expected, bool accept)
    {
        List<CodeInstruction> before = new List<CodeInstruction>();
        foreach (CodeInstruction instruction in code) before.Add(new CodeInstruction(instruction));
        int[] found = TransportLineMaintenanceBounds.Resolve(method, code);
        Check(accept ? found != null && found.Length == 1 && ReferenceEquals(code[found[0]], expected)
            : found == null, "Unexpected maintenance selection: " + method.Name);
        Check(code.Count == before.Count, "Resolver changed instruction count.");
        for (int i = 0; i < code.Count; ++i)
        {
            Check(code[i].opcode == before[i].opcode && object.Equals(code[i].operand, before[i].operand)
                && code[i].labels.Count == before[i].labels.Count && code[i].blocks.Count == before[i].blocks.Count,
                "Resolver mutated input: " + method.Name);
            for (int j = 0; j < before[i].labels.Count; ++j)
                Check(code[i].labels[j].Equals(before[i].labels[j]), "Resolver changed label.");
            for (int j = 0; j < before[i].blocks.Count; ++j)
                Check(ReferenceEquals(code[i].blocks[j], before[i].blocks[j]), "Resolver changed EH metadata.");
        }
    }

    private static void Test(string name, Type[] signature, int selected)
    {
        MethodInfo method = AccessTools.Method(typeof(TransportLine), name, signature, null);
        Check(method != null && TransportLineMaintenanceBounds.Handles(method), "Target identity rejected: " + name);
        ILGenerator labels;
        List<CodeInstruction> code;
        for (int variant = 0; variant < 5; ++variant)
        {
            code = Read(method, out labels);
            CodeInstruction target = code[selected];
            Check(object.Equals(target.operand, 32768), "Original bound changed: " + name);
            if (variant == 1) code.Insert(0, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 2)
                for (int i = code.Count - 1; i >= 0; --i) code.Insert(i, new CodeInstruction(OpCodes.Nop, null));
            if (variant == 3) Noise(code, labels);
            if (variant == 4)
            {
                target.labels.Add(labels.DefineLabel());
                target.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
            }
            Resolve(method, code, target, true);
        }
        code = Read(method, out labels);
        code[selected].operand = 32767; Noise(code, labels);
        Resolve(method, code, null, false); // Equal count of literals does not prove traversal.
        code = Read(method, out labels);
        code[selected + 1].opcode = OpCodes.Bge;
        Resolve(method, code, null, false);
        code = Read(method, out labels);
        code.Insert(selected, new CodeInstruction(OpCodes.Conv_U2, null));
        Resolve(method, code, null, false);
        code = Read(method, out labels);
        code[0].labels.Add((Label)code[selected + 1].operand);
        Resolve(method, code, null, false);
        code = Read(method, out labels);
        bool traversalChanged = false;
        for (int i = 0; i < selected; ++i)
        {
            MethodInfo call = code[i].operand as MethodInfo;
            FieldInfo field = code[i].operand as FieldInfo;
            if (name == "AddVehicle" && call != null && call.DeclaringType == typeof(TransportLine)
                && call.Name == "GetNextStop")
            {
                code[i].operand = AccessTools.Method(typeof(TransportLine), "GetPrevStop", new[] { typeof(ushort) }, null);
                traversalChanged = true;
            }
            if (name != "AddVehicle" && field != null && field.DeclaringType == typeof(NetSegment)
                && field.Name == "m_endNode")
            {
                code[i].operand = AccessTools.Field(typeof(NetSegment), "m_startNode"); traversalChanged = true;
            }
        }
        Check(traversalChanged, "Traversal negative not constructed: " + name);
        Resolve(method, code, null, false);
        code = Read(method, out labels);
        Label conditionLabel = (Label)code[selected + 1].operand;
        int condition = code.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains(conditionLabel); });
        Check(condition >= 0 && code[condition + 1].opcode == OpCodes.Brtrue, "Predicate fixture changed.");
        code[condition + 1].opcode = OpCodes.Brfalse;
        Resolve(method, code, null, false);
        code = Read(method, out labels);
        for (int i = 0; i < code.Count; ++i)
            if (code[i].opcode == OpCodes.Ldstr && object.Equals(code[i].operand, "Invalid list detected!\n"))
                code[i].operand = "Different diagnostic";
        Resolve(method, code, null, false);
        Console.WriteLine("PASS: " + name + " original/NOP/noise, traversal/predicate/counter rejection and immutable resolution.");
    }

    public static int Main()
    {
        try
        {
            Test("AddVehicle", new[] { typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(bool) }, 64);
            Test("GetPullValue", new[] { typeof(ushort) }, 82);
            Test("GetStopProgress", new[] { typeof(ushort), typeof(float).MakeByRefType(),
                typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, 96);
            foreach (string name in new[] { "ClearStops", "CloneLine", "GetLastStop", "ReleaseNode" })
                Check(!TransportLineMaintenanceBounds.Handles(AccessTools.Method(typeof(TransportLine), name, null, null)),
                    "Unreviewed maintenance shape claimed: " + name);
            Check(!TransportLineMaintenanceBounds.Handles(null), "Null target claimed.");
            Check(!TransportLineMaintenanceBounds.Handles(AccessTools.Method(typeof(TransportLine), "UpdatePaths", null, null)),
                "Other owner's target claimed.");
            Console.WriteLine("PASS: exact target ownership, four deferred shapes remain outside helper.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
