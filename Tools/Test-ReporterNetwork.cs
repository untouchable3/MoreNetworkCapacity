using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

class ReporterNetworkTests
{
    const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }

    static void TestTarget(Type compatibility, MethodInfo method)
    {
        MethodInfo rewrite = compatibility.GetMethod("Rewrite", Methods);
        List<CodeInstruction> input = PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null);
        List<CodeInstruction> result = (List<CodeInstruction>)rewrite.Invoke(null, new object[] {method, input});
        int changed = 0;
        for (int i = 0; i < input.Count; i++)
        {
            if (input[i].opcode == result[i].opcode && Equals(input[i].operand, result[i].operand)) continue;
            changed++;
            bool train = method.Name == "CheckNextLane";
            bool segment = method.Name == "RayCast";
            int vanilla = segment ? 36864 : 32768;
            Check(Equals(input[i].operand, vanilla), "Unexpected old limit");
            MemberInfo member = (MemberInfo)result[i].operand;
            FieldInfo limit = member.DeclaringType.GetField(segment ? "SegmentLoopLimit" : "NodeLoopLimit");
            Check(train ? member.Name == "GetNodeSimulationPhaseDivisor" : Equals(member, limit), "Wrong capacity");
            foreach (int size in new int[] {vanilla, 65024})
            {
                limit.SetValue(null, size);
                int value = train ? (int)((MethodInfo)member).Invoke(null, null) : (int)limit.GetValue(null);
                Check(value == size, "Capacity mismatch");
            }
            input[i].operand = -1;
            try { rewrite.Invoke(null, new object[] {method,input}); throw new Exception("Changed input accepted"); }
            catch (TargetInvocationException ex) { if (!(ex.InnerException is InvalidOperationException)) throw; }
        }
        Check(changed == 1 && input.Count == result.Count, "Unexpected edits");
        if (method.Name == "RenderNearNodes" && Environment.GetEnvironmentVariable("MNC_TEST_IL_ONLY") == "1")
        {
            Console.WriteLine("PASS IL (Unity scene required for initialization) " + method);
            return;
        }
        Harmony h = new Harmony("mnc.reporter.offline");
        h.Patch(method, null, null, new HarmonyMethod(rewrite), null);
        try { Check(Harmony.GetPatchInfo(method).Owners.Contains(h.Id), "Installation failed"); }
        finally { h.Unpatch(method, rewrite); }
        Console.WriteLine("PASS " + method);
    }

    static void TestStops(Assembly candidate, Assembly mod)
    {
        MethodInfo method = AccessTools.Method(typeof(TransportTool), "GetStopPosition", null, null);
        MethodInfo resolve = candidate.GetType("MoreNetworkCapacity.Patches.TransportLifecycleLoopBounds").GetMethod("Resolve", Methods);
        FieldInfo limit = candidate.GetType("MoreNetworkCapacity.Capacity").GetField("NodeLoopLimit");
        MethodInfo selection = mod.GetType("AdvancedStopSelection.Patcher").GetMethod("TransportToolGetStopPositionTranspiler", Methods);
        foreach (bool mncFirst in new bool[] {true, false})
        {
            ILGenerator il = new DynamicMethod("Stops", typeof(void), Type.EmptyTypes).GetILGenerator();
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method, il);
            int vanillaCount = code.Count;
            for (int pass = 0; pass < 2; pass++)
            {
                if ((pass == 0) == mncFirst)
                {
                    int[] indexes = (int[])resolve.Invoke(null, new object[]{method,code});
                    Check(indexes != null && indexes.Length == 1, "Stop loop not resolved");
                    code[indexes[0]].opcode = OpCodes.Ldsfld;
                    code[indexes[0]].operand = limit;
                }
                else code = new List<CodeInstruction>((IEnumerable<CodeInstruction>)selection.Invoke(null,new object[]{il,code,method}));
            }
            Check(code.Count != vanillaCount && code.Exists(delegate(CodeInstruction c){return Equals(c.operand,limit);}), "Selection or bound missing");
            Console.WriteLine("PASS stop selection, MNC first=" + mncFirst);
        }
    }

    static void Main(string[] args)
    {
        Assembly candidate = Assembly.LoadFrom(args[0]);
        Type compatibility = candidate.GetType("MoreNetworkCapacity.Compatibility.NetworkSearchCompatibility", true);
        for (int i = 1; i < args.Length; i++)
        {
            Assembly mod = Assembly.LoadFrom(args[i]);
            if (mod.GetName().Name == "AdvancedStopSelection") TestStops(candidate,mod);
            else foreach (MethodInfo method in (List<MethodInfo>)compatibility.GetMethod("Targets",Methods).Invoke(null,new object[]{mod}))
                TestTarget(compatibility,method);
        }
    }
}
