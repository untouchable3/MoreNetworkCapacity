using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;
internal static class CinematicLoopTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static List<CodeInstruction> Read(MethodBase m)
    { return PatchProcessor.GetOriginalInstructions(m, new DynamicMethod("CameraLabels", typeof(void), Type.EmptyTypes).GetILGenerator()); }
    private static List<CodeInstruction> Clone(List<CodeInstruction> c)
    { List<CodeInstruction> copy = new List<CodeInstruction>(); foreach(CodeInstruction i in c) copy.Add(new CodeInstruction(i)); return copy; }
    private static int[] Verify(MethodBase m, List<CodeInstruction> c, bool accept, string scenario)
    {
        List<CodeInstruction> before = Clone(c);
        int[] result = CinematicLoopBounds.Resolve(m, c);
        Check(accept ? result != null : result == null, m.Name + " / " + scenario);
        for(int i=0;i<c.Count;++i)
        {
            Check(c[i].opcode == before[i].opcode && object.Equals(c[i].operand,before[i].operand)
                && c[i].labels.Count == before[i].labels.Count && c[i].blocks.Count == before[i].blocks.Count,"Camera input changed");
            for(int j=0;j<c[i].labels.Count;++j) Check(c[i].labels[j].Equals(before[i].labels[j]),"Camera label changed");
        }
        return result;
    }
    private static void Test(string name, int[] expected)
    {
        MethodBase m = AccessTools.Method(typeof(CinematicCameraController),name,null,null);
        Check(CinematicLoopBounds.Handles(m),"Missing reviewed identity");
        List<CodeInstruction> original=Read(m),c;
        int[] found=Verify(m,original,true,"original");
        Check(found.Length==expected.Length,"Wrong reviewed bounds count");
        for(int i=0;i<found.Length;++i) Check(found[i]==expected[i],"Wrong bound selected");
        c=Clone(original); c.Insert(0,new CodeInstruction(OpCodes.Nop,null)); Verify(m,c,true,"entry NOP");
        c=Clone(original);
        for(int i=c.Count-1;i>=0;--i)
        {
            CodeInstruction nop=new CodeInstruction(OpCodes.Nop,null); nop.labels.AddRange(c[i].labels); c[i].labels.Clear(); c.Insert(i,nop);
        }
        Verify(m,c,true,"all instructions padded with labels on NOP");
        c=Clone(original);
        for(int i=expected.Length-1;i>=0;--i) for(int j=0;j<9;++j) c.Insert(expected[i],new CodeInstruction(OpCodes.Nop,null));
        Verify(m,c,true,"narrowing farther than six instructions");
        c=Clone(original); c.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,36864)); c.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        Verify(m,c,true,"same value noise");
        foreach(int bound in expected)
        {
            c=Clone(original); c[bound].operand=36863; Verify(m,c,false,"changed bound");
            c.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,36864));c.Insert(1,new CodeInstruction(OpCodes.Pop,null));
            Verify(m,c,false,"noise cannot replace missing bound");
            c=Clone(original); c[bound+1].opcode=OpCodes.Beq;Verify(m,c,false,"changed predicate");
            c=Clone(original); c[0].labels.Add((Label)c[bound+1].operand);Verify(m,c,false,"ambiguous target");
        }
        c=Clone(original);
        for(int i=0;i<c.Count;++i) if(c[i].opcode==OpCodes.Conv_U2) {c[i].opcode=OpCodes.Nop;break;}
        Verify(m,c,false,"removed reviewed narrowing");
        c=Clone(original);
        for(int i=0;i<c.Count;++i)
        {
            FieldInfo f=c[i].operand as FieldInfo;
            if(f!=null && f.DeclaringType==typeof(NetManager) && f.Name=="m_segments")
            {c[i].operand=AccessTools.Field(typeof(NetManager),"m_nodes");break;}
        }
        Verify(m,c,false,"different pool provenance");
        c=Clone(original); int load=expected[expected.Length-1]-1;
        int local=original[load].opcode==OpCodes.Ldloc_2?2:3;
        ILGenerator locals=new DynamicMethod("CameraWrongLocal",typeof(void),Type.EmptyTypes).GetILGenerator();
        for(int i=0;i<local;++i)locals.DeclareLocal(typeof(object));
        c[load].opcode=OpCodes.Ldloc;c[load].operand=locals.DeclareLocal(typeof(int));
        Verify(m,c,false,"same-index changed local declaration");
        c=Clone(original);c.Insert(0,new CodeInstruction(OpCodes.Ldloca,local));c.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        Verify(m,c,false,"camera counter address escapes");
        c=Clone(original);c.AddRange(Clone(original));Verify(m,c,false,"duplicate labelled region");
        Verify(m,new List<CodeInstruction>(),false,"empty input");
        Console.WriteLine("PASS: "+name+" reviewed preservation, NOP distance, aliases, roots, types, predicates and atomic refusal.");
    }
    public static int Main()
    { try {Test("GetRandomRoad",new[]{20,90});Test("GetRoadWithName",new[]{40});Check(!CinematicLoopBounds.Handles(null),"Null method accepted");return 0;}
      catch(Exception e){Console.Error.WriteLine(e);return 1;} }
}
