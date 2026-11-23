using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Capacity { internal const int VanillaNodeCapacity = 32768, ExpandedNodeCapacity = 65024; }
    internal static class PatchState { internal static bool ExpansionEnabled = true; }
    internal static class Logging
    { internal static void Info(string s) { Console.WriteLine(s); } internal static void Error(string s) { throw new Exception(s); } }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static Assembly FindLoadedAssembly(string name)
        { foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == name) return a; return null; }
    }
}
internal static class TLMStopStatisticsTests
{
    private sealed class SaveManager : ICities.ISerializableData
    {
        internal readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
        public ICities.IManagers managers { get { return null; } }
        public string[] EnumerateData() { string[] ids = new string[Data.Count]; Data.Keys.CopyTo(ids, 0); return ids; }
        public byte[] LoadData(string id) { byte[] data; return Data.TryGetValue(id, out data) ? data : null; }
        public void SaveData(string id, byte[] data) { Data[id] = data; }
        public void EraseData(string id) { Data.Remove(id); }
        public bool SaveGame(string name) { throw new NotSupportedException(); }
        public bool LoadGame(string name) { throw new NotSupportedException(); }
    }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static string[] Paths;
    private static int Index;
    private static uint ReadIndex(object owner) { return (uint)Index; }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    { foreach (string dir in Paths) { string file = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); } return null; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static object Invoke(MethodInfo method, object owner, params object[] args)
    { try { return method.Invoke(owner, args); } catch (TargetInvocationException e) { throw e.InnerException; } }
    private static long Read(Type type, object manager, string method, ushort id)
    {
        string prefix = method == "GetStopIncome" ? "TotalPrefix" : method == "GetCurrentStopIncome" ? "CurrentPrefix" : "LastPrefix";
        object[] patched = { manager, id, 0L };
        if (!(bool)Invoke(typeof(TLMCompatibility).GetMethod(prefix, All), null, patched)) return (long)patched[2];
        object[] args = { id, 0L }; Invoke(type.GetMethod(method, All), manager, args); return (long)args[1];
    }
    private static void Add(MethodInfo method, object manager, ushort id, long income, Citizen citizen)
    {
        object[] args = { manager, id, income, citizen };
        if ((bool)Invoke(typeof(TLMCompatibility).GetMethod("AddPrefix", All), null, args))
            Invoke(method, manager, id, income, args[3]);
    }
    private static void Invalid(byte[] bytes)
    {
        byte[] before = TLMStopStatistics.Serialize(); bool rejected = false;
        try { TLMStopStatistics.Deserialize(bytes); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && Equal(before, TLMStopStatistics.Serialize()), "Malformed payload accepted or partially committed.");
    }
    private static bool Equal(byte[] a, byte[] b)
    { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false; return true; }
    private static void Emit(ILGenerator il, CodeInstruction code)
    {
        if (code.operand == null) il.Emit(code.opcode);
        else if (code.operand is MethodInfo) il.Emit(code.opcode, (MethodInfo)code.operand);
        else if (code.operand is FieldInfo) il.Emit(code.opcode, (FieldInfo)code.operand);
        else if (code.operand is int) il.Emit(code.opcode, (int)code.operand);
        else throw new Exception("Unexpected slice operand: " + code);
    }
    private static void Run()
    {
        Console.WriteLine("Loading actual TLM statistics methods.");
        Assembly assembly = Assembly.LoadFrom(Path.Combine(Paths[1], "TransportLinesManager.dll"));
        Type type = assembly.GetType("TransportLinesManager.Data.Managers.TLMTransportLineStatusesManager", true);
        object manager = FormatterServices.GetUninitializedObject(type);
        MethodInfo init = type.GetMethod("InitArray", All), add = type.GetMethod("AddToStop", All);
        object[] longs = { null, 32768, type.GetNestedType("StopDataLong", All) };
        object[] ints = { null, 32768, type.GetNestedType("StopDataSmallInt", All) };
        Invoke(init.MakeGenericMethod(typeof(long)), manager, longs);
        Invoke(init.MakeGenericMethod(typeof(int)), manager, ints);
        FieldInfo lf = type.GetField("m_stopDataLong", All), inf = type.GetField("m_stopDataInt", All);
        lf.SetValue(manager, longs[0]); inf.SetValue(manager, ints[0]);
        Citizen citizen = new Citizen();
        bool originalFailed = false;
        try { Invoke(add, manager, (ushort)40000, 0L, citizen); } catch (IndexOutOfRangeException) { originalFailed = true; }
        Check(originalFailed, "Actual original DLL must reproduce the reported high-stop exception, even at zero income.");

        Console.WriteLine("Original high-ID failure reproduced; preparing compatibility hooks.");
        // CitiesHarmony native detours require its Unity/Mono host. Replay the
        // production prefix/original decision in this isolated CLR process.
        Check(TLMCompatibility.Prepare(type).Length == 6, "Actual target preparation failed.");
        // Avoid initializing the Unity simulation singleton in this standalone process.
        FieldInfo index = typeof(TLMCompatibility).GetField("_currentIndex", All);
        index.SetValue(null, Delegate.CreateDelegate(index.FieldType, typeof(TLMStopStatisticsTests).GetMethod("ReadIndex", All)));
        Check(Read(type, manager, "GetCurrentStopIncome", 65023) == 0 && TLMStopStatistics.Serialize().Length == 12,
            "Unseen stop must read zero without allocating a record.");
        foreach (ushort id in new ushort[] { 32767, 32768, 40000, 65023 })
        {
            citizen.m_flags = Citizen.Flags.Tourist | Citizen.Flags.Student;
            Add(add, manager, id, 37L, citizen);
            Check(Read(type, manager, "GetCurrentStopIncome", id) == 37 && Read(type, manager, "GetStopIncome", id) == 37,
                "Actual patched AddToStop/getters disagree at boundary " + id);
        }
        Add(add, manager, (ushort)40000, 0L, new Citizen());
        byte[] saved = TLMStopStatistics.Serialize();
        Check(saved.Length == 12 + 3 * 342, "Only three used high-stop records should exist.");
        using (BinaryReader reader = new BinaryReader(new MemoryStream(saved)))
        {
            reader.BaseStream.Position = 12 + 342 + 2 + 16 * 20;
            Check(reader.ReadInt64() == 37 && reader.ReadInt32() == 2 && reader.ReadInt32() == 1 && reader.ReadInt32() == 1,
                "Real DLL zero-Citizen/zero-income/Tourist/Student semantics changed.");
        }
        Check(object.ReferenceEquals(lf.GetValue(manager), longs[0]) && ((long[][])longs[0]).Length == 32768 * 17
            && object.ReferenceEquals(inf.GetValue(manager), ints[0]) && ((int[][])ints[0]).Length == 32768 * 17,
            "Legacy stop arrays were replaced or resized.");

        MethodInfo step = type.GetMethod("SimulationStepImpl", All);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(step, (ILGenerator)null);
        List<CodeInstruction> rewritten = TLMCompatibility.RewriteCycle(original);
        int changed = 0, replaced = -1;
        for (int i = 0; i < original.Count; ++i)
        {
            Check(original[i].opcode == rewritten[i].opcode && original[i].labels.Count == rewritten[i].labels.Count
                && original[i].blocks.Count == rewritten[i].blocks.Count, "Rollover control flow changed.");
            if (!object.Equals(original[i].operand, rewritten[i].operand)) { ++changed; replaced = i; }
        }
        Check(changed == 1 && object.Equals(original[replaced - 2].operand, lf), "Only actual stop-long rollover may be replaced.");
        List<CodeInstruction> missing = new List<CodeInstruction>(original);
        missing.RemoveAt(replaced); bool rejected = false;
        try { TLMCompatibility.RewriteCycle(missing); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Missing stop rollover anchor accepted.");
        List<CodeInstruction> duplicate = new List<CodeInstruction>(original);
        duplicate.AddRange(original.GetRange(replaced - 2, 3)); rejected = false;
        try { TLMCompatibility.RewriteCycle(duplicate); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Ambiguous stop rollover anchor accepted.");
        // Execute the actual rewritten five-instruction call slice with slot 3,
        // substituting only its singleton receiver with a supplied manager.
        DynamicMethod cycle = new DynamicMethod("ActualTLMStopCycle", typeof(void), new Type[] { typeof(object) }, typeof(TLMStopStatisticsTests), true);
        ILGenerator emit = cycle.GetILGenerator();
        emit.Emit(OpCodes.Ldc_I4_3); emit.Emit(OpCodes.Ldarg_0); emit.Emit(OpCodes.Castclass, type);
        for (int i = replaced - 2; i <= replaced; ++i) Emit(emit, rewritten[i]);
        emit.Emit(OpCodes.Ret);
        ((Action<object>)cycle.CreateDelegate(typeof(Action<object>)))(manager);
        Index = 4;
        foreach (ushort id in new ushort[] { 32767, 32768, 40000, 65023 })
            Check(Read(type, manager, "GetCurrentStopIncome", id) == 0 && Read(type, manager, "GetStopIncome", id) == 37,
                "Cycle lost income or failed to clear current slot: " + id);
        Check(Read(type, manager, "GetLastWeekStopIncome", 40000) == 37, "Last-week high-stop getter uses wrong slot.");
        // Only call high-ID last-week getter: the original low getter needs Unity.
        byte[] rotated = TLMStopStatistics.Serialize();
        TLMStopStatistics.Clear(); TLMStopStatistics.Deserialize(rotated);
        Check(Read(type, manager, "GetStopIncome", 40000) == 37, "First manager attachment discarded restored data.");
        Check(Equal(rotated, TLMStopStatistics.Serialize()), "All slots/passenger columns did not round-trip exactly.");
        TLMStopStatistics.Rotate(3);
        Check(Read(type, manager, "GetStopIncome", 40000) == 0, "History overwrite semantics differ from TLM.");
        TLMStopStatistics.Deserialize(saved);
        Invalid(new byte[0]); Invalid(new byte[12]); Invalid(new byte[saved.Length - 1]);
        byte[] bad = (byte[])saved.Clone(); bad[4] = 2; Invalid(bad);
        bad = (byte[])saved.Clone(); bad[12] = 0; bad[13] = 0; Invalid(bad);
        bad = (byte[])saved.Clone(); bad[12 + 342] = bad[12]; bad[13 + 342] = bad[13]; Invalid(bad);
        TLMStopStatistics.Deserialize(null); Check(TLMStopStatistics.Serialize().Length == 12, "Missing old-save extension was not accepted as empty.");
        TLMStopStatistics.Deserialize(saved); TLMStopStatistics.Add(manager, 40000, -7, false, false);
        Check(TLMStopStatistics.Get(manager, 40000, 16) == 30, "Signed income changed.");
        Check(TLMStopStatistics.Get(new object(), 40000, -1) == 0, "Replacement manager inherited old city data.");
        TLMStopStatistics.Deserialize(saved);
        Invoke(typeof(MoreNetworkCapacity.TLMStopStatisticsReadResetPatch).GetMethod("Prefix", All), null);
        Check(TLMStopStatistics.Serialize().Length == 12, "Incoming city reset failed.");
        Type data = typeof(MoreNetworkCapacity.TLMStopStatisticsData);
        Check(data.IsPublic && !data.IsAbstract && data.GetConstructor(Type.EmptyTypes) != null
            && typeof(ICities.ISerializableDataExtension).IsAssignableFrom(data), "Save extension not discoverable by actual game rules.");
        SaveManager saveManager = new SaveManager();
        MoreNetworkCapacity.TLMStopStatisticsData extension = new MoreNetworkCapacity.TLMStopStatisticsData();
        extension.OnCreated(saveManager); extension.OnLoadData();
        Check(TLMStopStatistics.Serialize().Length == 12, "Old save without MNC statistics did not load empty.");
        for (uint slot = 0; slot < 16; ++slot)
        {
            TLMStopStatistics.Add(manager, 40000, 1 + slot, slot % 2 == 0, slot % 3 == 0);
            TLMStopStatistics.Rotate(slot);
        }
        TLMStopStatistics.Add(manager, 40000, 71, true, true);
        byte[] allSlots = TLMStopStatistics.Serialize();
        extension.OnSaveData();
        Check(saveManager.Data.Count == 1 && Equal(allSlots, saveManager.Data["MoreNetworkCapacity/TLMHighStopStatistics/v1"]),
            "Extension did not save exact overlay or wrote legacy TLM SaveIds.");
        extension.OnReleased(); Check(TLMStopStatistics.Serialize().Length == 12, "Release left old city statistics.");
        extension.OnCreated(saveManager); extension.OnLoadData();
        Check(Read(type, manager, "GetStopIncome", 40000) == 207 && Equal(allSlots, TLMStopStatistics.Serialize()),
            "Extension reload lost populated history/current/passenger slots.");
        for (Index = 0; Index < 16; ++Index)
            Check(Read(type, manager, "GetLastWeekStopIncome", 40000) == ((Index + 15) & 15) + 1,
                "Last-week wraparound differs at index " + Index);
        MoreNetworkCapacity.PatchState.ExpansionEnabled = false;
        extension.OnLoadData(); extension.OnSaveData();
        Check(TLMStopStatistics.Serialize().Length == 12 && Equal(allSlots, saveManager.Data["MoreNetworkCapacity/TLMHighStopStatistics/v1"]),
            "Disabled expansion restored or overwrote statistics.");
        MoreNetworkCapacity.PatchState.ExpansionEnabled = true;
        extension.OnReleased();
        TLMCompatibility.Reset();
        originalFailed = false;
        try { Invoke(add, manager, (ushort)40000, 1L, citizen); } catch (IndexOutOfRangeException) { originalFailed = true; }
        Check(originalFailed, "Original TLM method was unexpectedly changed.");
        Console.WriteLine("PASS: real TLM DLL original error, production prefix/original replay, low/high boundaries, rewritten rollover slice, statistics/save validation/lifecycle. Unity lifecycle and native Harmony installation not executed.");
    }
    public static int Main(string[] args)
    { try { Paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve; Run(); return 0; } catch (Exception e) { Console.Error.WriteLine(e); return 1; } }
}
