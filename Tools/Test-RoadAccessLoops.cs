using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class RoadAccessLoopTests
{
    private sealed class Case
    {
        internal Type Type; internal string Name; internal int[] Indexes;
        internal Case(Type type, string name, params int[] indexes) { Type=type;Name=name;Indexes=indexes; }
    }
    private static Case[] Cases = new Case[]
    {
        new Case(typeof(AirportAuxBuildingAI),"FindRoadAccess",119,430),
        new Case(typeof(AirportCargoGateAI),"FindRoadAccess",119,425),
        new Case(typeof(AirportGateAI),"FindRoadAccess",119,425),
        new Case(typeof(BuildingTool),"SnapToCanal",282),
        new Case(typeof(BuildingTool),"SnapToPath",263),
        new Case(typeof(CampusBuildingAI),"FindRoadAccess",119,425),
        new Case(typeof(CanalAI),"CheckBuildPosition",722),
        new Case(typeof(CommonBuildingAI),"BuildingCollapsed",142),
        new Case(typeof(CommonBuildingAI),"FindEventAccess",99),
        new Case(typeof(CommonBuildingAI),"FindRoadAccess",99),
        new Case(typeof(MuseumAI),"FindRoadAccess",121,427),
        new Case(typeof(ParkBuildingAI),"FindRoadAccess",119,425),
        new Case(typeof(PlayerBuildingAI),"FindRoadAccess",97),
        new Case(typeof(RaceBuildingAI),"CheckRoadAccess",187),
        new Case(typeof(RaceBuildingAI),"FindRoadAccess",97),
        new Case(typeof(RaceEndBuildingAI),"CreateBuilding",69),
        new Case(typeof(RaceStandAI),"FindEventRoad",232),
    };
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    {List<CodeInstruction> copy=new List<CodeInstruction>();foreach(CodeInstruction c in code)copy.Add(new CodeInstruction(c));return copy;}
    private static List<CodeInstruction> Read(MethodBase method)
    {return PatchProcessor.GetOriginalInstructions(method,new DynamicMethod("BuildingLabels",typeof(void),Type.EmptyTypes).GetILGenerator());}
    private static int[] Resolve(MethodBase method,List<CodeInstruction> code,bool accept,string message)
    {
        List<CodeInstruction> before=Clone(code);
        int[] result=RoadAccessLoopBounds.Resolve(method,code);
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

    private static bool IsHead(MethodBase method)
    {
        return (method.DeclaringType==typeof(CommonBuildingAI)&&(method.Name=="FindRoadAccess"||method.Name=="FindEventAccess"))
            || (method.DeclaringType==typeof(PlayerBuildingAI)&&method.Name=="FindRoadAccess")
            || (method.DeclaringType==typeof(RaceBuildingAI)&&method.Name=="FindRoadAccess");
    }
    private static int LastStore(List<CodeInstruction> code,int local,int before)
    {
        for(int i=before-1;i>=0;--i)if(code[i].opcode.Name.StartsWith("stloc")&&LocalIndex(code[i])==local)return i;
        return -1;
    }
    private static int SourcePool(List<CodeInstruction> code,int anchor,MethodBase method)
    {
        if(method.DeclaringType==typeof(RaceEndBuildingAI)||method.DeclaringType==typeof(RaceStandAI))
        {
            int array=LocalIndex(code[anchor-3]);return LastStore(code,array,anchor-3)-2;
        }
        if(method.DeclaringType==typeof(BuildingTool))
        {
            int pool=LocalIndex(code[anchor-4]);return LastStore(code,pool,anchor-4)-1;
        }
        return anchor-4;
    }
    private static void Test(Case test)
    {
        MethodBase method=null;
        BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        foreach(MethodInfo m in test.Type.GetMethods(flags))if(m.Name==test.Name&&RoadAccessLoopBounds.Handles(m))
        {Check(method==null,"Ambiguous reviewed signature");method=m;}
        Check(method!=null,"Missing target "+test.Type.Name+"."+test.Name);
        List<CodeInstruction> original=Read(method);
        int[] actual=Resolve(method,original,true,"real IL");
        Check(actual.Length==test.Indexes.Length,"Wrong count");
        for(int i=0;i<actual.Length;++i)Check(actual[i]==test.Indexes[i],"Wrong real occurrence");
        List<CodeInstruction> code=Clone(original);
        for(int i=code.Count-1;i>=0;--i)
        {CodeInstruction nop=new CodeInstruction(OpCodes.Nop,null);nop.labels.AddRange(code[i].labels);code[i].labels.Clear();code.Insert(i,nop);}
        Resolve(method,code,true,"NOPs including labelled padding");
        code=Clone(original);code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,32768));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        code.Insert(2,new CodeInstruction(OpCodes.Ldc_I4,36864));code.Insert(3,new CodeInstruction(OpCodes.Pop,null));
        int[] moved=Resolve(method,code,true,"same-valued noise");
        for(int i=0;i<moved.Length;++i)Check(moved[i]==actual[i]+4,"Noise chosen as bound");
        code=Clone(original);code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4_0,null));
        code.Insert(1,new CodeInstruction(OpCodes.Stloc,WrongLocal(method.GetMethodBody().LocalVariables.Count,typeof(int))));
        Resolve(method,code,true,"unrelated newly declared typed local");
        code=Clone(original);code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4_0,null));
        code.Insert(1,new CodeInstruction(OpCodes.Stloc,method.GetMethodBody().LocalVariables.Count));
        Resolve(method,code,false,"unknown new numeric local without type evidence");
        foreach(int bound in actual)
        {
            bool head=IsHead(method);
            int body,condition,counter,cursor;
            if(head)
            {
                body=TargetIndex(original,original[bound+1].operand);
                int exit=TargetIndex(original,original[body-1].operand);
                condition=exit-2;counter=LocalIndex(original[bound-1]);cursor=LocalIndex(original[condition]);
            }
            else
            {condition=TargetIndex(original,original[bound+1].operand);body=TargetIndex(original,original[condition+1].operand);counter=LocalIndex(original[bound-1]);cursor=LocalIndex(original[condition]);}
            int update=condition-1;
            int anchor=-1;
            for(int i=update-1;i>=body;--i)
            {
                FieldInfo f=original[i].operand as FieldInfo;
                if(original[i].opcode==OpCodes.Ldfld&&f!=null&&(f.Name=="m_nextBuildingNode"||f.Name=="m_nextGridSegment")) {anchor=i;break;}
            }
            Check(anchor>=0,"Missing source anchor");
            code=Clone(original);code[bound].operand=(int)original[bound].operand-1;
            Resolve(method,code,false,"one missing bound rejects entire method");
            code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,original[bound].operand));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
            Resolve(method,code,false,"noise cannot stand in for missing bound");
            code=Clone(original);code[bound+1].opcode=OpCodes.Bge;
            Resolve(method,code,false,"changed predicate");
            code=Clone(original);
            code[bound-4].opcode=head?OpCodes.Ldc_I4_1:OpCodes.Dup;code[bound-4].operand=null;
            code[bound-3].opcode=head?OpCodes.Add:OpCodes.Ldc_I4_1;code[bound-3].operand=null;
            code[bound-2].opcode=head?OpCodes.Dup:OpCodes.Add;code[bound-2].operand=null;
            Resolve(method,code,false,"postincrement/preincrement semantics cannot be interchanged");
            code=Clone(original);code.Insert(bound,new CodeInstruction(OpCodes.Conv_U2,null));
            Resolve(method,code,false,"narrowed counter");
            code=Clone(original);code[0].labels.Add((Label)original[bound+1].operand);
            Resolve(method,code,false,"duplicate condition label");
            code=Clone(original);code[bound-1].opcode=OpCodes.Stloc;code[bound-1].operand=WrongLocal(counter,typeof(ushort));
            Resolve(method,code,false,"wrong typed counter operand");
            code=Clone(original);code[bound-1].opcode=OpCodes.Stloc;code[bound-1].operand=WrongLocal(counter,typeof(int));
            Resolve(method,code,true,"same typed counter operand");
            code=Clone(original);code.Insert(body+1,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[counter]));
            code.Insert(body+2,new CodeInstruction(OpCodes.Pop,null));Resolve(method,code,false,"counter address escapes");
            code=Clone(original);code.Insert(body+1,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[cursor]));
            code.Insert(body+2,new CodeInstruction(OpCodes.Pop,null));Resolve(method,code,false,"cursor address escapes");
            int root=LastStore(original,cursor,head?bound-5:body);
            Check(root>0,"Missing cursor root");
            code=Clone(original);code[root-1].opcode=OpCodes.Ldc_I4_0;code[root-1].operand=null;
            Resolve(method,code,false,"root substituted");
            if(original[root-1].opcode==OpCodes.Ldelem_U2)
            {
                code=Clone(original);code[root-5].operand=271;
                Resolve(method,code,false,"changed grid stride");
                int grid=root-7;
                if(method.DeclaringType==typeof(BuildingTool))
                {
                    int gridLocal=LocalIndex(original[grid]);grid=LastStore(original,gridLocal,grid)-1;
                    code=Clone(original);code.Add(new CodeInstruction(OpCodes.Ldnull,null));code.Add(new CodeInstruction(OpCodes.Stloc,gridLocal));
                    Resolve(method,code,false,"cached grid reassignment outside textual loop");
                    code=Clone(original);code.Add(new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[gridLocal]));code.Add(new CodeInstruction(OpCodes.Pop,null));
                    Resolve(method,code,false,"cached grid address escaped outside loop");
                }
                code=Clone(original);code[grid].operand=AccessTools.Field(typeof(NetManager),"m_nodeGrid");
                Resolve(method,code,false,"wrong grid field with same array type");
            }
            code=Clone(original);code[anchor].operand=AccessTools.Field(typeof(NetNode),"m_building");
            Resolve(method,code,false,"changed next-link field");
            code=Clone(original);code[anchor-1].operand=typeof(Building);
            Resolve(method,code,false,"changed element type");
            code=Clone(original);code[anchor-2].opcode=OpCodes.Ldc_I4_0;code[anchor-2].operand=null;
            Resolve(method,code,false,"changed cursor read");
            int pool=SourcePool(original,anchor,method);Check(pool>=0,"Missing pool fixture");
            code=Clone(original);code[pool].operand=AccessTools.Field(typeof(BuildingManager),"m_buildings");
            Resolve(method,code,false,"different pool source");
            bool raw=method.DeclaringType==typeof(RaceEndBuildingAI)||method.DeclaringType==typeof(RaceStandAI);
            int buffer=raw?pool+1:anchor-3;
            code=Clone(original);FieldInfo bufferField=(FieldInfo)original[buffer].operand;
            code[buffer].operand=AccessTools.Field(bufferField.DeclaringType==typeof(Array16<NetNode>)?typeof(Array16<NetSegment>):typeof(Array16<NetNode>),"m_buffer");
            Resolve(method,code,false,"different backing array");
            if(raw||method.DeclaringType==typeof(BuildingTool))
            {
                int cached=LocalIndex(original[raw?anchor-3:anchor-4]);
                code=Clone(original);code.Add(new CodeInstruction(OpCodes.Ldnull,null));code.Add(new CodeInstruction(OpCodes.Stloc,cached));
                Resolve(method,code,false,"cached pool/array reassignment outside loop");
                code=Clone(original);code.Add(new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[cached]));code.Add(new CodeInstruction(OpCodes.Pop,null));
                Resolve(method,code,false,"cached pool/array address escape outside loop");
            }
            if(method.DeclaringType==typeof(CommonBuildingAI)&&method.Name=="BuildingCollapsed")
            {
                int saved=LocalIndex(original[anchor+1]);
                code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[saved]));
                code.Insert(anchor+3,new CodeInstruction(OpCodes.Pop,null));Resolve(method,code,false,"cached-next address escape");
                code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldc_I4_0,null));
                code.Insert(anchor+3,new CodeInstruction(OpCodes.Stloc,saved));Resolve(method,code,false,"cached-next overwritten");
            }
            code=Clone(original);code[bound].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock,null));
            Resolve(method,code,true,"EH metadata retained");
        }
        code=Clone(original);code.AddRange(Clone(original));Resolve(method,code,false,"duplicated labels/loops");
        List<CodeInstruction> independent=Clone(original);
        ILGenerator labels=new DynamicMethod("RoadLabels",typeof(void),Type.EmptyTypes).GetILGenerator();
        for(int i=0;i<=original.Count;++i)labels.DefineLabel();
        Dictionary<Label,Label> remap=new Dictionary<Label,Label>();
        foreach(CodeInstruction c in independent)foreach(Label label in c.labels)if(!remap.ContainsKey(label))remap.Add(label,labels.DefineLabel());
        foreach(CodeInstruction c in independent)
        {for(int i=0;i<c.labels.Count;++i)c.labels[i]=remap[c.labels[i]];if(c.operand is Label)c.operand=remap[(Label)c.operand];Label[] targets=c.operand as Label[];if(targets!=null){Label[] changed=new Label[targets.Length];for(int i=0;i<targets.Length;++i)changed[i]=remap[targets[i]];c.operand=changed;}}
        code=Clone(original);code.AddRange(independent);Resolve(method,code,false,"independent duplicate traversals");
        Resolve(method,new List<CodeInstruction>(),false,"empty body");
        code=Clone(original);code.RemoveRange(0,test.Indexes[test.Indexes.Length-1]-5);Resolve(method,code,false,"truncated body");
        Console.WriteLine("PASS: "+test.Type.Name+"."+test.Name+" "+actual.Length+" reviewed bounds");
    }
    public static int Main()
    {
        try
        {
            int count=0;foreach(Case test in Cases){Test(test);count+=test.Indexes.Length;}
            Check(!RoadAccessLoopBounds.Handles(null),"Null target");
            MethodInfo unrelated=typeof(Building).GetMethod("ContainsNode");
            Check(unrelated!=null&&!RoadAccessLoopBounds.Handles(unrelated),"Unrelated method accepted");
            Check(RoadAccessLoopBounds.Resolve(unrelated,Read(unrelated))==null,"Unrelated method resolved");
            Console.WriteLine("PASS: "+Cases.Length+" real methods, "+count+" guards; strict positive/negative/read-only tests");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
