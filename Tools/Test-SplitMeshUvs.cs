using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
class UvProbe {
    static int count;
    static DynamicMethod guard;
    static MethodInfo rewrite;
    static readonly List<Vector4[]> submitted=new List<Vector4[]>();
    static readonly List<int> counts=new List<int>();
    public static bool Eq(UnityEngine.Object a,UnityEngine.Object b){return object.ReferenceEquals(a,b);}
    public static bool Ne(UnityEngine.Object a,UnityEngine.Object b){return !Eq(a,b);}
    public static Mesh NewMesh(){return (Mesh)FormatterServices.GetUninitializedObject(typeof(Mesh));}
    public static void Vertices(Mesh m,Vector3[] v){count=v.Length;}
    public static int Count(Mesh m){return count;}
    public static void Uvs(Mesh m,int channel,Vector4[] v){
        if(channel!=3)throw new Exception("wrong channel");
        object[] args={m,v};guard.Invoke(null,args);submitted.Add((Vector4[])args[1]);counts.Add(count);
    }
    static DynamicMethod Replay(MethodInfo m){
        List<Type> types=new List<Type>(); if(!m.IsStatic)types.Add(m.DeclaringType);
        foreach(ParameterInfo p in m.GetParameters())types.Add(p.ParameterType);
        DynamicMethod dm=new DynamicMethod(m.Name,m.ReturnType,types.ToArray(),typeof(UvProbe).Module,true);
        ILGenerator il=dm.GetILGenerator();foreach(LocalVariableInfo v in m.GetMethodBody().LocalVariables)il.DeclareLocal(v.LocalType);
        IEnumerable<CodeInstruction> instructions = PatchProcessor.GetOriginalInstructions(m,il);
        if (m.Name == "PopulateMesh") instructions = (IEnumerable<CodeInstruction>)rewrite.Invoke(null,new object[]{instructions});
        foreach(CodeInstruction c in instructions){
            foreach(Label l in c.labels)il.MarkLabel(l);
            OpCode op=c.opcode; object o=c.operand;MethodBase f=o as MethodBase;
            if(f!=null){
                string n=f.Name;string replacement=null;
                if(f.DeclaringType==typeof(UnityEngine.Object))replacement=n=="op_Equality"?"Eq":n=="op_Inequality"?"Ne":null;
                if(f.DeclaringType==typeof(Mesh))replacement=n==".ctor"?"NewMesh":n=="set_vertices"?"Vertices":n=="get_vertexCount"?"Count":null;
                if(f.DeclaringType.Name=="MeshExtensions"&&n=="SetUVs")replacement="Uvs";
                if(replacement!=null){op=OpCodes.Call;o=typeof(UvProbe).GetMethod(replacement);}
                else if(f.DeclaringType==typeof(Mesh)||n=="Destroy"&&f.DeclaringType==typeof(UnityEngine.Object)){
                    if(n!="Clear"&&n!="Destroy"&&!n.StartsWith("set_"))throw new Exception(n);
                    foreach(ParameterInfo p in f.GetParameters())il.Emit(OpCodes.Pop);
                    if(!f.IsStatic)il.Emit(OpCodes.Pop);continue;
                }
            }
            if(o==null)il.Emit(op);else if(o is Label)il.Emit(op,(Label)o);else if(o is FieldInfo)il.Emit(op,(FieldInfo)o);
            else if(o is LocalBuilder)il.Emit(op,(LocalBuilder)o);else if(o is ConstructorInfo)il.Emit(op,(ConstructorInfo)o);
            else if(o is MethodInfo)il.Emit(op,(MethodInfo)o);else if(o is Type)il.Emit(op,(Type)o);
            else if(o is int)il.Emit(op,(int)o);else if(o is byte)il.Emit(op,(byte)o);else if(o is sbyte)il.Emit(op,(sbyte)o);
            else if(o is string)il.Emit(op,(string)o);else throw new Exception(o.ToString());
        }return dm;
    }
    static Vector4 Tag(int i){return new Vector4(i+1,2,3,i+4);}
    static void Run(DynamicMethod draw,MethodInfo prefix,Type data,Type wrapper,int length,int uvLength,int nextStart){
        object d=FormatterServices.GetUninitializedObject(data);
        data.GetField("m_vertices").SetValue(d,new Vector3[length]);
        Vector4[] uv=new Vector4[uvLength];for(int i=0;i<uv.Length;i++)uv[i]=Tag(i);
        data.GetField("m_uvs4").SetValue(d,uv);
        int[] triangles=nextStart>=0?new int[]{0,1,2,nextStart,length-2,length-1}:new int[]{0,1,2};
        if(length>130000) triangles=new int[]{0,1,2,nextStart,nextStart+1,nextStart+2,130000,length-2,length-1};
        data.GetField("m_triangles").SetValue(d,triangles);
        prefix.Invoke(null,new object[]{d});counts.Clear();submitted.Clear();
        draw.Invoke(null,new object[]{d,Activator.CreateInstance(wrapper)});
        int[] starts=nextStart>=0?new int[]{0,nextStart}:new int[]{0};
        if(length>130000) starts=new int[]{0,nextStart,130000};
        if(submitted.Count!=starts.Length)throw new Exception("wrong mesh count");
        for(int j=0;j<starts.Length;j++){
            int bad=0,zeros=0;for(int k=0;k<counts[j];k++){
                Vector4 got=submitted[j][k];
                Vector4 expectedValue=starts[j]+k<uvLength?Tag(starts[j]+k):Vector4.zero;
                if(!got.Equals(expectedValue))bad++;
                if(got.Equals(Vector4.zero))zeros++;
            }
            int expected=Math.Max(0,counts[j]-Math.Max(0,uvLength-starts[j]));
            Console.WriteLine("vertices={0} inputUV={1} chunkStart={2} sent={3} wrong={4} padded={5}",length,uvLength,starts[j],counts[j],bad,zeros);
            if(bad!=0 || zeros!=expected || submitted[j].Length!=counts[j])throw new Exception("unexpected UV data");
        }
    }
    static void Main(string[] args){
        Type patch=Assembly.LoadFrom(args[0]).GetType("MoreNetworkCapacity.Patches.RenderGroupMeshDataPatch",true);
        patch.GetField("_fixCount",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,9);
        guard=Replay(AccessTools.Method(patch,"SetUvsPrefix",null,null));
        rewrite=AccessTools.Method(patch,"RewriteSplitUvs",null,null);
        Type d=typeof(RenderGroup).GetNestedType("MeshData"), w=typeof(RenderGroup).GetNestedType("MeshWrapper");
        DynamicMethod draw=Replay(AccessTools.Method(d,"PopulateMesh",new Type[]{w},null));
        MethodInfo pre=AccessTools.Method(patch,"Prefix",null,null);
        Run(draw,pre,d,w,6,6,-1);Run(draw,pre,d,w,6,4,-1);Run(draw,pre,d,w,65000,65000,-1);
        Run(draw,pre,d,w,65003,65003,65000);Run(draw,pre,d,w,65003,65003,64998);
        Run(draw,pre,d,w,65003,65002,65000);Run(draw,pre,d,w,65003,65008,64998);
        Run(draw,pre,d,w,130003,130003,65000);
        MethodInfo target=AccessTools.Method(d,"PopulateMesh",new Type[]{w},null);
        List<CodeInstruction> changed=PatchProcessor.GetOriginalInstructions(target,(ILGenerator)null);
        foreach(CodeInstruction instruction in changed)
            if(instruction.opcode==OpCodes.Call && instruction.operand is MethodInfo
                && ((MethodInfo)instruction.operand).Name=="SetUVs")instruction.opcode=OpCodes.Nop;
        bool refused=false;
        try { rewrite.Invoke(null,new object[]{changed}); }
        catch(TargetInvocationException ex){refused=ex.InnerException is InvalidOperationException;}
        if(!refused)throw new Exception("Unexpected submission accepted");
        Harmony harmony=new Harmony("MNC.offline.splitUV");
        harmony.Patch(target,new HarmonyMethod(pre),null,new HarmonyMethod(rewrite),null);
        try {if(!Harmony.GetPatchInfo(target).Owners.Contains(harmony.Id))throw new Exception("Missing test patch");}
        finally {harmony.Unpatch(target,rewrite); harmony.Unpatch(target,pre);}
    }
}
