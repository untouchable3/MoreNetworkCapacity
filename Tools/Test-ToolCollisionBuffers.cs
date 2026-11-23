using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal const int ExpandedSegmentCapacity = 65024, ExpandedNodeCapacity = 65024, VanillaNodeCapacity = 32768;
        internal static int NodeLoopLimit = 65024;
    }
    internal static class Logging
    {
        internal static void Info(string message) { }
        internal static void Warning(string message) { }
        internal static void Error(string message) { throw new Exception(message); }
    }
}

internal static class ToolCollisionBufferTests
{
    private static string[] paths;
    private static NetManager network;
    private static BuildingManager buildings;
    private delegate void Ignore(ushort building, ulong[] segments, ulong[] buildingBits);
    public static NetManager Network() { return network; }
    public static BuildingManager Buildings() { return buildings; }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static FieldInfo Field(string name) { return AccessTools.Field(typeof(ToolController), name); }
    private static ToolController Controller(int words)
    {
        ToolController tool = (ToolController)FormatterServices.GetUninitializedObject(typeof(ToolController));
        Field("m_collidingSegments1").SetValue(tool, new ulong[words]);
        Field("m_collidingSegments2").SetValue(tool, new ulong[words]);
        Field("m_collidingBuildings1").SetValue(tool, new ulong[768]);
        Field("m_collidingBuildings2").SetValue(tool, new ulong[768]);
        return tool;
    }
    private static ulong[] Bits(ToolController tool, string name) { return (ulong[])Field(name).GetValue(tool); }
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    {
        List<CodeInstruction> copy = new List<CodeInstruction>();
        foreach (CodeInstruction c in code) copy.Add(new CodeInstruction(c));
        return copy;
    }
    private static void TestAwake()
    {
        MethodInfo awake = AccessTools.Method(typeof(ToolController), "Awake", null, null);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(awake, (ILGenerator)null);
        List<CodeInstruction> changed = Clone(original);
        int second = -1;
        for (int i = 0; i + 2 < changed.Count; ++i)
            if (Field("m_collidingSegments2").Equals(changed[i + 2].operand)) { second = i; break; }
        Check(second >= 0, "Actual second allocation missing.");
        changed[second].operand = 575;
        bool rejected = false;
        try { ToolCollisionBuffers.RewriteAwake(changed); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Changed bitmap layout accepted.");
        for (int i = 0; i + 2 < changed.Count; ++i)
            if (Field("m_collidingSegments1").Equals(changed[i + 2].operand))
                Check((int)changed[i].operand == 576, "Failure partially changed first allocation.");
        List<CodeInstruction> code = Clone(original);
        DynamicMethod labels = new DynamicMethod("labels", typeof(void), Type.EmptyTypes);
        code[second].labels.Add(labels.GetILGenerator().DefineLabel());
        code[second].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock, null));
        ToolCollisionBuffers.RewriteAwake(code);
        int replacements = 0;
        for (int i = 0; i < code.Count; ++i)
        {
            if (code[i].opcode != original[i].opcode || !object.Equals(code[i].operand, original[i].operand))
            {
                ++replacements;
                Check(code[i].opcode == OpCodes.Ldc_I4 && (int)code[i].operand == 1016,
                    "Unrelated Awake instruction changed.");
            }
        }
        Check(replacements == 2 && code[second].labels.Count == 1 && code[second].blocks.Count == 1,
            "Exact two replacements or control-flow metadata incorrect.");
    }
    private static void TestExisting()
    {
        ToolController tool = Controller(576);
        ulong[] first = Bits(tool, "m_collidingSegments1"), second = Bits(tool, "m_collidingSegments2");
        ulong[] building = Bits(tool, "m_collidingBuildings1");
        first[575] = 0x8000000000000000UL; second[0] = 123;
        ToolCollisionBuffers.EnsureController(tool);
        Check(Bits(tool, "m_collidingSegments1").Length == 1016 && Bits(tool, "m_collidingSegments2").Length == 1016
            && Bits(tool, "m_collidingSegments1")[575] == first[575] && Bits(tool, "m_collidingSegments2")[0] == 123
            && object.ReferenceEquals(building, Bits(tool, "m_collidingBuildings1")), "Migration lost bits or changed building storage.");
        first = Bits(tool, "m_collidingSegments1"); ToolCollisionBuffers.EnsureController(tool);
        Check(object.ReferenceEquals(first, Bits(tool, "m_collidingSegments1")), "Ready migration replaced array.");
        tool = Controller(576); first = Bits(tool, "m_collidingSegments1");
        Field("m_collidingDepth").SetValue(tool, 1);
        bool refused = false;
        try { ToolCollisionBuffers.EnsureController(tool); } catch (InvalidOperationException) { refused = true; }
        Check(refused && object.ReferenceEquals(first, Bits(tool, "m_collidingSegments1")), "Active collision references invalidated.");
        tool = Controller(1000); first = Bits(tool, "m_collidingSegments1"); refused = false;
        try { ToolCollisionBuffers.EnsureController(tool); } catch (InvalidOperationException) { refused = true; }
        Check(refused && object.ReferenceEquals(first, Bits(tool, "m_collidingSegments1")), "Unknown array layout changed.");
        ToolCollisionBuffers.EnsureController(null);
        ToolCollisionBuffers.EnsureController((ToolController)FormatterServices.GetUninitializedObject(typeof(ToolController)));
    }
    private static void Emit(ILGenerator il, CodeInstruction c)
    {
        foreach (Label label in c.labels) il.MarkLabel(label);
        object o = c.operand;
        if (o == null) il.Emit(c.opcode);
        else if (o is MethodInfo) il.Emit(c.opcode, (MethodInfo)o);
        else if (o is FieldInfo) il.Emit(c.opcode, (FieldInfo)o);
        else if (o is Type) il.Emit(c.opcode, (Type)o);
        else if (o is Label) il.Emit(c.opcode, (Label)o);
        else if (o is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)o);
        else if (o is byte) il.Emit(c.opcode, (byte)o);
        else if (o is sbyte) il.Emit(c.opcode, (sbyte)o);
        else if (o is short) il.Emit(c.opcode, (short)o);
        else if (o is int) il.Emit(c.opcode, (int)o);
        else if (o is string) il.Emit(c.opcode, (string)o);
        else throw new Exception("Unsupported original IL operand: " + o.GetType());
    }
    private static Ignore OriginalIgnore()
    {
        MethodInfo method = AccessTools.Method(typeof(BuildingTool), "IgnoreRelocateSegments", null, null);
        DynamicMethod body = new DynamicMethod("ActualIgnoreRelocate", typeof(void),
            new[] { typeof(ushort), typeof(ulong[]), typeof(ulong[]) }, typeof(ToolCollisionBufferTests).Module, true);
        ILGenerator il = body.GetILGenerator();
        foreach (LocalVariableInfo local in method.GetMethodBody().LocalVariables) il.DeclareLocal(local.LocalType);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method, il);
        foreach (CodeInstruction c in code)
        {
            MethodInfo call = c.operand as MethodInfo;
            if (call != null && call.Name == "get_instance" && call.ReturnType == typeof(NetManager))
                c.operand = typeof(ToolCollisionBufferTests).GetMethod("Network");
            if (call != null && call.Name == "get_instance" && call.ReturnType == typeof(BuildingManager))
                c.operand = typeof(ToolCollisionBufferTests).GetMethod("Buildings");
            Emit(il, c);
        }
        return (Ignore)body.CreateDelegate(typeof(Ignore));
    }
    private static void TestRelocation()
    {
        // Actual game structs and original relocation IL; only singleton reads
        // are redirected to detached fixtures. No Unity/runtime save is touched.
        network = (NetManager)FormatterServices.GetUninitializedObject(typeof(NetManager));
        buildings = (BuildingManager)FormatterServices.GetUninitializedObject(typeof(BuildingManager));
        network.m_nodes = new Array16<NetNode>(65024);
        network.m_segments = new Array16<NetSegment>(65024);
        buildings.m_buildings = new Array16<Building>(49152);
        buildings.m_buildings.m_buffer[123].m_netNode = 50000;
        network.m_nodes.m_buffer[50000].m_building = 123;
        Ignore ignore = OriginalIgnore();
        foreach (ushort id in new ushort[] { 36863, 36864, 50000, 65023 })
        {
            network.m_nodes.m_buffer[50000].m_segment0 = id;
            network.m_segments.m_buffer[id].m_flags = (NetSegment.Flags)32;
            ToolController tool = Controller(576);
            MethodInfo begin = AccessTools.Method(typeof(ToolController), "BeginColliding", null, null);
            MethodInfo end = AccessTools.Method(typeof(ToolController), "EndColliding", null, null);
            if (id == 50000)
            {
                object[] args = { null, null }; begin.Invoke(tool, args); bool reproduced = false;
                try { ignore(123, (ulong[])args[0], (ulong[])args[1]); } catch (IndexOutOfRangeException) { reproduced = true; }
                end.Invoke(tool, null);
                Check(reproduced, "Original bitmap overflow was not reproduced.");
            }
            ToolCollisionBuffers.EnsureController(tool);
            object[] callArgs = { null, null }; begin.Invoke(tool, callArgs);
            ulong[] segments = (ulong[])callArgs[0], buildingBits = (ulong[])callArgs[1];
            // BeginColliding resets the maps; collision collection populates
            // them before IgnoreRelocateSegments clears the building's own bits.
            segments[id >> 6] = ulong.MaxValue;
            buildingBits[123 >> 6] = ulong.MaxValue;
            ignore(123, segments, buildingBits);
            ulong own = 1UL << (id & 63);
            Check((segments[id >> 6] & own) == 0 && (segments[id >> 6] & (1UL << ((id + 1) & 63))) != 0,
                "Relocation did not clear only its own segment bit.");
            Check((buildingBits[123 >> 6] & (1UL << (123 & 63))) == 0, "Own building exclusion lost.");
            end.Invoke(tool, null);
            Check(Bits(tool, "m_collidingSegments2")[id >> 6] == segments[id >> 6], "EndColliding failed to publish high-ID bits.");
            AccessTools.Method(typeof(ToolController), "ClearColliding", null, null).Invoke(tool, null);
            Check(Bits(tool, "m_collidingSegments1")[id >> 6] == 0 && Bits(tool, "m_collidingSegments2")[id >> 6] == 0,
                "ClearColliding failed at high ID.");
        }
    }
    private static void TestNestedCollisions()
    {
        ToolController tool = Controller(576);
        ToolCollisionBuffers.EnsureController(tool);
        MethodInfo begin = AccessTools.Method(typeof(ToolController), "BeginColliding", null, null);
        MethodInfo end = AccessTools.Method(typeof(ToolController), "EndColliding", null, null);
        object[] outer = { null, null }; begin.Invoke(tool, outer);
        ulong[] first = (ulong[])outer[0]; first[1015] = 1UL << 63;
        object[] inner = { null, null }; begin.Invoke(tool, inner);
        Check(object.ReferenceEquals(first, inner[0]) && ((ulong[])inner[0])[1015] == (1UL << 63),
            "Nested BeginColliding reset or replaced the outer high-ID collision map.");
        ToolCollisionBuffers.EnsureController(tool);
        Check(object.ReferenceEquals(first, Bits(tool, "m_collidingSegments1")), "Ready active map was replaced.");
        end.Invoke(tool, null);
        Check((int)Field("m_collidingDepth").GetValue(tool) == 1 && Bits(tool, "m_collidingSegments2")[1015] == 0,
            "Inner EndColliding published results before outer collision ended.");
        end.Invoke(tool, null);
        Check((int)Field("m_collidingDepth").GetValue(tool) == 0 && Bits(tool, "m_collidingSegments2")[1015] == (1UL << 63),
            "Outermost EndColliding did not publish the last supported segment bit.");
    }
    private static void TestExistingAuxiliaryAwakePatches()
    {
        Type[] managers = { typeof(CoverageManager), typeof(ElectricityManager), typeof(WaterManager) };
        Type[] patches = { typeof(CoverageManagerAwakePatch), typeof(ElectricityManagerAwakePatch), typeof(WaterManagerAwakePatch) };
        int[] expected = { 2, 2, 4 };
        for (int n = 0; n < managers.Length; ++n)
        {
            MethodInfo originalMethod = AccessTools.Method(managers[n], "Awake", null, null);
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(originalMethod, (ILGenerator)null);
            MethodInfo transpiler = patches[n].GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic);
            List<CodeInstruction> changed = new List<CodeInstruction>((IEnumerable<CodeInstruction>)
                transpiler.Invoke(null, new object[] { originalMethod, Clone(original) }));
            int count = 0;
            for (int i = 0; i < changed.Count; ++i)
            {
                if (object.Equals(original[i].operand, changed[i].operand) && original[i].opcode == changed[i].opcode) continue;
                ++count;
                Check(original[i].opcode == OpCodes.Ldc_I4 && (int)original[i].operand == 32768
                    && changed[i].opcode == OpCodes.Ldc_I4 && (int)changed[i].operand == 65024
                    && changed[i + 1].opcode == OpCodes.Newarr && changed[i + 2].opcode == OpCodes.Stfld,
                    "Existing auxiliary patch changed unrelated manager allocation.");
            }
            Check(count == expected[n], managers[n].Name + " node-indexed allocation coverage changed.");
        }
    }
    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string path in paths)
        {
            string file = Path.Combine(path, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(file)) return Assembly.LoadFrom(file);
        }
        return null;
    }
    private static int Main(string[] args)
    {
        try
        {
            paths = args; AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            TestAwake(); TestExisting(); TestRelocation(); TestNestedCollisions(); TestExistingAuxiliaryAwakePatches();
            Console.WriteLine("PASS: actual relocation overflow fix, nested collision lifecycle, atomic migration and existing coverage/electricity/water Awake allocations.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
