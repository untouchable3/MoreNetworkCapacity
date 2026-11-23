using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class TransportLifecycleLoopTests
{
    public static void MutateAlias(ref ushort value) { value=0; }
    public static void MutateButtons(ref ColossalFramework.UI.UIButton[] value) { value=null; }
    private static void Check(bool value,string text) { if(!value) throw new Exception(text); }
    private static List<CodeInstruction> Read(MethodInfo method,out ILGenerator labels)
    {
        labels = new DynamicMethod("LifeLabels",typeof(void),Type.EmptyTypes).GetILGenerator();
        return PatchProcessor.GetOriginalInstructions(method,labels);
    }
    private static void Noise(List<CodeInstruction> code,ILGenerator labels)
    {
        Label end=labels.DefineLabel();CodeInstruction marker=new CodeInstruction(OpCodes.Nop,null);marker.labels.Add(end);
        code.InsertRange(0,new[]{new CodeInstruction(OpCodes.Ldc_I4_0,null),new CodeInstruction(OpCodes.Ldc_I4,32768),new CodeInstruction(OpCodes.Blt,end),marker});
    }
    private static int Local(CodeInstruction code)
    {
        string name=code.opcode.Name;
        if(name=="ldloc.0" || name=="stloc.0") return 0;if(name=="ldloc.1" || name=="stloc.1") return 1;
        if(name=="ldloc.2" || name=="stloc.2") return 2;if(name=="ldloc.3" || name=="stloc.3") return 3;
        LocalBuilder b=code.operand as LocalBuilder;if(b!=null) return b.LocalIndex;
        LocalVariableInfo v=code.operand as LocalVariableInfo;if(v!=null) return v.LocalIndex;
        return Convert.ToInt32(code.operand);
    }
    private static void Verify(MethodInfo method,List<CodeInstruction> code,CodeInstruction[] expected)
    {
        List<CodeInstruction> before=new List<CodeInstruction>();foreach(CodeInstruction c in code) before.Add(new CodeInstruction(c));
        int[] found=TransportLifecycleLoopBounds.Resolve(method,code);
        Check(expected==null?found==null:found!=null && found.Length==expected.Length,"Unexpected layout "+method.DeclaringType.Name+"."+method.Name);
        if(expected!=null) for(int i=0;i<found.Length;++i) Check(ReferenceEquals(expected[i],code[found[i]]),"Wrong target reference");
        Check(code.Count==before.Count,"Changed length");
        for(int i=0;i<code.Count;++i)
        {
            Check(code[i].opcode==before[i].opcode && object.Equals(code[i].operand,before[i].operand)
                && code[i].labels.Count==before[i].labels.Count && code[i].blocks.Count==before[i].blocks.Count,"Mutated code");
            for(int j=0;j<code[i].labels.Count;++j) Check(code[i].labels[j].Equals(before[i].labels[j]),"Mutated label");
            for(int j=0;j<code[i].blocks.Count;++j) Check(ReferenceEquals(code[i].blocks[j],before[i].blocks[j]),"Mutated EH");
        }
    }
    private static int Start(List<CodeInstruction> code,int bound)
    {
        Label destination=(Label)code[bound+1].operand;
        int condition=code.FindIndex(delegate(CodeInstruction c){return c.labels.Contains(destination);});
        for(int i=condition+1;i<code.Count;++i)
        {
            if(!(code[i].operand is Label) || code[i].opcode.FlowControl!=FlowControl.Cond_Branch) continue;
            Label target=(Label)code[i].operand;
            int body=code.FindIndex(delegate(CodeInstruction c){return c.labels.Contains(target);});
            if(body>=0 && body<bound) return body;
        }
        throw new Exception("Body label missing");
    }
    private static void Cases(MethodInfo method,List<int> indexes)
    {
        ILGenerator labels;
        for(int variant=0;variant<6;++variant)
        {
            List<CodeInstruction> code=Read(method,out labels);
            CodeInstruction[] selected=indexes.ConvertAll(delegate(int n){return code[n];}).ToArray();
            if(variant==1) code.Insert(0,new CodeInstruction(OpCodes.Nop,null));
            if(variant==2) for(int i=code.Count-1;i>=0;--i) code.Insert(i,new CodeInstruction(OpCodes.Nop,null));
            if(variant==3) Noise(code,labels);
            if(variant==4) foreach(CodeInstruction c in selected) {c.labels.Add(labels.DefineLabel());c.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock,null));}
            if(variant==5)
            {
                ILGenerator extra=new DynamicMethod("Extra",typeof(void),Type.EmptyTypes).GetILGenerator();
                foreach(LocalVariableInfo local in method.GetMethodBody().LocalVariables) extra.DeclareLocal(local.LocalType);
                LocalBuilder added=extra.DeclareLocal(typeof(string));
                code.InsertRange(0,new[]{new CodeInstruction(OpCodes.Ldnull,null),new CodeInstruction(OpCodes.Stloc,added)});
            }
            Verify(method,code,selected);
        }
        foreach(int bound in indexes)
        {
            List<CodeInstruction> code=Read(method,out labels);code[bound].operand=32767;Noise(code,labels);Verify(method,code,null);
            code=Read(method,out labels);code[bound+1].opcode=OpCodes.Bge;Verify(method,code,null);
            code=Read(method,out labels);code.Insert(bound,new CodeInstruction(OpCodes.Conv_U2,null));Verify(method,code,null);
            code=Read(method,out labels);code[0].labels.Add((Label)code[bound+1].operand);Verify(method,code,null);
            code=Read(method,out labels);int counter=Local(code[bound-5]);
            ILGenerator altered=new DynamicMethod("Narrow",typeof(void),Type.EmptyTypes).GetILGenerator();
            for(int i=0;i<counter;++i) altered.DeclareLocal(typeof(int));
            LocalBuilder wrong=altered.DeclareLocal(typeof(ushort));code[bound-5].opcode=OpCodes.Ldloc;code[bound-5].operand=wrong;Verify(method,code,null);
            code=Read(method,out labels);int start=Start(code,bound);
            code.InsertRange(start+1,new[]{new CodeInstruction(OpCodes.Ldloca,counter),new CodeInstruction(OpCodes.Pop,null)});Verify(method,code,null);
            code=Read(method,out labels);code[bound-3].opcode=OpCodes.Sub;Verify(method,code,null);
            code=Read(method,out labels);Label conditionLabel=(Label)code[bound+1].operand;
            int condition=code.FindIndex(delegate(CodeInstruction c){return c.labels.Contains(conditionLabel);});
            code[condition+1].opcode=code[condition+1].opcode==OpCodes.Brtrue?OpCodes.Brfalse:OpCodes.Brtrue;Verify(method,code,null);
            code=Read(method,out labels);code[condition-1].operand=code[bound+1].operand;Verify(method,code,null);
            code=Read(method,out labels);bool changed=false;
            for(int i=bound-1;i>=start;--i)
            {
                MethodInfo call=code[i].operand as MethodInfo;FieldInfo f=code[i].operand as FieldInfo;
                if(call!=null && call.DeclaringType==typeof(TransportLine) && call.Name=="GetNextStop")
                {code[i].operand=AccessTools.Method(typeof(TransportLine),"GetPrevStop",new[]{typeof(ushort)},null);changed=true;break;}
                if(code[i].opcode==OpCodes.Ldfld && f!=null && (f.DeclaringType==typeof(NetNode) && (f.Name=="m_nextGridNode" || f.Name=="m_nextBuildingNode")
                    || f.DeclaringType==typeof(NetSegment) && f.Name=="m_endNode"))
                {code[i].operand=f.DeclaringType==typeof(NetNode)?AccessTools.Field(typeof(NetNode),"m_nextLaneNode"):AccessTools.Field(typeof(NetSegment),"m_startNode");changed=true;break;}
            }
            Check(changed,"Missing traversal fixture "+method.Name);Verify(method,code,null);
            code=Read(method,out labels);changed=false;
            for(int i=start-1;i>=0;--i)
            {
                FieldInfo f=code[i].operand as FieldInfo;
                if(code[i].opcode!=OpCodes.Ldfld || f==null) continue;
                if(f.DeclaringType==typeof(TransportLine) && f.Name=="m_stops")
                {code[i].operand=AccessTools.Field(typeof(TransportLine),"m_vehicles");changed=true;break;}
                if(f.DeclaringType==typeof(Building) && f.Name=="m_netNode")
                {code[i].operand=AccessTools.Field(typeof(Building),"m_parentBuilding");changed=true;break;}
                if(f.DeclaringType==typeof(NetManager) && f.Name=="m_nodeGrid")
                {code[i].operand=AccessTools.Field(typeof(NetManager),"m_segmentGrid");changed=true;break;}
            }
            Check(changed,"Missing root fixture "+method.Name);Verify(method,code,null);
            if(method.Name=="ClearStops" || method.Name=="ReleaseLineImplementation")
            {
                code=Read(method,out labels);
                for(int i=condition;i<code.Count;++i) if(code[i].opcode==OpCodes.Ldc_I4_3){code[i].opcode=OpCodes.Ldc_I4_1;break;}
                Verify(method,code,null);
            }
            if(method.Name=="OnSetTarget")
            {code=Read(method,out labels);code[condition+6].opcode=OpCodes.Ble;Verify(method,code,null);}
            if(method.Name=="AfterDeserialize")
            {code=Read(method,out labels);code[141].opcode=OpCodes.Brtrue;Verify(method,code,null);}
            if(method.Name=="GetLastStop")
            {code=Read(method,out labels);code[49].opcode=OpCodes.Ldc_I4_0;Verify(method,code,null);}
            if(method.Name=="UpdateMeshData")
            {
                code=Read(method,out labels);
                if(bound==174) code[167].opcode=OpCodes.Brfalse; else code[469].opcode=OpCodes.Beq;
                Verify(method,code,null);
            }
        }
        foreach(OpCode op in new[]{OpCodes.Ldloc,OpCodes.Stloc,OpCodes.Ldloca})
        {List<CodeInstruction> code=Read(method,out labels);code.Insert(0,new CodeInstruction(op,new object()));Verify(method,code,null);}
        if(method.Name=="CloneLine")
        {
            List<CodeInstruction> code=Read(method,out labels);code[14].opcode=OpCodes.Ldarg_1;Verify(method,code,null);
            code=Read(method,out labels);
            code.InsertRange(28,new[]{new CodeInstruction(OpCodes.Ldloca,3),
                new CodeInstruction(OpCodes.Call,typeof(TransportLifecycleLoopTests).GetMethod("MutateAlias"))});Verify(method,code,null);
        }
        if(method.Name=="GetLastStop")
        {
            List<CodeInstruction> code=Read(method,out labels);
            ILGenerator altered=new DynamicMethod("WrongCursorType",typeof(void),Type.EmptyTypes).GetILGenerator();
            foreach(LocalVariableInfo local in method.GetMethodBody().LocalVariables) altered.DeclareLocal(local.LocalType);
            LocalBuilder cursor=altered.DeclareLocal(typeof(int));
            foreach(CodeInstruction c in code)
                if((c.opcode.Name.StartsWith("ldloc") || c.opcode.Name.StartsWith("stloc")) && Local(c)==1)
                {c.opcode=c.opcode.Name.StartsWith("stloc")?OpCodes.Stloc:OpCodes.Ldloc;c.operand=cursor;}
            Verify(method,code,null);
        }
        if(method.Name=="OnSetTarget")
        {
            List<CodeInstruction> code=Read(method,out labels);code[154].opcode=OpCodes.Ldloc;code[154].operand=9;Verify(method,code,null);
            code=Read(method,out labels);code.InsertRange(175,new[]{new CodeInstruction(OpCodes.Ldloca,7),
                new CodeInstruction(OpCodes.Call,typeof(TransportLifecycleLoopTests).GetMethod("MutateButtons"))});Verify(method,code,null);
            code=Read(method,out labels);code[147].opcode=OpCodes.Ldnull;code[147].operand=null;Verify(method,code,null);
        }
        {
            List<CodeInstruction> code=Read(method,out labels);
            ILGenerator a=new DynamicMethod("StringExtra",typeof(void),Type.EmptyTypes).GetILGenerator();
            ILGenerator b=new DynamicMethod("IntExtra",typeof(void),Type.EmptyTypes).GetILGenerator();
            foreach(LocalVariableInfo local in method.GetMethodBody().LocalVariables){a.DeclareLocal(local.LocalType);b.DeclareLocal(local.LocalType);}
            code.InsertRange(0,new[]{new CodeInstruction(OpCodes.Ldnull,null),new CodeInstruction(OpCodes.Stloc,a.DeclareLocal(typeof(string))),
                new CodeInstruction(OpCodes.Ldc_I4_0,null),new CodeInstruction(OpCodes.Stloc,b.DeclareLocal(typeof(int)))});Verify(method,code,null);
        }
        List<CodeInstruction> doubled=Read(method,out labels),copy=new List<CodeInstruction>();Dictionary<Label,Label> map=new Dictionary<Label,Label>();
        foreach(CodeInstruction c in doubled) foreach(Label label in c.labels) if(!map.ContainsKey(label)) map.Add(label,labels.DefineLabel());
        foreach(CodeInstruction c in doubled)
        {
            CodeInstruction d=new CodeInstruction(c);d.labels.Clear();foreach(Label label in c.labels)d.labels.Add(map[label]);
            if(d.operand is Label)d.operand=map[(Label)d.operand];Label[] table=d.operand as Label[];
            if(table!=null){Label[] newTable=new Label[table.Length];for(int i=0;i<table.Length;++i)newTable[i]=map[table[i]];d.operand=newTable;}copy.Add(d);
        }
        doubled.AddRange(copy);Verify(method,doubled,null);
        Console.WriteLine("PASS: "+method.DeclaringType.Name+"."+method.Name+" actual/NOP/noise, per-bound atomic refusal and immutable metadata.");
    }
    public static int Main(string[] args)
    {
        try
        {
            int methods=0,bounds=0;
            Dictionary<string,List<int>> expected=new Dictionary<string,List<int>>();
            foreach(string line in File.ReadAllLines(args[0]))
            {
                string[] cells=line.Split(new[]{"\",\""},StringSplitOptions.None);
                if(cells.Length<9 || cells[0]=="\"Type") continue;
                string key=cells[0].Trim('"')+"."+cells[1]; List<int> indexes;
                if(!expected.TryGetValue(key,out indexes)) { indexes=new List<int>();expected.Add(key,indexes); }
                indexes.Add(int.Parse(cells[4]));
            }
            foreach(Type type in new[]{typeof(TransportLine),typeof(TransportManager),typeof(TransportManager).GetNestedType("Data",BindingFlags.Public|BindingFlags.NonPublic),
                typeof(TransportTool),typeof(BusAI),typeof(PublicTransportWorldInfoPanel),typeof(PathVisualizer)})
                foreach(MethodInfo method in AccessTools.GetDeclaredMethods(type))
                {
                    if(!TransportLifecycleLoopBounds.Handles(method)) continue;
                    ILGenerator labels; List<CodeInstruction> code=Read(method,out labels);
                    int[] result=TransportLifecycleLoopBounds.Resolve(method,code); string key=type.FullName+"."+method.Name;
                    Console.WriteLine(key+": "+(result==null?"REFUSED":string.Join(",",Array.ConvertAll(result,delegate(int x){return x.ToString();}))));
                    Check(result!=null && result.Length==expected[key].Count,"Original mismatch "+key);
                    for(int i=0;i<result.Length;++i) Check(result[i]==expected[key][i],"Wrong selection "+key);
                    Cases(method,expected[key]);
                    ++methods;bounds+=result.Length;
                }
            Check(methods==13 && bounds==14,"Wrong counts");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
