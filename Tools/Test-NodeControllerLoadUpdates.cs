using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using MoreNetworkCapacity.Compatibility;

public struct NetNode
{
    [Flags] public enum Flags { None = 0, Created = 1 }
    public Flags m_flags;
}
public sealed class NodeBuffer { public NetNode[] m_buffer = new NetNode[65024]; }
public sealed class NetManager
{
    public NodeBuffer m_nodes = new NodeBuffer();
    public readonly List<ushort> Requested = new List<ushort>();
    public void UpdateNode(ushort nodeId) { Requested.Add(nodeId); }
}
namespace HarmonyLib
{
    public class CodeInstruction { public OpCode opcode; public object operand; }
    public sealed class HarmonyMethod
    {
        public MethodInfo method;
        public HarmonyMethod(MethodInfo value) { method = value; }
    }
    public sealed class Harmony
    {
        public int PatchCalls, InitialPostfixes;
        public void Patch(MethodBase original, HarmonyMethod prefix, HarmonyMethod postfix,
            HarmonyMethod transpiler, HarmonyMethod finalizer)
        {
            ++PatchCalls;
            if (original.Name == "StartInitialUpdate" && postfix != null
                && postfix.method.Name == "InitialUpdatePostfix") ++InitialPostfixes;
        }
    }
}
namespace NodeController
{
    public sealed class Manager
    {
        public object[] Buffer { get; private set; }
        public Manager() { Buffer = new object[32768]; }
        public void Clear() { Buffer = new object[32768]; }
        public void Replace(object[] value) { Buffer = value; }
        public void StartInitialUpdate(ushort[] toUpdateIds, bool initial) { }
        public void FromXml(object xml, object map, bool initial) { }
    }
}
namespace MoreNetworkCapacity.Compatibility
{
    // The real corner flag transpiler has its own actual-DLL/equivalence tests.
    internal static class NodeControllerFlagChecks
    {
        internal static void Reset() { }
        internal static void Apply(HarmonyLib.Harmony harmony, Assembly assembly) { }
    }
}
namespace MoreNetworkCapacity
{
    internal static class Capacity { internal const int ExpandedNodeCapacity = 65024, VanillaNodeCapacity = 32768; }
    internal static class Logging
    {
        public static void Info(string message) { }
        public static void Warning(string message) { }
        public static void Error(string message) { throw new Exception(message); }
    }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static int BufferDiscoveries;
        internal static Assembly FindLoadedAssembly(string name) { return Assembly.GetExecutingAssembly(); }
        internal static Type FindType(Assembly assembly, string name, string modName) { return assembly.GetType(name); }
        internal static FieldInfo FindField(Type type, string name)
        { return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        internal static FieldInfo FindBackingField(Type type, string name)
        { ++BufferDiscoveries; return FindField(type, "<" + name + ">k__BackingField"); }
    }
}
internal static class NodeControllerLoadUpdateTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static NetManager NewManager()
    {
        NetManager manager = new NetManager();
        manager.m_nodes.m_buffer[12].m_flags = NetNode.Flags.Created;
        manager.m_nodes.m_buffer[65023].m_flags = NetNode.Flags.Created;
        return manager;
    }
    private static int Main()
    {
        try
        {
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony();
            NodeController.Manager nc = new NodeController.Manager();
            object record = new object(); nc.Buffer[12] = record;
            NodeControllerCompatibility.EnsureManagerBuffer(nc);
            Check(nc.Buffer.Length == 65024 && object.ReferenceEquals(record, nc.Buffer[12]), "Expanded NC buffer lost records.");
            object[] ready = nc.Buffer;
            int discoveries = ThirdPartyCapacityCompatibility.BufferDiscoveries;
            for (int i = 0; i < 100000; ++i) NodeControllerCompatibility.EnsureManagerBuffer(nc);
            Check(ThirdPartyCapacityCompatibility.BufferDiscoveries == discoveries && object.ReferenceEquals(ready, nc.Buffer),
                "Ready prefixes must perform no metadata rediscovery or buffer replacement.");
            nc.Clear(); NodeControllerCompatibility.EnsureManagerBuffer(nc);
            Check(nc.Buffer.Length == 65024 && !object.ReferenceEquals(ready, nc.Buffer), "Same-manager Clear escaped live-array validation.");
            nc.Replace(null); NodeControllerCompatibility.EnsureManagerBuffer(nc); Check(nc.Buffer == null, "Null buffer must stay untouched.");
            object[] unknown = new object[50000]; nc.Replace(unknown); NodeControllerCompatibility.EnsureManagerBuffer(nc);
            Check(object.ReferenceEquals(unknown, nc.Buffer), "Unknown-length NC buffer must stay untouched.");
            NodeControllerCompatibility.EnsureManagerBuffer(new NodeController.Manager());
            Check(ThirdPartyCapacityCompatibility.BufferDiscoveries == discoveries, "A new manager of the same type should reuse accessor metadata.");
            NodeControllerCompatibility.Reset(); NodeControllerCompatibility.EnsureManagerBuffer(nc);
            Check(ThirdPartyCapacityCompatibility.BufferDiscoveries == discoveries + 1, "Lifecycle reset did not discard accessor metadata.");
            NodeControllerCompatibility.Apply(harmony);
            int patches = harmony.PatchCalls;
            NodeControllerCompatibility.Apply(harmony);
            Check(harmony.InitialPostfixes == 1 && harmony.PatchCalls == patches,
                "Initial-update capture hook must be installed once");

            NetManager manager = NewManager();
            ushort[] initial = { 12, 65023, 12, 0, 65535, 27 };
            NodeControllerPatches.InitialUpdatePostfix(initial, true);
            initial[0] = 27;
            NodeControllerPatches.InitialUpdatePostfix(new ushort[] { 27 }, false);
            int count = NodeControllerLoadUpdates.Replay(manager);
            Check(count == 2 && manager.Requested.Count == 2 && manager.Requested.Contains(12)
                && manager.Requested.Contains(65023),
                "Capture must clone load ids, ignore interactive imports and replay only live unique nodes including high ids");
            Check(NodeControllerLoadUpdates.Replay(manager) == 0 && manager.Requested.Count == 2,
                "An initial-update request must be consumed only once");

            NodeControllerPatches.InitialUpdatePostfix(new ushort[] { 12 }, true);
            NodeControllerPatches.ManagerClearPostfix(new NodeController.Manager());
            Check(NodeControllerLoadUpdates.Replay(manager) == 0, "Manager.Clear must cancel stale load ids");
            NodeControllerPatches.InitialUpdatePostfix(new ushort[] { 12 }, true);
            NodeControllerCompatibility.Reset();
            Check(NodeControllerLoadUpdates.Replay(manager) == 0, "Disable/reset must cancel stale ids");
            NodeControllerPatches.InitialUpdatePostfix(new ushort[] { 12 }, true);
            NodeControllerLoadUpdates.Reset();
            Check(NodeControllerLoadUpdates.Replay(manager) == 0, "Next network read must clear the prior request");
            NodeControllerLoadUpdates.Capture(null, true);
            Check(NodeControllerLoadUpdates.Replay(manager) == 0, "No-data load must be inactive");
            NodeControllerLoadUpdates.Capture(new ushort[] { 12 }, true);
            Check(NodeControllerLoadUpdates.Replay(null) == 0
                && NodeControllerLoadUpdates.Replay(manager) == 0, "Invalid load must not leak into the next replay");
            Console.WriteLine("PASS: 100,000 buffer checks without metadata rediscovery, preserved records, Clear/replacement/null/unknown/reset handling, initial hook cache and one-shot high-ID load replay.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
