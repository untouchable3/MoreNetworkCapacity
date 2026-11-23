using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class StationLaneLoopTests
{
    private sealed class Case
    {
        internal Type Type; internal string Name; internal int[] Indexes;
        internal Case(Type type, string name, params int[] indexes) { Type=type;Name=name;Indexes=indexes; }
    }
    private static Case[] Cases = new Case[]
    {
        new Case(typeof(CableCarAI),"FindNextStop",126),
        new Case(typeof(CableCarPathAI),"UpdateNode",77),
        new Case(typeof(CableCarStationAI),"CalculateLaneNodePassengers",30),
        new Case(typeof(CableCarStationAI),"CountLines",59),
        new Case(typeof(CableCarStationAI),"FindLineNode",110),
        new Case(typeof(CableCarStationAI),"FindPathSegment",68),
        new Case(typeof(CableCarStationAI),"FindTargetSegment",101),
        new Case(typeof(CableCarStationAI),"ProduceGoods",208),
        new Case(typeof(ParkGateAI),"UpdatePathCost",224),
        new Case(typeof(ShelterAI),"SimulationStep",191),
        new Case(typeof(TollBoothAI),"UpdatePathCost",140),
        new Case(typeof(TransportStationAI),"CalculateLaneNodePassengers",30),
        new Case(typeof(TransportStationAI),"ProduceGoods",261),
        new Case(typeof(TransportStationAI),"RemoveConnectionLines",78),
    };
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    {List<CodeInstruction> copy=new List<CodeInstruction>();foreach(CodeInstruction c in code)copy.Add(new CodeInstruction(c));return copy;}
    private static List<CodeInstruction> Read(MethodBase method)
    {return PatchProcessor.GetOriginalInstructions(method,new DynamicMethod("BuildingLabels",typeof(void),Type.EmptyTypes).GetILGenerator());}
    private static int[] Resolve(MethodBase method,List<CodeInstruction> code,bool accept,string message)
    {
        List<CodeInstruction> before=Clone(code);
        int[] result=StationLaneLoopBounds.Resolve(method,code);
        Check(accept?result!=null:result==null,method.DeclaringType.Name+"."+method.Name+": "+message);
        for(int i=0;i<code.Count;++i)
        {
            Check(code[i].opcode==before[i].opcode&&object.Equals(code[i].operand,before[i].operand),"Input mutated");
            Check(code[i].labels.Count==before[i].labels.Count&&code[i].blocks.Count==before[i].blocks.Count,"Metadata mutated");
            for(int j=0;j<code[i].labels.Count;++j)Check(code[i].labels[j].Equals(before[i].labels[j]),"Label mutated");
        }
        return result;
    }
    private static int FieldAnchor(List<CodeInstruction> code,int bound,bool array)
    {
        if(array)
        {
            Label target=(Label)code[bound+1].operand;
            for(int i=0;i<code.Count;++i)if(code[i].labels.Contains(target))return i+5;
            return -1;
        }
        for(int i=bound-1;i>=0;--i)
        {
            FieldInfo f=code[i].operand as FieldInfo;
            if(code[i].opcode==OpCodes.Ldfld&&f!=null&&(f.DeclaringType==typeof(NetNode)||f.DeclaringType==typeof(NetSegment))
                &&(f.Name=="m_nextBuildingNode"||f.Name=="m_nextGridNode"||f.Name=="m_nextGridSegment"))return i;
        }
        return -1;
    }
    private static int LocalIndex(CodeInstruction c)
    {
        OpCode op=c.opcode;
        if(op==OpCodes.Ldloc_0||op==OpCodes.Stloc_0)return 0;
        if(op==OpCodes.Ldloc_1||op==OpCodes.Stloc_1)return 1;
        if(op==OpCodes.Ldloc_2||op==OpCodes.Stloc_2)return 2;
        if(op==OpCodes.Ldloc_3||op==OpCodes.Stloc_3)return 3;
        LocalBuilder builder=c.operand as LocalBuilder;if(builder!=null)return builder.LocalIndex;
        LocalVariableInfo variable=c.operand as LocalVariableInfo;if(variable!=null)return variable.LocalIndex;
        return Convert.ToInt32(c.operand);
    }
    private static int TargetIndex(List<CodeInstruction> code,object target)
    {for(int i=0;i<code.Count;++i)if(code[i].labels.Contains((Label)target))return i;return -1;}
    private static LocalBuilder WrongLocal(int index,Type wrong)
    {
        ILGenerator g=new DynamicMethod("WrongLocalType",typeof(void),Type.EmptyTypes).GetILGenerator();
        LocalBuilder local=null;for(int i=0;i<=index;++i)local=g.DeclareLocal(wrong);return local;
    }



    private static int LastStore(List<CodeInstruction> code,int local,int end)
    {for(int i=end-1;i>=0;--i)if(code[i].opcode.Name.StartsWith("stloc")&&LocalIndex(code[i])==local)return i;return -1;}
    public static void MutateUShort(ref ushort value){value=0;}
    public static void MutateInt(ref int value){value=9;}
    private static void InjectAddress(MethodBase method,List<CodeInstruction> original,int at,int local,string reason)
    {
        List<CodeInstruction> changed=Clone(original);
        changed.Insert(at,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[local]));
        Type type=method.GetMethodBody().LocalVariables[local].LocalType;
        changed.Insert(at+1,new CodeInstruction(OpCodes.Call,typeof(StationLaneLoopTests).GetMethod(type==typeof(ushort)?"MutateUShort":"MutateInt")));
        Resolve(method,changed,false,reason);
    }
    private static void Test(Case test)
    {
        MethodBase method=null;
        BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        foreach(MethodInfo m in test.Type.GetMethods(flags))if(m.Name==test.Name&&StationLaneLoopBounds.Handles(m))
        {Check(method==null,"Ambiguous reviewed signature");method=m;}
        Check(method!=null,"Missing target "+test.Type.Name+"."+test.Name);
        List<CodeInstruction> original=Read(method);
        int[] actual=Resolve(method,original,true,"real IL");
        Check(actual.Length==1&&actual[0]==test.Indexes[0],"Wrong real occurrence");
        int bound=actual[0];
        bool path=test.Type==typeof(CableCarPathAI),graph=path||test.Name=="FindTargetSegment";
        bool removal=test.Name=="FindLineNode"||test.Name=="RemoveConnectionLines";
        int condition=TargetIndex(original,original[bound+1].operand),back=condition+(path?8:1);
        int cursor=LocalIndex(original[condition+(path?3:0)]),counter=LocalIndex(original[bound-1]);
        int body=TargetIndex(original,original[back].operand),root=LastStore(original,cursor,body);
        Check(root>=0,"Missing root fixture");
        List<CodeInstruction> code=Clone(original);
        for(int i=code.Count-1;i>=0;--i)
        {CodeInstruction nop=new CodeInstruction(OpCodes.Nop,null);nop.labels.AddRange(code[i].labels);code[i].labels.Clear();code.Insert(i,nop);}
        Resolve(method,code,true,"NOPs including labels");
        code=Clone(original);code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,32768));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        Check(Resolve(method,code,true,"same-valued noise")[0]==bound+2,"Noise chosen");
        code=Clone(original);code[bound].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock,null));
        Resolve(method,code,true,"EH metadata retained");
        code=Clone(original);code[bound-1].opcode=OpCodes.Stloc;code[bound-1].operand=WrongLocal(counter,typeof(int));
        Resolve(method,code,true,"same typed LocalBuilder");
        code=Clone(original);code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4_0,null));
        code.Insert(1,new CodeInstruction(OpCodes.Stloc,WrongLocal(method.GetMethodBody().LocalVariables.Count,typeof(int))));
        Resolve(method,code,true,"unrelated explicitly typed new local");
        code=Clone(original);
        foreach(CodeInstruction instruction in code)
        {
            int argument=instruction.opcode==OpCodes.Ldarg_0?0:instruction.opcode==OpCodes.Ldarg_1?1:instruction.opcode==OpCodes.Ldarg_2?2:instruction.opcode==OpCodes.Ldarg_3?3:-1;
            int parameter=argument-(method.IsStatic?0:1);
            if(argument<0||parameter<0||parameter>=method.GetParameters().Length)continue;
            instruction.opcode=OpCodes.Ldarg;instruction.operand=method.GetParameters()[parameter];
        }
        Resolve(method,code,true,"equivalent ParameterInfo argument operands");
        code=Clone(original);code[bound].operand=32767;
        Resolve(method,code,false,"missing guard");
        code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,32768));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        Resolve(method,code,false,"noise cannot substitute missing guard");
        code=Clone(original);code[bound+1].opcode=OpCodes.Bge;Resolve(method,code,false,"changed predicate");
        code=Clone(original);code.Insert(bound,new CodeInstruction(OpCodes.Conv_U2,null));Resolve(method,code,false,"narrowed counter");
        code=Clone(original);code[bound-1].opcode=OpCodes.Stloc;code[bound-1].operand=WrongLocal(counter,typeof(ushort));
        Resolve(method,code,false,"wrong counter operand type");
        code=Clone(original);code[0].labels.Add((Label)original[bound+1].operand);Resolve(method,code,false,"duplicate condition label");
        code=Clone(original);code[back].opcode=OpCodes.Brfalse;Resolve(method,code,false,"changed back predicate");
        code=Clone(original);code[condition-1].operand=original[back].operand;Resolve(method,code,false,"wrong invalid-list exit");
        code=Clone(original);code[root-1].opcode=OpCodes.Ldc_I4_0;code[root-1].operand=null;Resolve(method,code,false,"wrong root");
        int site=body+(path?10:1);
        InjectAddress(method,original,site,counter,"counter byref mutation");
        InjectAddress(method,original,site,cursor,"cursor byref mutation");
        int pool=-1,element=-1,index=-1;
        if(!graph)
        {
            int anchor=-1;
            for(int i=bound-1;i>=body;--i){FieldInfo f=original[i].operand as FieldInfo;if(original[i].opcode==OpCodes.Ldfld&&f!=null&&(f.Name=="m_nextBuildingNode"||f.Name=="m_nextLaneNode")){anchor=i;break;}}
            Check(anchor>=0,"Missing next field");pool=anchor-4;element=anchor-1;index=anchor-2;
            code=Clone(original);code[anchor].operand=AccessTools.Field(typeof(NetNode),original[anchor].operand.ToString().IndexOf("m_nextLaneNode")>=0?"m_nextBuildingNode":"m_nextLaneNode");
            Resolve(method,code,false,"wrong next-link field");
            if(removal)
            {
                int saved=LocalIndex(original[anchor+1]);
                InjectAddress(method,original,anchor+2,saved,"saved-next byref mutation");
                int previous=LocalIndex(original[bound-8]);
                InjectAddress(method,original,anchor+2,previous,"previous-node byref mutation");
                code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldc_I4_0,null));code.Insert(anchor+3,new CodeInstruction(OpCodes.Stloc,saved));
                Resolve(method,code,false,"saved-next extra write");
                code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldc_I4_0,null));code.Insert(anchor+3,new CodeInstruction(OpCodes.Stloc,previous));
                Resolve(method,code,false,"previous-node extra write");
                code=Clone(original);code[bound-10].opcode=OpCodes.Ldc_I4_0;code[bound-10].operand=null;
                Resolve(method,code,false,"previous/current preservation changed");
            }
        }
        else
        {
            int other=-1;for(int i=body;i<bound;++i){MethodInfo m=original[i].operand as MethodInfo;if(m!=null&&m.DeclaringType==typeof(NetSegment)&&m.Name=="GetOtherNode"){other=i;break;}}
            Check(other>0,"Missing GetOtherNode");pool=other-5;element=other-2;index=other-3;
            int candidate=LocalIndex(original[index]),capture=LastStore(original,candidate,index),get=capture-1,slot=LocalIndex(original[get-1]);
            code=Clone(original);code[other].operand=typeof(TransportLine).GetMethod("GetNextStop",new[]{typeof(ushort)});
            Resolve(method,code,false,"different next-node call");
            code=Clone(original);code[get].operand=AccessTools.Method(typeof(NetNode),"CountSegments",null,null);
            Resolve(method,code,false,"different slot selector");
            InjectAddress(method,original,capture+1,candidate,"candidate segment byref mutation");
            InjectAddress(method,original,capture+1,slot,"slot index byref mutation");
            InjectAddress(method,original,capture+1,LocalIndex(original[other+3]),"previous-segment byref mutation");
            int eight=-1;for(int i=get;i<bound;++i)if(original[i].opcode==OpCodes.Ldc_I4_8){eight=i;break;}
            Check(eight>=0,"Missing slot8 predicate");code=Clone(original);code[eight].opcode=OpCodes.Ldc_I4_7;Resolve(method,code,false,"changed slot limit");
            if(path)
            {
                code=Clone(original);code[condition+6].operand=524288;Resolve(method,code,false,"different continuation flag");
                code=Clone(original);code[body+7].operand=-524289;Resolve(method,code,false,"different cleared flag");
            }
            else
            {
                int foundBranch=bound-7;
                Check(original[foundBranch].opcode==OpCodes.Brtrue,"Missing moved-node gate fixture");
                code=Clone(original);code[foundBranch].opcode=OpCodes.Brfalse;Resolve(method,code,false,"changed found-node gate");
            }
        }
        code=Clone(original);code[pool].operand=AccessTools.Field(typeof(NetManager),graph?"m_nodes":"m_segments");
        Resolve(method,code,false,"wrong node/segment pool");
        code=Clone(original);code[element].operand=typeof(Building);Resolve(method,code,false,"wrong element type");
        code=Clone(original);code[index].opcode=OpCodes.Ldc_I4_0;code[index].operand=null;Resolve(method,code,false,"wrong element cursor");
        code=Clone(original);code.AddRange(Clone(original));Resolve(method,code,false,"duplicate labels/regions");
        List<CodeInstruction> independent=Clone(original);ILGenerator gen=new DynamicMethod("Independent",typeof(void),Type.EmptyTypes).GetILGenerator();for(int i=0;i<=original.Count;++i)gen.DefineLabel();
        Dictionary<Label,Label> remap=new Dictionary<Label,Label>();foreach(CodeInstruction c in independent)foreach(Label l in c.labels)if(!remap.ContainsKey(l))remap.Add(l,gen.DefineLabel());
        foreach(CodeInstruction c in independent){for(int i=0;i<c.labels.Count;++i)c.labels[i]=remap[c.labels[i]];if(c.operand is Label)c.operand=remap[(Label)c.operand];Label[] ts=c.operand as Label[];if(ts!=null){Label[] ns=new Label[ts.Length];for(int i=0;i<ns.Length;++i)ns[i]=remap[ts[i]];c.operand=ns;}}
        code=Clone(original);code.AddRange(independent);Resolve(method,code,false,"independently labelled duplicate region");
        Resolve(method,new List<CodeInstruction>(),false,"empty input");
        code=Clone(original);code.RemoveRange(0,bound-5);Resolve(method,code,false,"truncated input");
        Console.WriteLine("PASS: "+test.Type.Name+"."+test.Name+" root/type/control-flow and atomic rejection");
    }
    public static int Main()
    {
        try
        {
            foreach(Case test in Cases)Test(test);
            Check(!StationLaneLoopBounds.Handles(null),"Null target");
            MethodInfo unknown=typeof(Building).GetMethod("ContainsNode");
            Check(!StationLaneLoopBounds.Handles(unknown)&&StationLaneLoopBounds.Resolve(unknown,Read(unknown))==null,"Unknown target");
            Console.WriteLine("PASS:14 actual methods/14 guards; metadata/read-only/strict positive and negative fixtures");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
