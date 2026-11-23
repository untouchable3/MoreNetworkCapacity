using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Patches;

// Keep the production Capacity/Metadata/PatchState intact while preventing
// Unity's singleton accessor from creating managers in the test process.
namespace ColossalFramework { public static class Singleton<T> { public static T instance; } }
// Plain managed stand-ins also avoid UnityEngine.Object's native null test.
// The actual shipped loader/data types are inspected independently below.
public sealed class SimulationMetaData { public Dictionary<string, bool> m_modOverride; }
public sealed class SimulationManager
{
    public SimulationMetaData m_metaData;
    public sealed class Data { public void Deserialize() { } }
}
public sealed class NodePool { public byte[] m_buffer; }
public sealed class NetManager { public NodePool m_nodes, m_segments; }
public sealed class ElectricityManager { public ushort[] m_nodeGroups; }
namespace MoreNetworkCapacity
{
    internal static class NetManagerCustomizer
    {
        internal static bool Deferred;
        internal static int Cancels;
        internal static void CancelDeferredLoopLimitActivation() { Deferred = false; ++Cancels; }
    }
    internal static class LoadStageJournal
    {
        internal static int Records;
        internal static void Record(string text) { ++Records; }
    }
}
public static class SimulationDataReadStateTests
{
    private static string[] paths;
    private const string Marker = "User/MoreNetworkCapacity/64K-v1";
    private static readonly MethodInfo Prefix = typeof(SimulationDataReadStatePatch).GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string directory in paths)
        {
            string candidate = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
        }
        return null;
    }
    private static SimulationMetaData MetadataFor(bool expanded)
    {
        SimulationMetaData metadata = new SimulationMetaData();
        metadata.m_modOverride = new Dictionary<string, bool>(); metadata.m_modOverride["unrelated"] = true;
        if (expanded) metadata.m_modOverride[Marker] = true;
        return metadata;
    }
    private static void Call() { Prefix.Invoke(null, null); }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            VerifyActualEntryOrder();
            SimulationManager simulation = new SimulationManager();
            ColossalFramework.Singleton<SimulationManager>.instance = simulation;
            NetManager network = new NetManager();
            network.m_nodes = new NodePool(); network.m_nodes.m_buffer = new byte[65024];
            network.m_segments = new NodePool(); network.m_segments.m_buffer = new byte[65024];
            ColossalFramework.Singleton<NetManager>.instance = network;
            ElectricityManager electricity = new ElectricityManager(); electricity.m_nodeGroups = new ushort[65024];
            ColossalFramework.Singleton<ElectricityManager>.instance = electricity;
            Capacity.NodeLoopLimit = Capacity.SegmentLoopLimit = 65024;
            PatchState.MarkEnabled();
            simulation.m_metaData = MetadataFor(true); Metadata.SetExpandedSave();
            SimulationMetaData previous = simulation.m_metaData;
            Check(PatchState.SaveExpanded && Capacity.GetAuxiliaryNodeStorageSize() == 65024, "Expanded initial state missing");
            SimulationMetaData vanilla = MetadataFor(false); simulation.m_metaData = vanilla;
            Check(!Metadata.IsExpandedSave() && Capacity.GetAuxiliaryNodeStorageSize() == 65024,
                "Fixture failed to reproduce prior-city state overriding incoming vanilla metadata");
            NetManagerDeserializeState.SetLoadingExpanded(true); NetManagerCustomizer.Deferred = true;
            Call();
            Check(!PatchState.SaveExpanded && !NetManagerDeserializeState.LoadingExpanded
                && Capacity.NodeStorageSize == 32768 && !NetManagerCustomizer.Deferred, "Previous-city state survived read entry");
            Check(Object.ReferenceEquals(simulation.m_metaData, vanilla) && vanilla.m_modOverride.Count == 1
                && vanilla.m_modOverride["unrelated"] && previous.m_modOverride[Marker], "Incoming or previous metadata mutated");
            Check(Capacity.GetAuxiliaryNodeStorageSize() == 32768 && Capacity.GetNodeIdValidationLimit() == 32768
                && Capacity.GetElectricityNodeGroupLength() == 32768, "Vanilla auxiliary reads followed stale expanded arrays/flags");
            Check(Capacity.GetNodeReadSize() == 32768 && Capacity.GetSegmentReadSize() == 36864
                && Capacity.GetNodeWriteSize() == 32768 && Capacity.GetSegmentWriteSize() == 36864, "Vanilla read/write layout sizes changed");
            // The existing level-loaded finalizer promotes the converted city
            // for future expanded saves; resetting input state must not stop it.
            Metadata.SetExpandedSave();
            Check(PatchState.SaveExpanded && Capacity.GetNodeWriteSize() == 65024 && Capacity.GetSegmentWriteSize() == 65024,
                "Post-load expanded saving no longer works");
            SimulationMetaData expanded = MetadataFor(true); simulation.m_metaData = expanded;
            NetManagerDeserializeState.SetLoadingExpanded(true); NetManagerCustomizer.Deferred = true;
            Call();
            Check(!PatchState.SaveExpanded && !NetManagerDeserializeState.LoadingExpanded && expanded.m_modOverride[Marker]
                && Object.ReferenceEquals(simulation.m_metaData, expanded) && expanded.m_modOverride.Count == 2, "Expanded incoming marker lost");
            Check(Capacity.GetAuxiliaryNodeStorageSize() == 65024 && Capacity.GetNodeIdValidationLimit() == 65024
                && Capacity.GetElectricityNodeGroupLength() == 65024, "Expanded early auxiliary reads lost incoming marker");
            NetManagerDeserializeState.SetLoadingExpanded(true);
            Check(Capacity.GetNodeReadSize() == 65024 && Capacity.GetSegmentReadSize() == 65024, "Expanded network read layout lost");
            Metadata.SetExpandedSave();
            Check(Capacity.GetNodeWriteSize() == 65024 && Capacity.GetSegmentWriteSize() == 65024, "Expanded post-load writes lost");
            // Disabled patches must not change a currently loaded city's state.
            PatchState.MarkDisabled(); NetManagerCustomizer.Deferred = true;
            int cancels = NetManagerCustomizer.Cancels, records = LoadStageJournal.Records;
            Call();
            Check(PatchState.SaveExpanded && NetManagerDeserializeState.LoadingExpanded && NetManagerCustomizer.Deferred
                && NetManagerCustomizer.Cancels == cancels && LoadStageJournal.Records == records
                && expanded.m_modOverride[Marker], "Inactive prefix modified state");
            // No metadata exists in this isolated bootstrap case. Reset must
            // not create a simulation singleton or mutate an incoming object.
            PatchState.MarkEnabled(); ColossalFramework.Singleton<SimulationManager>.instance = null; Call();
            Check(!PatchState.SaveExpanded && !NetManagerDeserializeState.LoadingExpanded && Capacity.GetAuxiliaryNodeStorageSize() == 32768,
                "Empty bootstrap retained previous input state");
            Console.WriteLine("PASS: actual metadata-before-data entry order; real Capacity/Metadata/PatchState expanded-to-vanilla-to-expanded layouts, preserved markers, disabled no-op and no Unity manager creation.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void VerifyActualEntryOrder()
    {
        Type iterator = null;
        Assembly actual = typeof(LoadingManager).Assembly;
        Type actualSimulation = actual.GetType("SimulationManager", true);
        Type actualData = actual.GetType("SimulationManager+Data", true);
        foreach (Type nested in typeof(LoadingManager).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            if (nested.Name.StartsWith("<LoadSimulationData>", StringComparison.Ordinal)) iterator = nested;
        Check(iterator != null, "Actual simulation data loader missing");
        MethodInfo move = iterator.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(move, (ILGenerator)null);
        int metadata = -1, store = -1, data = -1;
        for (int i = 0; i < code.Count; ++i)
        {
            MethodInfo call = code[i].operand as MethodInfo;
            if (code[i].opcode != OpCodes.Call || call == null || call.Name != "Deserialize" || !call.IsGenericMethod) continue;
            Type value = call.GetGenericArguments()[0];
            if (value.FullName == "SimulationMetaData" && value.Assembly == actual)
            {
                metadata = i;
                FieldInfo field = i + 1 < code.Count ? code[i + 1].operand as FieldInfo : null;
                if (code[i + 1].opcode == OpCodes.Stfld && field != null && field.DeclaringType == actualSimulation
                    && field.Name == "m_metaData") store = i + 1;
            }
            if (value == actualData) data = i;
        }
        Check(metadata >= 0 && store == metadata + 1 && data > store, "Incoming metadata is not assigned before simulation data");
        MethodInfo target = actualData.GetMethod("Deserialize");
        Check(target != null && target.GetParameters().Length == 1 && target.ReturnType == typeof(void)
            && PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null).Count > 0, "Actual patched deserialize entry missing");
    }
}
