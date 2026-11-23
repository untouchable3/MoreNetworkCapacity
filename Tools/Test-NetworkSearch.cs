using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;

class NetworkSearchTests
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
    static string[] paths;
    public static NetManager network;
    static MethodInfo rewrite;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        foreach (string dir in paths)
        {
            string path = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
    static List<CodeInstruction> Rewrite(MethodInfo method, List<CodeInstruction> code)
    { return (List<CodeInstruction>)rewrite.Invoke(null, new object[] { method, code }); }

    static void Emit(ILGenerator il, CodeInstruction c)
    {
        foreach (Label label in c.labels) il.MarkLabel(label);
        object o = c.operand;
        if (o == null) il.Emit(c.opcode);
        else if (o is Label) il.Emit(c.opcode, (Label)o);
        else if (o is MethodInfo) il.Emit(c.opcode, (MethodInfo)o);
        else if (o is ConstructorInfo) il.Emit(c.opcode, (ConstructorInfo)o);
        else if (o is FieldInfo) il.Emit(c.opcode, (FieldInfo)o);
        else if (o is Type) il.Emit(c.opcode, (Type)o);
        else if (o is LocalBuilder) il.Emit(c.opcode, (LocalBuilder)o);
        else if (o is byte) il.Emit(c.opcode, (byte)o);
        else if (o is int) il.Emit(c.opcode, (int)o);
        else throw new Exception("Unknown operand " + o);
    }
    static DynamicMethod Map(MethodInfo method, bool patched)
    {
        DynamicMethod dm = new DynamicMethod("AssetMap", typeof(byte[]), new Type[] {method.DeclaringType}, typeof(NetworkSearchTests).Module, true);
        ILGenerator il = dm.GetILGenerator();
        foreach (LocalVariableInfo v in method.GetMethodBody().LocalVariables) il.DeclareLocal(v.LocalType);
        List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(method, il);
        if (patched) code = Rewrite(method, code);
        foreach (CodeInstruction c in code)
        {
            MethodInfo m = c.operand as MethodInfo;
            if (m != null && m.Name == "get_instance" && m.ReturnType == typeof(NetManager))
            { c.opcode = OpCodes.Ldsfld; c.operand = typeof(NetworkSearchTests).GetField("network"); }
            Emit(il, c);
        }
        return dm;
    }
    static void TestMap(MethodInfo method, Type concrete)
    {
        object instance = FormatterServices.GetUninitializedObject(concrete);
        DynamicMethod old = Map(method, false), updated = Map(method, true);
        foreach (int size in new int[] {36864, 65024})
        {
            network = (NetManager)FormatterServices.GetUninitializedObject(typeof(NetManager));
            network.m_segments = new Array16<NetSegment>((uint)size);
            List<byte> expected = new List<byte>();
            foreach (ushort id in new ushort[] {17,36863,36864,65023})
            {
                if (id >= size) continue;
                network.m_segments.m_buffer[id].m_flags = NetSegment.Flags.Created;
                network.m_segments.m_buffer[id].m_startNode = (ushort)(id-1);
                network.m_segments.m_buffer[id].m_endNode = id;
                foreach (ushort value in new ushort[] {id,(ushort)(id-1),id}) expected.AddRange(BitConverter.GetBytes(value));
            }
            byte[] before = (byte[])old.Invoke(null, new object[]{instance});
            byte[] after = (byte[])updated.Invoke(null, new object[]{instance});
            Check(before.Length == 12 && after.Length == expected.Count, "Asset record count");
            for(int i=0;i<before.Length;i++) Check(before[i]==after[i],"Low record changed");
            // GetBytes in the shipped assembly determines which byte comes first.
            bool little = before[0] == 17;
            for(int i=0;i<after.Length;i++) Check(after[i]==expected[little ? i : (i^1)], "Asset record mismatch at "+i);
        }
    }
    static void TestChanges(MethodInfo m)
    {
        List<CodeInstruction> input = PatchProcessor.GetOriginalInstructions(m, (ILGenerator)null);
        List<CodeInstruction> output = Rewrite(m, input);
        bool map = m.Name == "GetMap";
        int changes=0, shift=0, bound=-1;
        for(int i=0;i<input.Count;i++)
        {
            CodeInstruction a=input[i], b=output[i+shift];
            if(a.opcode==b.opcode && Equals(a.operand,b.operand))continue;
            Check(a.opcode==OpCodes.Ldc_I4, "Changed non-constant"); changes++; bound=i;
            if(map)
            {
                Check(b.opcode==OpCodes.Ldloc_0 && ((MethodInfo)output[i+1].operand).Name=="AssetSegmentCount","Wrong map bound");
                shift++;
            }
            else
            {
                bool node = ((FieldInfo)input[i-7].operand).Name=="m_nextGridNode";
                FieldInfo limit=(FieldInfo)b.operand;
                Check(b.opcode==OpCodes.Ldsfld && limit.Name==(node?"NodeLoopLimit":"SegmentLoopLimit"),"Wrong pool");
                DynamicMethod dm=new DynamicMethod("Guard",typeof(bool),new Type[]{typeof(int)},typeof(NetworkSearchTests).Module,true);
                ILGenerator il=dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(b.opcode,limit);
                bool inclusive=input[i+1].opcode==OpCodes.Ble_S;
                if(inclusive){ Label yes=il.DefineLabel(); il.Emit(OpCodes.Ble_S,yes); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); il.MarkLabel(yes); il.Emit(OpCodes.Ldc_I4_1); }
                else {Check(input[i+1].opcode==OpCodes.Clt,"Unexpected comparison");il.Emit(OpCodes.Clt);}
                il.Emit(OpCodes.Ret);
                foreach(int n in new int[]{node?32768:36864,65024})
                {
                    limit.SetValue(null,n);
                    foreach(int count in new int[]{32767,32768,36863,36864,65023,65024,65025})
                        Check((bool)dm.Invoke(null,new object[]{count})==(inclusive?count<=n:count<n),"Wrong guard");
                }
            }
        }
        Check(output.Count==input.Count+shift && changes==(m.DeclaringType.FullName=="MoveIt.MoveItTool"?2:1),"Unexpected number of edits");
        input[bound].operand=12345;
        bool rejected=false;
        try{Rewrite(m,input);}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}
        Check(rejected,"Altered input accepted");
    }
    static int Main(string[] args)
    {
        try
        {
            SetErrorMode(3);
            paths=args; AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
            Assembly candidate=Assembly.LoadFrom(Path.Combine(args[0],"MoreNetworkCapacity.dll"));
            Assembly mod=Assembly.LoadFrom(Path.Combine(args[1],Path.GetFileName(args[args.Length-1])));
            Type t=candidate.GetType("MoreNetworkCapacity.Compatibility.NetworkSearchCompatibility",true);
            rewrite=t.GetMethod("Rewrite",BindingFlags.NonPublic|BindingFlags.Static);
            List<MethodInfo> targets=(List<MethodInfo>)t.GetMethod("Targets",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{mod});
            foreach(MethodInfo m in targets)
            {
                TestChanges(m);
                if(m.Name=="GetMap") TestMap(m,mod.GetType(mod.GetName().Name=="NodeControllerRenewal"?"NodeController.AssetDataExtension":"IMT.BuildingAssetDataExtension",true));
                if (Environment.GetEnvironmentVariable("MNC_TEST_IL_ONLY") != "1")
                {
                    Harmony harmony = new Harmony("MNC.offline.networksearch");
                    harmony.Patch(m, null, null, new HarmonyMethod(rewrite), null);
                    try { Check(Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id), "Harmony owner missing"); }
                    finally { harmony.Unpatch(m, rewrite); }
                }
                Console.WriteLine("PASS "+mod.GetName().Name+" "+m.Name);
            }
            return 0;
        }
        catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
