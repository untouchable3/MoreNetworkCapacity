using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

public static class NetworkBufferSizeTests
{
    private static string[] searchPaths;
    public static int NodeSize() { return 32768; }
    public static int SegmentSize() { return 36864; }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    public static int Main(string[] args)
    {
        try
        {
            searchPaths = args;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Run();
            Console.WriteLine("PASS: original Serialize/Deserialize IL, atomic mismatch rejection, duplicate rejection, labels and executable size replacement.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (string directory in searchPaths)
        {
            string path = System.IO.Path.Combine(directory, name);
            if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static void Run()
    {
        FieldInfo nodes = typeof(NetManager).GetField("m_nodes");
        FieldInfo segments = typeof(NetManager).GetField("m_segments");
        FieldInfo nodeBuffer = nodes.FieldType.GetField("m_buffer");
        FieldInfo segmentBuffer = segments.FieldType.GetField("m_buffer");
        MethodInfo nodeSize = typeof(NetworkBufferSizeTests).GetMethod("NodeSize");
        MethodInfo segmentSize = typeof(NetworkBufferSizeTests).GetMethod("SegmentSize");
        Type data = typeof(NetManager).GetNestedType("Data", BindingFlags.Public | BindingFlags.NonPublic);
        foreach (string methodName in new[] { "Serialize", "Deserialize" })
        {
            MethodInfo method = data.GetMethod(methodName);
            List<CodeInstruction> il = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
            List<CodeInstruction> broken = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in il) broken.Add(new CodeInstruction(instruction));
            foreach (CodeInstruction instruction in broken)
            {
                if (segmentBuffer.Equals(instruction.operand))
                {
                    instruction.operand = nodeBuffer;
                    break;
                }
            }
            OpCode[] before = new OpCode[broken.Count];
            for (int i = 0; i < broken.Count; ++i) before[i] = broken[i].opcode;
            bool rejected = false;
            try { NetworkBufferSizePatch.Apply(broken, nodes, nodeBuffer, segments, segmentBuffer, nodeSize, segmentSize); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, methodName + ": missing segment match was accepted");
            for (int i = 0; i < broken.Count; ++i)
                Check(before[i] == broken[i].opcode, methodName + ": failed validation partially mutated IL");

            NetworkBufferSizePatch.Apply(il, nodes, nodeBuffer, segments, segmentBuffer, nodeSize, segmentSize);
            int nodeCalls = 0, segmentCalls = 0;
            foreach (CodeInstruction instruction in il)
            {
                if (nodeSize.Equals(instruction.operand)) ++nodeCalls;
                if (segmentSize.Equals(instruction.operand)) ++segmentCalls;
            }
            Check(nodeCalls == 1 && segmentCalls == 1, methodName + ": wrong replacement counts");
            Console.WriteLine(methodName + ": node=1, segment=1, failure rejected before mutation");
        }

        DynamicMethod fixture = new DynamicMethod("size", typeof(int), Type.EmptyTypes);
        ILGenerator generator = fixture.GetILGenerator();
        LocalBuilder nodeLocal = generator.DeclareLocal(typeof(NetNode[]));
        LocalBuilder segmentLocal = generator.DeclareLocal(typeof(NetSegment[]));
        Label label = generator.DefineLabel();
        List<CodeInstruction> synthetic = new List<CodeInstruction> {
            new CodeInstruction(OpCodes.Ldfld, nodes), new CodeInstruction(OpCodes.Ldfld, nodeBuffer),
            new CodeInstruction(OpCodes.Stloc, nodeLocal), new CodeInstruction(OpCodes.Ldloc, nodeLocal),
            new CodeInstruction(OpCodes.Ldlen), new CodeInstruction(OpCodes.Conv_I4),
            new CodeInstruction(OpCodes.Ldfld, segments), new CodeInstruction(OpCodes.Ldfld, segmentBuffer),
            new CodeInstruction(OpCodes.Stloc, segmentLocal), new CodeInstruction(OpCodes.Ldloc, segmentLocal),
            new CodeInstruction(OpCodes.Ldlen), new CodeInstruction(OpCodes.Conv_I4)
        };
        synthetic[3].labels.Add(label);
        synthetic[4].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        List<CodeInstruction> duplicate = new List<CodeInstruction>(synthetic);
        duplicate.AddRange(synthetic);
        bool duplicateRejected = false;
        try { NetworkBufferSizePatch.Apply(duplicate, nodes, nodeBuffer, segments, segmentBuffer, nodeSize, segmentSize); }
        catch (InvalidOperationException) { duplicateRejected = true; }
        Check(duplicateRejected, "duplicate captures accepted");
        NetworkBufferSizePatch.Apply(synthetic, nodes, nodeBuffer, segments, segmentBuffer, nodeSize, segmentSize);
        Check(synthetic[3].labels.Count == 1 && synthetic[4].blocks.Count == 1, "control flow metadata lost");
        generator.Emit(synthetic[3].opcode);
        generator.Emit(synthetic[4].opcode, (MethodInfo)synthetic[4].operand);
        generator.Emit(synthetic[5].opcode);
        generator.Emit(OpCodes.Ret);
        Check(((Func<int>)fixture.CreateDelegate(typeof(Func<int>)))() == 32768, "rewritten stack shape invalid");
    }
}
