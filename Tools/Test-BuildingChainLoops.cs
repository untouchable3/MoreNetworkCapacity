using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

internal static class BuildingChainLoopTests
{
    private sealed class Case
    {
        internal Type Type; internal string Name; internal int[] Indexes;
        internal Case(Type type, string name, params int[] indexes) { Type=type;Name=name;Indexes=indexes; }
    }
    private static Case[] Cases = new Case[]
    {
        new Case(typeof(Building),"ContainsNode",29), new Case(typeof(Building),"ContainsSegment",47),
        new Case(typeof(Building),"FindAccessLane",199),new Case(typeof(Building),"FindLane",148),
        new Case(typeof(Building),"FindNode",79),new Case(typeof(Building),"FindParentNode",94),new Case(typeof(Building),"FindSegment",80),
        new Case(typeof(BuildingDecoration),"ClearDecorations",22,43),new Case(typeof(BuildingDecoration),"GetBuildingSegments",57),
        new Case(typeof(BuildingDecoration),"SavePaths",420,583),new Case(typeof(BuildingManager),"FindNodeOwner",102),
        new Case(typeof(BuildingManager),"IgnoreOverlap",62),new Case(typeof(BuildingManager),"ReleasePaths",56,176),
        new Case(typeof(BuildingManager),"UpdateFlags",87),new Case(typeof(BuildingTool),"IgnoreRelocateSegments",78,139),
        new Case(typeof(DefaultTool),"RenderOverlay",471,624),new Case(typeof(TransportLine),"ReleaseNode",58)
    };
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static List<CodeInstruction> Clone(List<CodeInstruction> code)
    {List<CodeInstruction> copy=new List<CodeInstruction>();foreach(CodeInstruction c in code)copy.Add(new CodeInstruction(c));return copy;}
    private static List<CodeInstruction> Read(MethodBase method)
    {return PatchProcessor.GetOriginalInstructions(method,new DynamicMethod("BuildingLabels",typeof(void),Type.EmptyTypes).GetILGenerator());}
    private static int[] Resolve(MethodBase method,List<CodeInstruction> code,bool accept,string message)
    {
        List<CodeInstruction> before=Clone(code);
        int[] result=BuildingChainLoopBounds.Resolve(method,code);
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
    private static void Test(Case test)
    {
        MethodBase method=null;
        BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        foreach(MethodInfo m in test.Type.GetMethods(flags))if(m.Name==test.Name&&BuildingChainLoopBounds.Handles(m))
        {Check(method==null,"Ambiguous reviewed signature");method=m;}
        Check(method!=null,"Missing target "+test.Name);
        List<CodeInstruction> original=Read(method);
        int[] actual=Resolve(method,original,true,"real IL");
        Check(actual.Length==test.Indexes.Length,"Wrong occurrence count");
        for(int i=0;i<actual.Length;++i)Check(actual[i]==test.Indexes[i],"Wrong actual occurrence");
        List<CodeInstruction> code=Clone(original);
        for(int i=code.Count-1;i>=0;--i)
        {
            CodeInstruction nop=new CodeInstruction(OpCodes.Nop,null);nop.labels.AddRange(code[i].labels);code[i].labels.Clear();code.Insert(i,nop);
        }
        Resolve(method,code,true,"NOP padding with moved labels");
        code=Clone(original);
        code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,32768));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
        code.Insert(2,new CodeInstruction(OpCodes.Ldc_I4,36864));code.Insert(3,new CodeInstruction(OpCodes.Pop,null));
        int[] moved=Resolve(method,code,true,"same-valued noise");
        for(int i=0;i<moved.Length;++i)Check(moved[i]==actual[i]+4,"Noise selected");
        bool array=method.DeclaringType==typeof(BuildingDecoration)&&(method.Name=="ClearDecorations"||method.Name=="SavePaths");
        foreach(int bound in actual)
        {
            code=Clone(original);code[bound].operand=(int)code[bound].operand-1;
            Resolve(method,code,false,"missing one bound rejects whole method");
            code.Insert(0,new CodeInstruction(OpCodes.Ldc_I4,original[bound].operand));code.Insert(1,new CodeInstruction(OpCodes.Pop,null));
            Resolve(method,code,false,"noise cannot replace missing traversal");
            code=Clone(original);code[bound+1].opcode=OpCodes.Bge;
            Resolve(method,code,false,"changed predicate");
            code=Clone(original);code.Insert(bound,new CodeInstruction(OpCodes.Conv_U2,null));
            Resolve(method,code,false,"narrowed counter/index");
            code=Clone(original);code[0].labels.Add((Label)code[bound+1].operand);
            Resolve(method,code,false,"duplicate bound target");
            int anchor=FieldAnchor(original,bound,array);Check(anchor>=0,"Missing typed provenance fixture");
            int branchTarget=TargetIndex(original,original[bound+1].operand);
            int body=array?branchTarget:TargetIndex(original,original[branchTarget+1].operand);
            int counter=LocalIndex(original[bound-1]);
            code=Clone(original);
            code[bound-1].opcode=array?OpCodes.Ldloc:OpCodes.Stloc;
            code[bound-1].operand=WrongLocal(counter,typeof(ushort));
            Resolve(method,code,false,"typed local operand disagrees with original Int32 metadata");
            code=Clone(original);
            code[bound-1].opcode=array?OpCodes.Ldloc:OpCodes.Stloc;
            code[bound-1].operand=WrongLocal(counter,typeof(int));
            Resolve(method,code,true,"equivalent correctly typed LocalBuilder operand");
            int addressSite=body+(array?10:1);
            code=Clone(original);code.Insert(addressSite,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[counter]));
            code.Insert(addressSite+1,new CodeInstruction(OpCodes.Pop,null));
            Resolve(method,code,false,"counter/index address escape inside body");
            if(!array)
            {
                int cursor=LocalIndex(original[branchTarget]);
                code=Clone(original);code.Insert(addressSite,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[cursor]));
                code.Insert(addressSite+1,new CodeInstruction(OpCodes.Pop,null));
                Resolve(method,code,false,"cursor address escape inside body");
                int root=-1;
                for(int i=body-1;i>=0;--i)
                {
                    if(original[i].opcode.Name.StartsWith("stloc")&&LocalIndex(original[i])==cursor){root=i;break;}
                }
                Check(root>0,"Missing reviewed cursor root");
                code=Clone(original);code[root-1].opcode=OpCodes.Ldc_I4_0;code[root-1].operand=null;
                Resolve(method,code,false,"cursor root substituted");
                // For cached-next variants, the source read is followed by its
                // own local store, not the loop cursor store at bound-6.
                if(anchor+1!=bound-6)
                {
                    int saved=LocalIndex(original[anchor+1]);
                    code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldloca,method.GetMethodBody().LocalVariables[saved]));
                    code.Insert(anchor+3,new CodeInstruction(OpCodes.Pop,null));
                    Resolve(method,code,false,"saved-next address escape");
                    code=Clone(original);code.Insert(anchor+2,new CodeInstruction(OpCodes.Ldc_I4_0,null));
                    code.Insert(anchor+3,new CodeInstruction(OpCodes.Stloc,saved));
                    Resolve(method,code,false,"saved-next additional write");
                }
            }
            code=Clone(original);code[anchor].operand=AccessTools.Field(typeof(NetNode),"m_building");
            Resolve(method,code,false,"changed link/flags field");
            code=Clone(original);code[anchor-4].operand=AccessTools.Field(typeof(NetManager),"m_buildings");
            Resolve(method,code,false,"changed pool source");
            code=Clone(original);code[anchor-3].operand=AccessTools.Field(typeof(Array16<NetSegment>),"m_buffer");
            if(((FieldInfo)original[anchor-3].operand).DeclaringType==typeof(Array16<NetSegment>))
                code[anchor-3].operand=AccessTools.Field(typeof(Array16<NetNode>),"m_buffer");
            Resolve(method,code,false,"changed backing array");
            code=Clone(original);code[anchor-2].opcode=OpCodes.Ldc_I4_0;code[anchor-2].operand=null;
            Resolve(method,code,false,"changed cursor/index");
            code=Clone(original);code[anchor-1].operand=typeof(Building);
            Resolve(method,code,false,"changed element type");
            code=Clone(original);code[bound].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock,null));
            Resolve(method,code,true,"EH metadata retained");
        }
        code=Clone(original);code.AddRange(Clone(original));
        Resolve(method,code,false,"duplicate traversals");
        List<CodeInstruction> independent=Clone(original);
        ILGenerator labels=new DynamicMethod("IndependentBuildingLabels",typeof(void),Type.EmptyTypes).GetILGenerator();
        for(int i=0;i<=original.Count;++i)labels.DefineLabel();
        Dictionary<Label,Label> remap=new Dictionary<Label,Label>();
        foreach(CodeInstruction c in independent)foreach(Label label in c.labels)if(!remap.ContainsKey(label))remap.Add(label,labels.DefineLabel());
        foreach(CodeInstruction c in independent)
        {
            for(int i=0;i<c.labels.Count;++i)c.labels[i]=remap[c.labels[i]];
            if(c.operand is Label)c.operand=remap[(Label)c.operand];
            Label[] targets=c.operand as Label[];
            if(targets!=null){Label[] changed=new Label[targets.Length];for(int i=0;i<targets.Length;++i)changed[i]=remap[targets[i]];c.operand=changed;}
        }
        code=Clone(original);code.AddRange(independent);
        Resolve(method,code,false,"multiple independently labelled traversals");
        code=new List<CodeInstruction>();Resolve(method,code,false,"empty body");
        code=Clone(original);code.RemoveRange(0,test.Indexes[test.Indexes.Length-1]-5);
        Resolve(method,code,false,"truncated traversal");
        Console.WriteLine("PASS: "+test.Type.Name+"."+test.Name+" "+actual.Length+" reviewed bounds");
    }
    public static int Main()
    {
        try
        {
            int count=0;
            foreach(Case test in Cases){Test(test);count+=test.Indexes.Length;}
            Check(!BuildingChainLoopBounds.Handles(null),"Null target");
            MethodInfo unrelated=typeof(BuildingTool).GetMethod("GetAllZoneBlocks",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static);
            Check(unrelated!=null&&!BuildingChainLoopBounds.Handles(unrelated),"Wrong target handled");
            Check(BuildingChainLoopBounds.Resolve(unrelated,Read(unrelated))==null,"Wrong target resolved");
            Check(BuildingChainLoopBounds.Resolve(Cases[0].Type.GetMethod(Cases[0].Name),null)==null,"Null input");
            Console.WriteLine("PASS: "+Cases.Length+" actual methods, "+count+" bounds; read-only/atomic positive and negative cases");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
