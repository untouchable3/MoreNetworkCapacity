using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity;
using MoreNetworkCapacity.Compatibility;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Logging { internal static void Info(string text) { } }
    internal static class Capacity
    {
        internal const int ExpandedNodeCapacity = 65024, ExpandedSegmentCapacity = 65024;
        internal static int NodeLoopLimit = 32768, SegmentLoopLimit = 36864;
        internal static int GetNodeBufferLength() { return NodeLoopLimit; }
        internal static int GetSegmentBufferLength() { return SegmentLoopLimit; }
    }
}
namespace ColossalFramework { public static class Singleton<T> { public static bool exists = true; } }
namespace TrafficManager.Util.Caching
{
    // Same append-only semantics as actual DLL. Its own IL is inspected too.
    public sealed class GenericArrayCache<T>
    {
        public T[] Values; public int Size;
        public GenericArrayCache(int capacity) { Values = new T[capacity]; }
        public void Clear() { Size = 0; }
        public void Add(T value) { Values[Size++] = value; }
    }
}
public sealed class OverlayFixture
{
    public int visits, last;
    public TrafficManager.Util.Caching.GenericArrayCache<ushort> cache;
    public void Seen(ushort id) { ++visits; last = id; }
    public void Nodes() { for (uint id = 1; id < 32768; ++id) Seen((ushort)id); }
    public void Segments() { for (uint id = 1; id < 36864; ++id) Seen((ushort)id); }
    public void NodeRefresh(ushort id) { for (uint i = id != 0 ? id : (uint)1; i <= (id != 0 ? id : 32767); ++i) Seen((ushort)i); }
    public void SegmentRefresh(ushort id) { for (ushort i = id != 0 ? id : (ushort)1; i <= (id != 0 ? id : 36863); ++i) Seen(i); }
    public void CreateCache() { cache = new TrafficManager.Util.Caching.GenericArrayCache<ushort>(32768); }
}
public static class TMPEOverlayTests
{
    private delegate void Run(OverlayFixture fixture);
    private delegate void Refresh(OverlayFixture fixture, ushort id);
    private static string[] paths;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string path in paths)
        {
            string file = Path.Combine(path, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }
    private static List<CodeInstruction> Copy(List<CodeInstruction> code)
    { List<CodeInstruction> result = new List<CodeInstruction>(); foreach (CodeInstruction c in code) result.Add(new CodeInstruction(c)); return result; }
    public static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Assembly tmpe = Assembly.LoadFrom(Path.Combine(paths[1], "TrafficManager.dll"));
            foreach (TMPEOverlayRanges.Target target in TMPEOverlayRanges.Targets)
            {
                MethodBase method = TMPEOverlayRanges.Resolve(tmpe, target);
                List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
                List<CodeInstruction> patched = TMPEOverlayRanges.ApplyTarget(method, target, Copy(original));
                int changes = 0, constant = -1;
                for (int i = 0; i < original.Count; ++i)
                {
                    if (NetworkSimulationSlicePatch.Constant(original[i], target.Limit)) constant = i;
                    if (original[i].opcode != patched[i].opcode) ++changes;
                }
                Check(changes == 1 && patched.Count == original.Count && patched[constant].opcode == OpCodes.Call, "Unexpected patch size");
                List<CodeInstruction> broken = Copy(original); broken[constant].operand = target.Limit - 2;
                Reject(method, target, broken);
                // Anchor mismatch must reject unchanged input, even with the
                // same numeric bound still present.
                TMPEOverlayRanges.Target wrong = new TMPEOverlayRanges.Target(target.Type, target.Method, target.Parameters,
                    target.Limit, "missing_anchor", target.Pattern);
                Reject(method, wrong, Copy(original));
                Label incoming = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
                List<CodeInstruction> labelled = Copy(original); labelled[constant].labels.Add(incoming);
                patched = TMPEOverlayRanges.ApplyTarget(method, target, labelled);
                Check(patched[constant].labels.Contains(incoming), "Incoming label lost");
                Console.WriteLine("Matched: " + target.Type + "." + target.Method);
            }
            Run nodes = (Run)Compile("Nodes", 32768, "Seen", TMPEOverlayRanges.Shape.Loop, typeof(Run));
            Run segments = (Run)Compile("Segments", 36864, "Seen", TMPEOverlayRanges.Shape.Loop, typeof(Run));
            Refresh nodeRefresh = (Refresh)Compile("NodeRefresh", 32767, "Seen", TMPEOverlayRanges.Shape.InclusiveRefresh, typeof(Refresh));
            Refresh segmentRefresh = (Refresh)Compile("SegmentRefresh", 36863, "Seen", TMPEOverlayRanges.Shape.InclusiveRefresh, typeof(Refresh));
            Run cache = (Run)Compile("CreateCache", 32768, "cache", TMPEOverlayRanges.Shape.CacheConstructor, typeof(Run));
            OverlayFixture f = new OverlayFixture();
            foreach (bool expanded in new bool[] { false, true, false, true })
            {
                Capacity.NodeLoopLimit = expanded ? 65024 : 32768; Capacity.SegmentLoopLimit = expanded ? 65024 : 36864;
                f.visits = 0; nodes(f); Check(f.visits == Capacity.NodeLoopLimit - 1 && f.last == Capacity.NodeLoopLimit - 1, "Node loop coverage");
                f.visits = 0; segments(f); Check(f.visits == Capacity.SegmentLoopLimit - 1 && f.last == Capacity.SegmentLoopLimit - 1, "Segment loop coverage");
                f.visits = 0; nodeRefresh(f, 0); Check(f.visits == Capacity.NodeLoopLimit - 1 && f.last == Capacity.NodeLoopLimit - 1, "Full node refresh");
                f.visits = 0; segmentRefresh(f, 0); Check(f.visits == Capacity.SegmentLoopLimit - 1 && f.last == Capacity.SegmentLoopLimit - 1, "Full segment refresh");
                foreach (ushort id in new ushort[] { 1, 32768, 65023 })
                {
                    f.visits = 0; nodeRefresh(f, id); Check(f.visits == 1 && f.last == id, "Targeted node refresh changed");
                    f.visits = 0; segmentRefresh(f, id); Check(f.visits == 1 && f.last == id, "Targeted segment refresh changed");
                }
            }
            // Constructor provision must cover expansion AFTER tools are built.
            Capacity.NodeLoopLimit = 32768; cache(f); Check(f.cache.Values.Length == 65024, "Early cache too small");
            for (int i = 0; i < 65024; ++i) f.cache.Add((ushort)i);
            Check(f.cache.Size == 65024 && f.cache.Values[65023] == 65023, "Cache maximum overflow");
            f.cache.Clear(); f.cache.Add(65023); Check(f.cache.Size == 1 && f.cache.Values[0] == 65023, "Cache reuse broken");
            ColossalFramework.Singleton<NetManager>.exists = false;
            Capacity.NodeLoopLimit = Capacity.SegmentLoopLimit = 65024;
            Check(TMPEOverlayRanges.NodeMaximum() == 32767 && TMPEOverlayRanges.SegmentMaximum() == 36863, "Absent manager fallback changed");
            Console.WriteLine("PASS: 13 real TMPE overlay/constructor patterns, rejection without mutation, vanilla/expanded/full/targeted refresh, final high ID and cache lifecycle.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Reject(MethodBase method, TMPEOverlayRanges.Target target, List<CodeInstruction> broken)
    {
        List<CodeInstruction> before = Copy(broken); bool rejected = false;
        try { TMPEOverlayRanges.ApplyTarget(method, target, broken); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed pattern accepted");
        for (int i = 0; i < broken.Count; ++i) Check(before[i].opcode == broken[i].opcode && object.Equals(before[i].operand, broken[i].operand), "Partial mutation on rejection");
    }
    private static Delegate Compile(string name, int limit, string anchor, TMPEOverlayRanges.Shape shape, Type delegateType)
    {
        MethodInfo original = typeof(OverlayFixture).GetMethod(name);
        List<Type> parameters = new List<Type>(); parameters.Add(typeof(OverlayFixture));
        foreach (ParameterInfo p in original.GetParameters()) parameters.Add(p.ParameterType);
        DynamicMethod method = new DynamicMethod(name + "Expanded", typeof(void), parameters.ToArray(), typeof(TMPEOverlayTests).Module, true);
        ILGenerator generator = method.GetILGenerator();
        foreach (LocalVariableInfo local in original.GetMethodBody().LocalVariables) generator.DeclareLocal(local.LocalType);
        TMPEOverlayRanges.Target target = new TMPEOverlayRanges.Target("fixture", name, original.GetParameters().Length, limit, anchor, shape);
        // The installed TMPE compiler emits the argument-first ternary branch;
        // framework 3.5 csc emits the equivalent bound-first branch. Execute a
        // small fixture with the installed branch layout, already checked on
        // all four real method bodies above, without weakening production checks.
        List<CodeInstruction> input = shape == TMPEOverlayRanges.Shape.InclusiveRefresh
            ? InclusiveFixture(generator, limit, name == "NodeRefresh")
            : PatchProcessor.GetOriginalInstructions(original, generator);
        List<CodeInstruction> code = TMPEOverlayRanges.ApplyTarget(original, target, input);
        foreach (CodeInstruction c in code)
        {
            foreach (Label label in c.labels) generator.MarkLabel(label);
            if (c.operand == null) generator.Emit(c.opcode);
            else if (c.operand is Label) generator.Emit(c.opcode, (Label)c.operand);
            else if (c.operand is LocalBuilder) generator.Emit(c.opcode, (LocalBuilder)c.operand);
            else if (c.operand is LocalVariableInfo) generator.Emit(c.opcode, (short)((LocalVariableInfo)c.operand).LocalIndex);
            else if (c.operand is MethodInfo) generator.Emit(c.opcode, (MethodInfo)c.operand);
            else if (c.operand is ConstructorInfo) generator.Emit(c.opcode, (ConstructorInfo)c.operand);
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
    private static List<CodeInstruction> InclusiveFixture(ILGenerator il, int limit, bool wide)
    {
        Label init = il.DefineLabel(), initialStore = il.DefineLabel(), loop = il.DefineLabel(), test = il.DefineLabel();
        Label bound = il.DefineLabel(), common = il.DefineLabel();
        List<CodeInstruction> c = new List<CodeInstruction>();
        c.Add(new CodeInstruction(OpCodes.Ldarg_1, null)); c.Add(new CodeInstruction(OpCodes.Brfalse, init));
        c.Add(new CodeInstruction(OpCodes.Ldarg_1, null)); c.Add(new CodeInstruction(OpCodes.Br, initialStore));
        CodeInstruction one = new CodeInstruction(OpCodes.Ldc_I4_1, null); one.labels.Add(init); c.Add(one);
        CodeInstruction store = new CodeInstruction(OpCodes.Stloc_0, null); store.labels.Add(initialStore); c.Add(store);
        c.Add(new CodeInstruction(OpCodes.Br, test));
        CodeInstruction self = new CodeInstruction(OpCodes.Ldarg_0, null); self.labels.Add(loop); c.Add(self);
        c.Add(new CodeInstruction(OpCodes.Ldloc_0, null)); c.Add(new CodeInstruction(OpCodes.Conv_U2, null));
        c.Add(new CodeInstruction(OpCodes.Call, typeof(OverlayFixture).GetMethod("Seen")));
        c.Add(new CodeInstruction(OpCodes.Ldloc_0, null)); c.Add(new CodeInstruction(OpCodes.Ldc_I4_1, null)); c.Add(new CodeInstruction(OpCodes.Add, null));
        if (!wide) c.Add(new CodeInstruction(OpCodes.Conv_U2, null));
        c.Add(new CodeInstruction(OpCodes.Stloc_0, null));
        CodeInstruction load = new CodeInstruction(OpCodes.Ldloc_0, null); load.labels.Add(test); c.Add(load);
        if (wide) c.Add(new CodeInstruction(OpCodes.Conv_U8, null));
        c.Add(new CodeInstruction(OpCodes.Ldarg_1, null)); c.Add(new CodeInstruction(OpCodes.Brfalse, bound));
        c.Add(new CodeInstruction(OpCodes.Ldarg_1, null)); c.Add(new CodeInstruction(OpCodes.Br, common));
        CodeInstruction maximum = new CodeInstruction(OpCodes.Ldc_I4, limit); maximum.labels.Add(bound); c.Add(maximum);
        CodeInstruction compare = new CodeInstruction(wide ? OpCodes.Conv_I8 : OpCodes.Ble, wide ? null : (object)loop);
        compare.labels.Add(common); c.Add(compare);
        if (wide) c.Add(new CodeInstruction(OpCodes.Ble, loop));
        c.Add(new CodeInstruction(OpCodes.Ret, null));
        return c;
    }
}
