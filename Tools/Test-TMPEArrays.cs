using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

// Prevent tests from invoking Unity singleton creation; actual game IL is
// inspected separately. A missing manager must use vanilla limits.
namespace ColossalFramework
{
    public static class Singleton<T>
    {
        public static bool exists = true;
        public static T instance;
    }
}

public sealed class NetworkArrayFixture
{
    public bool[][] parkingAllowed;
    public bool[] defaultVehicleTypeCache;
    public bool[] rules = new bool[65024];
    public void OnBeforeLoadData()
    {
        parkingAllowed = new bool[36864][];
        for (int i = 0; i < parkingAllowed.Length; ++i) parkingAllowed[i] = new bool[] { true, true };
    }
    public int ReadCache(ushort id)
    {
        if (defaultVehicleTypeCache == null) defaultVehicleTypeCache = new bool[36864];
        defaultVehicleTypeCache[id] = true;
        return defaultVehicleTypeCache.Length;
    }
    public bool GetPrioritySign(ushort id) { return rules[id]; }
    public int SaveData(ref bool success)
    {
        int count = 0;
        for (uint id = 0; id < 36864; ++id) if (GetPrioritySign((ushort)id)) ++count;
        return count;
    }
}

public static class TMPEArrayTests
{
    private delegate void ResetFixture(NetworkArrayFixture fixture);
    private delegate int CacheFixture(NetworkArrayFixture fixture, ushort id);
    private delegate int SaveFixture(NetworkArrayFixture fixture, ref bool success);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static List<CodeInstruction> Copy(List<CodeInstruction> input)
    { List<CodeInstruction> output = new List<CodeInstruction>(); foreach (CodeInstruction c in input) output.Add(new CodeInstruction(c)); return output; }
    public static void Run(string[] paths)
    {
        Assembly assembly = Assembly.LoadFrom(Path.Combine(paths[1], "TrafficManager.dll"));
        foreach (TMPENetworkArrayPatches.Target target in TMPENetworkArrayPatches.Targets)
        {
            MethodInfo method = TMPENetworkArrayPatches.Resolve(assembly, target);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            List<CodeInstruction> patched = TMPENetworkArrayPatches.ApplyTarget(method, target, Copy(original));
            int changes = 0;
            for (int i = 0; i < original.Count; ++i)
                if (original[i].opcode != patched[i].opcode)
                { ++changes; Check(patched[i].opcode == OpCodes.Call, "Unexpected instruction change"); }
            Check(changes == 1 && patched.Count == original.Count, "Unexpected TMPE array replacement count");
            List<CodeInstruction> broken = Copy(original);
            foreach (CodeInstruction c in broken) if (NetworkSimulationSlicePatch.Constant(c, target.Limit)) { c.operand = target.Limit - 1; break; }
            List<CodeInstruction> before = Copy(broken); bool rejected = false;
            try { TMPENetworkArrayPatches.ApplyTarget(method, target, broken); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Broken array pattern accepted");
            for (int i = 0; i < before.Count; ++i)
                Check(before[i].opcode == broken[i].opcode && object.Equals(before[i].operand, broken[i].operand), "Mismatch partially mutated input");
            Label label = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            List<CodeInstruction> labelled = Copy(original);
            int constant = labelled.FindIndex(delegate(CodeInstruction c) { return NetworkSimulationSlicePatch.Constant(c, target.Limit); });
            labelled[constant].labels.Add(label);
            patched = TMPENetworkArrayPatches.ApplyTarget(method, target, labelled);
            Check(patched[constant].labels.Contains(label), "Array patch lost incoming label");
            Console.WriteLine("TMPE array: " + target.Type + "." + target.Method + " matched.");
        }
        // Ensure non-network numeric values are not even selected as targets.
        Type restrictions = assembly.GetType("TrafficManager.Manager.Impl.VehicleRestrictionsManager");
        MethodInfo cable = restrictions.GetMethod("IsCableCarAllowed", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Check(cable != null, "Vehicle flag witness missing");
        List<CodeInstruction> flags = PatchProcessor.GetOriginalInstructions(cable, (ILGenerator)null);
        Check(flags.Exists(delegate(CodeInstruction c) { return NetworkSimulationSlicePatch.Constant(c, 32768); }), "Flag witness changed");
        foreach (TMPENetworkArrayPatches.Target target in TMPENetworkArrayPatches.Targets)
            Check(target.Method != cable.Name, "Vehicle flag accidentally selected");

        TMPENetworkArrayPatches.Target parking = new TMPENetworkArrayPatches.Target("fixture", "OnBeforeLoadData", 0, 36864, "parkingAllowed", true);
        TMPENetworkArrayPatches.Target cache = new TMPENetworkArrayPatches.Target("fixture", "ReadCache", 1, 36864, "defaultVehicleTypeCache", true);
        TMPENetworkArrayPatches.Target saveTarget = new TMPENetworkArrayPatches.Target("fixture", "SaveData", 1, 36864, "GetPrioritySign", false);
        ResetFixture reset = (ResetFixture)Compile("OnBeforeLoadData", typeof(ResetFixture), parking);
        CacheFixture read = (CacheFixture)Compile("ReadCache", typeof(CacheFixture), cache);
        SaveFixture save = (SaveFixture)Compile("SaveData", typeof(SaveFixture), saveTarget);
        NetworkArrayFixture fixture = new NetworkArrayFixture();
        foreach (int length in new int[] { 36864, 65024, 36864, 65024 })
        {
            Capacity.SegmentBufferLength = length;
            reset(fixture); Check(fixture.parkingAllowed.Length == length, "Load reset restored small parking array");
            Check(fixture.parkingAllowed[length - 1][0] && fixture.parkingAllowed[length - 1][1], "High parking entries uninitialized");
            fixture.defaultVehicleTypeCache = null;
            Check(read(fixture, (ushort)(length - 1)) == length, "Cache clear/reallocation restored small array");
        }
        fixture.rules[1] = fixture.rules[40000] = fixture.rules[64512] = true;
        bool success = false;
        Check(fixture.SaveData(ref success) == 1, "Fixture did not reproduce high rule omission");
        Check(save(fixture, ref success) == 3 && !success, "High rules skipped or failure result overridden");
        Capacity.SegmentBufferLength = 36864;
        ColossalFramework.Singleton<NetManager>.exists = false;
        Capacity.NodeBufferLength = 65024;
        Capacity.SegmentBufferLength = 65024;
        Check(TMPENetworkArrayPatches.SegmentCount() == 36864 && TMPENetworkArrayPatches.NodeCount() == 32768,
            "Unloading/menu path attempted expanded limits without a manager");
        ColossalFramework.Singleton<NetManager>.exists = true;
        Check(TMPENetworkArrayPatches.NodeCount() == 65024, "High node defaults traversal remains vanilla");
        Capacity.NodeBufferLength = 32768;
        Capacity.SegmentBufferLength = 36864;
        Check(save(fixture, ref success) == 1, "Vanilla rule traversal changed");
        Capacity.SegmentBufferLength = 65536; bool unsupported = false;
        try { TMPENetworkArrayPatches.SegmentCount(); } catch (InvalidOperationException) { unsupported = true; }
        Check(unsupported, "Unsupported ushort-wrap buffer accepted");
        Capacity.SegmentBufferLength = 36864;
        Console.WriteLine("PASS: 16 actual TMPE methods, lifecycle reset/cache recreation, high-ID rule save, vanilla reload, result preservation and flag exclusion.");
    }
    private static Delegate Compile(string name, Type delegateType, TMPENetworkArrayPatches.Target target)
    {
        MethodInfo original = typeof(NetworkArrayFixture).GetMethod(name);
        List<Type> parameters = new List<Type>(); parameters.Add(typeof(NetworkArrayFixture));
        foreach (ParameterInfo p in original.GetParameters()) parameters.Add(p.ParameterType);
        DynamicMethod method = new DynamicMethod(name + "Expanded", original.ReturnType, parameters.ToArray(), typeof(TMPEArrayTests).Module, true);
        ILGenerator generator = method.GetILGenerator();
        foreach (LocalVariableInfo local in original.GetMethodBody().LocalVariables) generator.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = TMPENetworkArrayPatches.ApplyTarget(original, target, PatchProcessor.GetOriginalInstructions(original, generator));
        foreach (CodeInstruction c in code)
        {
            foreach (Label label in c.labels) generator.MarkLabel(label);
            if (c.operand == null) generator.Emit(c.opcode);
            else if (c.operand is Label) generator.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is Label[]) generator.Emit(c.opcode, (Label[])c.operand);
            else if (c.operand is LocalBuilder) generator.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) generator.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) generator.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is FieldInfo) generator.Emit(c.opcode, (FieldInfo)c.operand);
            else if (c.operand is Type) generator.Emit(c.opcode, (Type)c.operand);
            else if (c.operand is byte) generator.Emit(c.opcode, (byte)c.operand);
            else if (c.operand is sbyte) generator.Emit(c.opcode, (sbyte)c.operand);
            else if (c.operand is short) generator.Emit(c.opcode, (short)c.operand);
            else if (c.operand is int) generator.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected fixture operand " + c.operand);
        }
        return method.CreateDelegate(delegateType);
    }
}
