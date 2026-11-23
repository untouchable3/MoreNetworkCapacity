using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static partial class TransportLineLoopTests
{
    private delegate void CompleteLineSimulation(ref TransportLine line, ushort lineId);

    // Rehost only the actual TLM transpilers. Supplying these reflection values
    // avoids running TLM's static initializer and its game/UI dependencies.
    public static MethodInfo OrderTargetVehicles, OrderSetActive, OrderNewTargetVehicles;
    public static MethodInfo OrderEconomyFetch, OrderEconomyReplacement, OrderGetBudget;

    public static void IgnoreOrderLog(string message, object[] args) { }

    private static FieldInfo OrderReflectionField(FieldInfo field, Type owner)
    {
        Check(field.DeclaringType == owner && field.IsStatic && field.FieldType == typeof(MethodInfo),
            "Unexpected TLM reflection field shape: " + field);
        string replacement;
        switch (field.Name)
        {
            case "m_targetVehicles": replacement = "OrderTargetVehicles"; break;
            case "m_setActive": replacement = "OrderSetActive"; break;
            case "m_newTargetVehicles": replacement = "OrderNewTargetVehicles"; break;
            case "m_economyManagerCallFetch": replacement = "OrderEconomyFetch"; break;
            case "m_doTransportLineEconomyManagement": replacement = "OrderEconomyReplacement"; break;
            case "m_getBudgetInt": replacement = "OrderGetBudget"; break;
            default: throw new Exception("Unreviewed TLM reflection field: " + field.Name);
        }
        FieldInfo result = typeof(TransportLineLoopTests).GetField(replacement);
        Check(result != null && result.GetValue(null) is MethodInfo,
            "Missing TLM replay reflection value: " + replacement);
        return result;
    }

    private static Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>> ReplayOrderTransform(
        Type type, string name, string[] expectedFields, int expectedLogs)
    {
        MethodInfo source = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(IEnumerable<CodeInstruction>) }, null);
        Check(source != null && source.ReturnType == typeof(IEnumerable<CodeInstruction>)
            && source.GetMethodBody() != null && source.GetMethodBody().ExceptionHandlingClauses.Count == 0,
            "Unreviewed TLM transpiler signature/body: " + name);
        DynamicMethod replay = new DynamicMethod("ReplayOrder_" + name,
            typeof(IEnumerable<CodeInstruction>), new[] { typeof(IEnumerable<CodeInstruction>) },
            typeof(TransportLineLoopTests).Module, true);
        ILGenerator il = replay.GetILGenerator();
        List<CodeInstruction> body = PatchProcessor.GetOriginalInstructions(source, il);
        List<string> fields = new List<string>();
        int prints = 0, logs = 0;
        foreach (CodeInstruction instruction in body)
        {
            FieldInfo field = instruction.operand as FieldInfo;
            if (field != null && field.DeclaringType == type)
            {
                Check(instruction.opcode == OpCodes.Ldsfld,
                    "Unreviewed TLM reflection field operation: " + name);
                fields.Add(field.Name);
                instruction.operand = OrderReflectionField(field, type);
            }
            MethodInfo call = instruction.operand as MethodInfo;
            if (call != null && call.DeclaringType.FullName == "Commons.Utils.LogUtils")
            {
                Check(instruction.opcode == OpCodes.Call && call.ReturnType == typeof(void),
                    "Unreviewed TLM logger call shape: " + name);
                ParameterInfo[] parameters = call.GetParameters();
                if (call.Name == "PrintMethodIL" && parameters.Length == 2
                    && parameters[0].ParameterType == typeof(List<CodeInstruction>)
                    && parameters[1].ParameterType == typeof(bool))
                {
                    instruction.operand = typeof(TransportLineLoopTests).GetMethod("IgnoreDebugPrint");
                    ++prints;
                }
                else if (call.Name == "DoLog" && parameters.Length == 2
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(object[]))
                {
                    instruction.operand = typeof(TransportLineLoopTests).GetMethod("IgnoreOrderLog");
                    ++logs;
                }
                else throw new Exception("Unreviewed TLM logger: " + call);
            }
        }
        // Check every field read, including duplicates, before executing a transform.
        Check(fields.Count == expectedFields.Length && prints == 1 && logs == expectedLogs,
            "TLM replay substitution counts changed: " + name);
        for (int i = 0; i < fields.Count; ++i)
            Check(fields[i] == expectedFields[i], "TLM replay field sequence changed: " + name);
        foreach (CodeInstruction instruction in body) EmitOrderInstruction(il, instruction);
        return (Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>)replay.CreateDelegate(
            typeof(Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>));
    }

    private static void EmitOrderInstruction(ILGenerator il, CodeInstruction instruction)
    {
        Check(instruction.blocks.Count == 0, "Unexpected exception region in TLM order replay.");
        if (instruction.operand is float || instruction.operand is long || instruction.operand is Label[])
        {
            foreach (Label label in instruction.labels) il.MarkLabel(label);
            if (instruction.operand is float) il.Emit(instruction.opcode, (float)instruction.operand);
            else if (instruction.operand is long) il.Emit(instruction.opcode, (long)instruction.operand);
            else il.Emit(instruction.opcode, (Label[])instruction.operand);
        }
        else Emit(il, instruction);
    }

    private static void TestAllTlmOrders(Assembly tlm)
    {
        Check(tlm.GetName().Version.ToString() == "14.7.0.37334", "Unreviewed TLM order-test version.");
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        Type type = tlm.GetType("TransportLinesManager.Overrides.TransportLineOverrides", true);
        OrderTargetVehicles = typeof(TransportLine).GetMethod("CalculateTargetVehicleCount", flags);
        OrderSetActive = typeof(TransportLine).GetMethod("SetActive", flags);
        OrderNewTargetVehicles = type.GetMethod("NewCalculateTargetVehicleCount", flags);
        OrderEconomyFetch = EconomyFetch;
        OrderEconomyReplacement = EconomyReplacement;
        OrderGetBudget = tlm.GetType("TransportLinesManager.Utils.TLMLineUtils", true)
            .GetMethod("GetEffectiveBudgetInt", flags);
        Check(Original.GetMethodBody().ExceptionHandlingClauses.Count == 0,
            "Original line simulation exception layout changed.");
        var transforms = new Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>[]
        {
            ReplayOrderTransform(type, "TranspileSimulationStepLine", new[]
                { "m_getBudgetInt", "m_getBudgetInt", "m_setActive", "m_targetVehicles", "m_newTargetVehicles" }, 0),
            ReplayOrderTransform(type, "TranspileSimulationStepLine_GoingBack", new string[0], 2),
            ReplayOrderTransform(type, "TranspileSimulationStep", new[]
                { "m_economyManagerCallFetch", "m_doTransportLineEconomyManagement" }, 0)
        };
        int[][] orders = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
        int cases = 0;
        foreach (int[] order in orders)
        {
            for (int slot = 0; slot <= 3; ++slot)
            {
                string context = "TLM order " + order[0] + "," + order[1] + "," + order[2]
                    + "; MNC slot " + slot;
                int errors = MoreNetworkCapacity.Logging.Errors;
                DynamicMethod final = new DynamicMethod("CompleteTlmOrder", typeof(void),
                    new[] { typeof(TransportLine).MakeByRefType(), typeof(ushort) },
                    typeof(TransportLineLoopTests).Module, true);
                ILGenerator il = final.GetILGenerator();
                // PatchProcessor declares the original locals on this generator.
                // Declaring them twice would prevent TLM's local-index pattern matching.
                List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(Original, il);
                for (int position = 0; position <= 3; ++position)
                {
                    if (position == slot) code = Patch(code);
                    if (position < 3) code = new List<CodeInstruction>(transforms[order[position]](code));
                }
                Check(MoreNetworkCapacity.Logging.Errors == errors && PatchedCount(code) == 3
                    && Guards(code).Count == 1, context + " lost a reviewed network/citizen bound.");
                int oldTarget = 0, newTarget = 0, oldEconomy = 0, newEconomy = 0, budgets = 0;
                foreach (CodeInstruction instruction in code)
                {
                    if (object.Equals(instruction.operand, OrderTargetVehicles)) ++oldTarget;
                    if (object.Equals(instruction.operand, OrderNewTargetVehicles)) ++newTarget;
                    if (object.Equals(instruction.operand, OrderEconomyFetch)) ++oldEconomy;
                    if (object.Equals(instruction.operand, OrderEconomyReplacement)) ++newEconomy;
                    if (object.Equals(instruction.operand, OrderGetBudget)) ++budgets;
                }
                Check(oldTarget == 0 && newTarget == 1 && oldEconomy == 0 && newEconomy == 1
                    && budgets == 2 && code.Count == 1015, context + ": one of three actual TLM rewrites did not apply.");
                foreach (CodeInstruction instruction in code) EmitOrderInstruction(il, instruction);
                // Verify the complete method can be emitted; never invoke game simulation.
                final.CreateDelegate(typeof(CompleteLineSimulation));
                ++cases;
            }
        }
        Check(cases == 24, "Incomplete MNC/TLM rewrite order coverage.");
        Console.WriteLine("PASS: all three actual TLM transpilers, 24 MNC/TLM orders; complete delegates created, not invoked.");
    }
}
