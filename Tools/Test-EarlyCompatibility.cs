using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal const int VanillaNodeCapacity = 32768, ExpandedNodeCapacity = 65024, ExpandedSegmentCapacity = 65024;
        internal static int NodeLoopLimit = 32768, SegmentLoopLimit = 36864;
        internal static int GetElectricityNodeGroupLength() { return 32768; }
        internal static int GetNodeIdValidationLimit() { return 65024; }
    }
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { ++Errors; }
    }
    internal static class HarmonyBootstrap { internal static Harmony HarmonyInstance { get { return null; } } }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static Assembly FindLoadedAssembly(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == name) return assembly;
            return null;
        }
        internal static Type FindType(Assembly assembly, string name, string label) { return assembly.GetType(name, false); }
        internal static FieldInfo FindField(Type type, string name) { return ArrayCompatibility.FindField(type, name); }
    }
}

internal static class EarlyCompatibilityTests
{
    private static string[] paths;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in paths)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    private static void TestTiles(Assembly assembly)
    {
        foreach (string label in new[] { "electricity", "water" })
        {
            Type owner = assembly.GetType("EightyOne2.Patches." + (label == "electricity" ? "Electricity" : "Water") + "ManagerDataPatches");
            foreach (string operation in new[] { "CustomSerialize", "CustomDeserialize" })
            {
                MethodInfo actual = EightyOne2Compatibility.ResolveSerializationTarget(owner, operation, label);
                Check(actual == owner.GetMethod(operation, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                    "Required actual serialization entry point not resolved.");
                Check((actual.ReturnType == typeof(void)) == (operation == "CustomDeserialize"),
                    "Serialize must return cells; only Deserialize is void.");
            }
            bool wrongOwner = false;
            try { EightyOne2Compatibility.ResolveSerializationTarget(owner, "CustomSerialize", label == "electricity" ? "water" : "electricity"); }
            catch (InvalidOperationException) { wrongOwner = true; }
            Check(wrongOwner, "Wrong manager signature accepted.");
        }
        string[] types = { "ExpandedElectricityManager", "ExpandedElectricityManager", "ExpandedWaterManager", "ExpandedWaterManager", "ExpandedWaterManager" };
        string[] names = { "s_pulseGroups", "s_pulseUnits", "s_waterPulseUnits", "s_sewagePulseUnits", "s_heatingPulseUnits" };
        FieldInfo[] fields = new FieldInfo[names.Length]; Array[] old = new Array[names.Length];
        for (int i = 0; i < names.Length; ++i)
        {
            fields[i] = assembly.GetType("EightyOne2.Patches." + types[i]).GetField(names[i], BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            old[i] = Array.CreateInstance(fields[i].FieldType.GetElementType(), 32768); fields[i].SetValue(null, old[i]);
        }
        EightyOne2Compatibility.Reset(); EightyOne2Compatibility.ApplyEarly(null);
        for (int i = 0; i < names.Length; ++i) Check(object.ReferenceEquals(old[i], fields[i].GetValue(null)), "Early pass replaced pulse arrays.");
        Type data = assembly.GetType("EightyOne2.Serialization.ElectricityDataContainer");
        Type water = assembly.GetType("EightyOne2.Serialization.WaterDataContainer");
        HashSet<string> cache = (HashSet<string>)typeof(EightyOne2Compatibility).GetField("PatchedMethods", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        string cached = data.FullName + "|Serialize|container";
        cache.Add(cached);
        typeof(EightyOne2Compatibility).GetMethod("PatchDataContainer", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { new Harmony("test.early.compatibility.cache"), "Serialize", "DataContainerSerializeTranspiler", 1 });
        Check(cache.Contains(cached), "Repeated installation discarded a successful patch marker.");
        cache.Remove(cached);
        foreach (string name in new string[] { "Serialize", "Deserialize" })
        {
            MethodInfo method = data.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            List<CodeInstruction> patched = new List<CodeInstruction>(name == "Serialize"
                ? EightyOne2Compatibility.DataContainerSerializeTranspiler(original)
                : EightyOne2Compatibility.DataContainerDeserializeTranspiler(original));
            int calls = 0; foreach (CodeInstruction c in patched) if (c.operand is MethodInfo && ((MethodInfo)c.operand).Name == "GetElectricityNodeGroupLength") ++calls;
            Check(calls == (name == "Serialize" ? 1 : 2), "Electricity data length replacements missing.");
        }
        foreach (string name in new string[] { "ReadPulseGroups", "ReadPulseUnits" })
        {
            MethodInfo method = water.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            List<CodeInstruction> patched = new List<CodeInstruction>(EightyOne2Compatibility.NodeLimitValidationTranspiler(method, original));
            int calls = 0; foreach (CodeInstruction c in patched) if (c.operand is MethodInfo && ((MethodInfo)c.operand).Name == "GetNodeIdValidationLimit") ++calls;
            Check(calls == 1, "Water high-node validation missing.");
        }
        EightyOne2Compatibility.CustomDeserializePrefix(new object[] { null, null, new int[65024], new int[65024] });
        EightyOne2Compatibility.WaterCustomDeserializePrefix(new object[] { null, null, new int[65024], new int[65024], new int[65024] });
        for (int i = 0; i < names.Length; ++i) Check(((Array)fields[i].GetValue(null)).Length == 65024, "Deserialize prefix did not size actual pulse array.");
        bool rejected = false;
        try { new List<CodeInstruction>(EightyOne2Compatibility.DataContainerSerializeTranspiler(new CodeInstruction[0])); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed electricity reader/writer accepted.");
        Console.WriteLine("PASS: actual 81 Tiles serialization/validation IL, metadata-only early pass, lazy deserialize array expansion.");
    }
    private static void TestZoning(Assembly assembly)
    {
        Type type = assembly.GetType("ZoningAdjuster.SegmentData");
        FieldInfo flags = type.GetField("_zoneBlockFlags", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodBase[] methods = ZoningAdjusterCompatibility.ValidateTargets(type, flags);
        object instance = FormatterServices.GetUninitializedObject(type);
        type.GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, instance);
        byte[] previous = new byte[49152]; previous[40000] = 9; flags.SetValue(instance, previous);
        ZoningAdjusterCompatibility.Reset(); ZoningAdjusterCompatibility.ApplyEarly(null);
        Check(((byte[])flags.GetValue(instance)).Length == 65024 && ((byte[])flags.GetValue(instance))[40000] == 9, "Existing pre-load ZA instance not expanded/preserved.");
        byte[] saved = new byte[65024]; saved[49151] = 1; saved[49152] = 2; saved[64512] = 3;
        // Execute the copy portion from the actual ReadData IL, stopping
        // before the third-party logging call (no Unity runtime required).
        MethodInfo read = (MethodInfo)methods[1];
        DynamicMethod copy = new DynamicMethod("ZoningReadCopy", typeof(void), new Type[] { type, typeof(byte[]) }, typeof(EarlyCompatibilityTests).Module, true);
        ILGenerator il = copy.GetILGenerator();
        foreach (LocalVariableInfo local in read.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(read, il);
        for (int i = 0; i < 26; ++i)
        {
            CodeInstruction c = code[i]; foreach (Label label in c.labels) il.MarkLabel(label);
            if (c.operand == null) il.Emit(c.opcode);
            else if (c.operand is FieldInfo) il.Emit(c.opcode, (FieldInfo)c.operand);
            else if (c.operand is MethodInfo) il.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is Label) il.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is int) il.Emit(c.opcode, (int)c.operand);
            else throw new Exception("Unexpected ZA copy instruction.");
        }
        il.Emit(OpCodes.Ret);
        Delegate run = copy.CreateDelegate(typeof(Action<,>).MakeGenericType(type, typeof(byte[])));
        flags.SetValue(instance, new byte[49152]); run.DynamicInvoke(instance, saved);
        Check(((byte[])flags.GetValue(instance))[49151] == 1 && ((byte[])flags.GetValue(instance)).Length == 49152, "Original ZA truncation did not reproduce.");
        ZoningAdjusterCompatibility.ReadDataPrefix(instance); run.DynamicInvoke(instance, saved);
        Check(((byte[])flags.GetValue(instance))[49152] == 2 && ((byte[])flags.GetValue(instance))[64512] == 3, "High segment flags lost with load prefix.");
        byte[] expanded = (byte[])flags.GetValue(instance); ZoningAdjusterCompatibility.ReadDataPrefix(instance);
        Check(object.ReferenceEquals(expanded, flags.GetValue(instance)), "Second pass replaced expanded flags.");
        byte[] unknown = new byte[50000]; flags.SetValue(instance, unknown); int errors = Logging.Errors;
        ZoningAdjusterCompatibility.ReadDataPrefix(instance);
        Check(object.ReferenceEquals(unknown, flags.GetValue(instance)) && Logging.Errors == errors + 1, "Unknown layout mutated or not reported.");
        Console.WriteLine("PASS: actual ZA ctor/read preflight and executed copy IL reproduce truncation; pre-read expansion preserves high-ID data and rejects unknown arrays.");
    }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            TestTiles(Assembly.LoadFrom(Path.Combine(paths[1], "EightyOne2.dll")));
            TestZoning(Assembly.LoadFrom(Path.Combine(paths[2], "ZoningAdjuster.dll")));
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
