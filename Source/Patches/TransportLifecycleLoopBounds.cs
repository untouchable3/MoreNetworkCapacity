using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ColossalFramework;

namespace MoreNetworkCapacity.Patches
{
    internal static class TransportLifecycleLoopBounds
    {
        private sealed class Target
        {
            internal string Type, Name, Signature, Result; internal bool Static;
            internal Target(string type, string name, string signature, string result, bool isStatic)
            { Type = type; Name = name; Signature = signature; Result = result; Static = isStatic; }
        }
        private static readonly Target[] Targets = new[] {
            new Target("BusAI", "FindOutgoingConnectionStop", "System.UInt16,Vehicle&", "System.UInt16", false),
            new Target("PathVisualizer", "AddPaths", "InstanceID,System.Int32,System.Int32", "System.Void", false),
            new Target("PublicTransportWorldInfoPanel", "OnSetTarget", "", "System.Void", false),
            new Target("TransportLine", "ClearStops", "System.UInt16", "System.Void", false),
            new Target("TransportLine", "CloneLine", "System.UInt16,System.UInt16", "System.Void", false),
            new Target("TransportLine", "GetLastStop", "", "System.UInt16", false),
            new Target("TransportLine", "UpdateMeshData", "System.UInt16", "System.Boolean", false),
            new Target("TransportManager", "RayCast", "UnityEngine.Ray,System.Single,System.Int32,UnityEngine.Vector3&,System.UInt16&,System.Int32&,System.Int32&", "System.Boolean", false),
            new Target("TransportManager", "ReleaseLineImplementation", "System.UInt16,TransportLine&", "System.Void", false),
            new Target("TransportManager+Data", "AfterDeserialize", "ColossalFramework.IO.DataSerializer", "System.Void", false),
            new Target("TransportTool", "FindBuildingLine", "System.UInt16", "System.UInt16", true),
            new Target("TransportTool", "GetLineCount", "UnityEngine.Vector3,UnityEngine.Vector3,TransportInfo+TransportType", "System.Int32", false),
            new Target("TransportTool", "GetStopPosition", "TransportInfo,System.UInt16,System.UInt16,System.UInt16,UnityEngine.Vector3&,System.Boolean&", "System.Boolean", false),
        };
        internal static bool Handles(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            if (method == null || method.DeclaringType == null || method.DeclaringType.Assembly != typeof(NetManager).Assembly) return false;
            string signature = "";
            foreach (ParameterInfo p in method.GetParameters()) signature += (signature.Length == 0 ? "" : ",") + p.ParameterType.FullName;
            foreach (Target target in Targets)
                if (method.DeclaringType.FullName == target.Type && method.Name == target.Name && method.IsStatic == target.Static
                    && method.ReturnType.FullName == target.Result && signature == target.Signature) return true;
            return false;
        }
        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null || !TypedLocals(original, code)) return null;
            Dictionary<string,int> found = new Dictionary<string,int>();
            foreach (int bound in Constants(code))
            {
                string role = Guard(original, code, bound);
                if (role == null) continue;
                if (found.ContainsKey(role)) return null;
                found.Add(role, bound);
            }
            string[] expected = original.Name == "UpdateMeshData" ? new[] { "mesh-count", "mesh-fill" } : new[] { "chain" };
            if (found.Count != expected.Length) return null;
            List<int> result = new List<int>();
            foreach(string role in expected) { int value; if (!found.TryGetValue(role,out value)) return null; result.Add(value); }
            result.Sort(); return result.ToArray();
        }
        private static IEnumerable<int> Constants(List<CodeInstruction> code)
        {
            for(int i = 0; i < code.Count; ++i) if(code[i].opcode == OpCodes.Ldc_I4 && object.Equals(code[i].operand,32768)) yield return i;
        }
        private sealed class Loop
        {
            internal int Start, Condition, Back, Exit, Counter, Cursor, Load, Zero;
        }
        private static string Guard(MethodBase original, List<CodeInstruction> code, int bound)
        {
            string name = original.Name;
            bool flags = name == "ClearStops" || name == "ReleaseLineImplementation";
            bool ui = name == "OnSetTarget", last = name == "GetLastStop";
            int branch = Next(code,bound);
            bool inclusive = name == "FindBuildingLine" || name == "FindOutgoingConnectionStop" || name == "AddPaths";
            if(branch < 0 || !Branch(code[branch].opcode,inclusive ? OpCodes.Ble : OpCodes.Blt)) return null;
            int condition = Skip(code,LabelIndex(code,code[branch].operand));
            if(condition <= branch) return null;
            int test = Local(code[condition],false), back = Next(code,condition), exit, start;
            if(test < 0 || back < 0) return null;
            if(flags || ui)
            {
                if(!Branch(code[back].opcode,OpCodes.Brfalse)) return null;
                exit = Skip(code,LabelIndex(code,code[back].operand));
                if(ui)
                {
                    int check = Next(code,back), array = Next(code,check), length = Next(code,array), conv = Next(code,length), jump = Next(code,conv);
                    if(jump < 0 || code[length].opcode != OpCodes.Ldlen || code[conv].opcode != OpCodes.Conv_I4
                        || !Branch(code[jump].opcode,OpCodes.Blt)) return null;
                    start = Skip(code,LabelIndex(code,code[jump].operand)); back = jump;
                    if(exit != Next(code,jump)) return null;
                }
                else
                {
                    // Flags are evaluated on the exact current node before the body.
                    int field = NextN(code,back,6), three = Next(code,field), and = Next(code,three), one = Next(code,and), compare = Next(code,one);
                    if(compare < 0 || !Field(code[field],typeof(NetNode),"m_flags") || code[three].opcode != OpCodes.Ldc_I4_3
                        || code[and].opcode != OpCodes.And || code[one].opcode != OpCodes.Ldc_I4_1
                        || !Element(original,code,Previous(code,field),test,typeof(NetNode),condition,exit)) return null;
                    if(name == "ClearStops")
                    { if(!Branch(code[compare].opcode,OpCodes.Beq)) return null; back = compare; }
                    else
                    {
                        if(!Branch(code[compare].opcode,OpCodes.Bne_Un) || Skip(code,LabelIndex(code,code[compare].operand)) != exit) return null;
                        int line = NextN(code,compare,6), argument = Next(code,line), equal = Next(code,argument);
                        if(equal < 0 || !Field(code[line],typeof(NetNode),"m_transportLine") || Argument(original,code[argument]) != 1
                            || !Element(original,code,Previous(code,line),test,typeof(NetNode),condition,exit)
                            || !Branch(code[equal].opcode,OpCodes.Beq)) return null;
                        back = equal;
                    }
                    start = Skip(code,LabelIndex(code,code[back].operand));
                    if(Next(code,back) != exit) return null;
                }
            }
            else
            {
                if(!Branch(code[back].opcode,OpCodes.Brtrue)) return null;
                start = Skip(code,LabelIndex(code,code[back].operand)); exit = Next(code,back);
            }
            int store = Previous(code,bound), dup = Previous(code,store), add = Previous(code,dup), oneIncrement = Previous(code,add), load = Previous(code,oneIncrement);
            int counter = store < 0 ? -1 : Local(code[store],true);
            if(start < 0 || load < start || counter < 0 || !TypeIs(original,code[store],counter,typeof(int))
                || code[dup].opcode != OpCodes.Dup || code[add].opcode != OpCodes.Add || code[oneIncrement].opcode != OpCodes.Ldc_I4_1
                || Local(code[load],false) != counter || !Handler(code,branch,condition,exit)) return null;
            int zero, initialStore;
            if(last) { initialStore = Previous(code,start); zero = Previous(code,initialStore); }
            else
            {
                int initialJump = Previous(code,start); initialStore = Previous(code,initialJump); zero = Previous(code,initialStore);
                if(initialJump < 0 || !Branch(code[initialJump].opcode,OpCodes.Br)
                    || Skip(code,LabelIndex(code,code[initialJump].operand)) != condition) return null;
            }
            if(zero < 0 || code[zero].opcode != OpCodes.Ldc_I4_0 || Local(code[initialStore],true) != counter) return null;
            Loop loop = new Loop { Start=start,Condition=condition,Back=back,Exit=exit,Counter=counter,Cursor=last ? -1 : test,Load=load,Zero=zero };
            if(ui)
            {
                int check = NextN(code,condition,2), array = Next(code,check);
                int button = array < 0 ? -1 : Local(code[array],false);
                if(Local(code[check],false) != counter || !TypeIs(original,code[array],button,typeof(ColossalFramework.UI.UIButton[]))
                    || !UiButtons(original,code,button,array,loop.Back+1)) return null;
            }
            string role = "chain";
            if(last)
            {
                if(!LastStop(original,code,loop,test)) return null;
            }
            else if(name == "CloneLine" || name == "ClearStops" || name == "ReleaseLineImplementation")
            { if(!NextAdvance(original,code,loop,true)) return null; }
            else if(name == "RayCast" || name == "GetStopPosition")
            { if(!NextAdvance(original,code,loop,false)) return null; }
            else if(name == "UpdateMeshData" || ui)
            {
                int update = LastStore(code,loop.Cursor,load);
                if(!SegmentAdvance(original,code,loop,update,name == "UpdateMeshData")) return null;
                if(name == "UpdateMeshData")
                {
                    bool count = ContainsField(code,start,load,"TransportLine+TempUpdateMeshData","m_pathSegmentCount",OpCodes.Stfld);
                    bool fill = ContainsField(code,start,load,"TransportLine+TempUpdateMeshData","m_pathSegmentIndex",OpCodes.Stfld);
                    if(count == fill) return null;
                    role = count ? "mesh-count" : "mesh-fill";
                }
            }
            else if(name == "AfterDeserialize") { if(!DeserializeAdvance(original,code,loop)) return null; }
            else
            {
                int update = Previous(code,load);
                if(Local(code[update],true) != loop.Cursor || !Link(original,code,update,loop.Cursor,
                    name == "GetLineCount" ? "m_nextGridNode" : "m_nextBuildingNode",loop.Start,loop.Load)) return null;
                if(!OnlyWrites(code,loop.Cursor,start,load,new[]{ update })) return null;
            }
            if(loop.Cursor < 0 || loop.Cursor == counter || !TypeIs(original,code[condition],loop.Cursor,typeof(ushort)) && !last) return null;
            if(!Seed(original,code,loop,name)) return null;
            if(!OnlyWrites(code,counter,start,back,new[]{ store })) return null;
            if(!NoAddress(code,counter,start,back) || !NoAddress(code,loop.Cursor,start,back)) return null;
            return role;
        }
        private static bool Handler(List<CodeInstruction> code,int branch,int condition,int exit)
        {
            int i = Next(code,branch); if(i < 0 || code[i].opcode != OpCodes.Ldc_I4_1) return false;
            i=Next(code,i); if(i<0 || code[i].opcode != OpCodes.Ldstr || !object.Equals(code[i].operand,"Invalid list detected!\n")) return false;
            i=Next(code,i); if(!Call(code,i,typeof(Environment),"get_StackTrace")) return false;
            i=Next(code,i); MethodInfo concat=i<0?null:code[i].operand as MethodInfo;
            if(!Call(code,i,typeof(string),"Concat") || concat.ReturnType != typeof(string) || concat.GetParameters().Length!=2) return false;
            i=Next(code,i); MethodInfo error=i<0?null:code[i].operand as MethodInfo;
            if(i<0 || code[i].opcode != OpCodes.Call || error==null || error.DeclaringType != typeof(CODebugBase<LogChannel>) || error.Name!="Error") return false;
            i=Next(code,i); return i>=0 && Branch(code[i].opcode,OpCodes.Br) && Next(code,i)==condition && Skip(code,LabelIndex(code,code[i].operand))==exit;
        }
        private static bool UiButtons(MethodBase original,List<CodeInstruction> code,int button,int use,int end)
        {
            int init=LastStore(code,button,use), setter=Previous(code,init), count=Previous(code,setter), field=Previous(code,count), self=Previous(code,field);
            MethodInfo set=setter<0?null:code[setter].operand as MethodInfo;
            FieldInfo list=field<0?null:code[field].operand as FieldInfo;
            int countLocal=count<0?-1:Local(code[count],false);
            if(self<0 || set==null || code[setter].opcode!=OpCodes.Callvirt || set.IsStatic || set.Name!="SetItemCount"
                || set.ReturnType!=typeof(ColossalFramework.UI.UIButton[]) || set.GetParameters().Length!=1
                || set.GetParameters()[0].ParameterType!=typeof(int) || !Field(code[field],typeof(PublicTransportWorldInfoPanel),"m_stopButtons")
                || set.DeclaringType!=list.FieldType || Argument(original,code[self])!=0 || countLocal<0
                || !TypeIs(original,code[count],countLocal,typeof(int)) || !Stable(code,button,init+1,end)) return false;
            int countInit=LastStore(code,countLocal,count), call=Previous(code,countInit), line=Previous(code,call), element=Previous(code,line);
            int index=Previous(code,element), buffer=Previous(code,index), pool=Previous(code,buffer), manager=Previous(code,pool);
            MethodInfo stops=call<0?null:code[call].operand as MethodInfo;
            int lineID=line<0?-1:Local(code[line],false);
            return manager>=0 && stops!=null && code[call].opcode==OpCodes.Call && stops.DeclaringType==typeof(TransportLine)
                && stops.Name=="CountStops" && !stops.IsStatic && stops.ReturnType==typeof(int)
                && stops.GetParameters().Length==1 && stops.GetParameters()[0].ParameterType==typeof(ushort)
                && lineID>=0 && Local(code[index],false)==lineID && TypeIs(original,code[line],lineID,typeof(ushort))
                && code[element].opcode==OpCodes.Ldelema && object.Equals(code[element].operand,typeof(TransportLine))
                && Field(code[buffer],typeof(Array16<TransportLine>),"m_buffer") && Field(code[pool],typeof(TransportManager),"m_lines")
                && Receiver(original,code,manager,typeof(TransportManager),end) && Stable(code,countLocal,countInit+1,end);
        }
        private static bool NextAdvance(MethodBase original,List<CodeInstruction> code,Loop loop,bool cached)
        {
            int update=LastStore(code,loop.Cursor,loop.Load), source=Previous(code,update), capture=update;
            int temp=-1;
            if(cached)
            {
                temp=Local(code[source],false); capture=LastStore(code,temp,source);
                if(temp<0 || temp==loop.Cursor || temp==loop.Counter || !TypeIs(original,code[source],temp,typeof(ushort))
                    || !OnlyWrites(code,temp,loop.Start,update,new[]{capture}) || !NoAddress(code,temp,loop.Start,update)) return false;
            }
            int call=Previous(code,capture), input=Previous(code,call);
            if(input<loop.Start || Local(code[input],false)!=loop.Cursor || !StopCall(code[call],"GetNextStop")) return false;
            if(!OnlyWrites(code,loop.Cursor,loop.Start,loop.Load,new[]{update})) return false;
            if(original.Name=="ClearStops" || original.Name=="ReleaseLineImplementation")
            {
                bool release=false;
                for(int i=capture+1;i<update;++i)
                {
                    MethodInfo m=code[i].operand as MethodInfo;
                    if(m==null || m.DeclaringType!=typeof(TransportLine) || m.Name!="ReleaseNode" || code[i].opcode!=OpCodes.Call) continue;
                    int arg=Previous(code,i), receiver=Previous(code,arg);
                    if(Local(code[arg],false)!=loop.Cursor || Argument(original,code[receiver])!=(original.Name=="ClearStops"?0:2)) return false;
                    release=true;
                }
                if(!release) return false;
            }
            return original.Name=="ReleaseLineImplementation" || Ring(original,code,loop,update,false);
        }

        private static bool Ring(MethodBase original,List<CodeInstruction> code,Loop loop,int update,bool mesh)
        {
            int read=Next(code,update), first=Next(code,read);
            if(read<0 || Local(code[read],false)!=loop.Cursor) return false;
            if(Argument(original,code[first])==0 && Field(code[Next(code,first)],typeof(TransportLine),"m_stops")) first=Next(code,first);
            int compare=Next(code,first), exitJump=Next(code,compare);
            if(compare<0 || !Branch(code[compare].opcode,OpCodes.Bne_Un) || !Branch(code[exitJump].opcode,OpCodes.Br)
                || Skip(code,LabelIndex(code,code[exitJump].operand))!=loop.Exit) return false;
            int seed=LastStore(code,loop.Cursor,loop.Start), source=Previous(code,seed);
            if(Local(code[source],false)>=0)
            {
                int alias=Local(code[source],false), definition=LastStore(code,alias,source);
                if(Local(code[first],false)!=alias || definition<0 || !Stable(code,alias,definition+1,loop.Back+1)) return false;
            }
            else if(Argument(original,code[source])>=0)
            { if(Argument(original,code[first])!=Argument(original,code[source])) return false; }
            else if(!Field(code[first],typeof(TransportLine),"m_stops") || !Field(code[source],typeof(TransportLine),"m_stops")) return false;
            int next=Skip(code,LabelIndex(code,code[compare].operand));
            if(next==loop.Load) return true;
            if(!mesh) return false;
            int flag=Local(code[next],false), branch=Next(code,next), stop=Next(code,branch);
            return flag>=0 && TypeIs(original,code[next],flag,typeof(bool)) && Branch(code[branch].opcode,OpCodes.Brtrue)
                && Skip(code,LabelIndex(code,code[branch].operand))==loop.Load && Branch(code[stop].opcode,OpCodes.Br)
                && Skip(code,LabelIndex(code,code[stop].operand))==loop.Exit && NoAddress(code,flag,loop.Start,loop.Load);
        }

        private static bool SegmentAdvance(MethodBase original,List<CodeInstruction> code,Loop loop,int update,bool cached)
        {
            if(update<loop.Start || Local(code[update],true)!=loop.Cursor) return false;
            int capture=update, temp=-1;
            if(cached)
            {
                int source=Previous(code,update); temp=Local(code[source],false); capture=LastStore(code,temp,source);
                if(temp<0 || !TypeIs(original,code[source],temp,typeof(ushort)) || temp==loop.Cursor || temp==loop.Counter) return false;
            }
            int field=Previous(code,capture), element=Previous(code,field), index=Previous(code,element);
            int candidate=index<0?-1:Local(code[index],false);
            if(candidate<0 || !Field(code[field],typeof(NetSegment),"m_endNode")
                || !Element(original,code,element,candidate,typeof(NetSegment),loop.Start,loop.Load)) return false;
            int candidateStore=LastStore(code,candidate,index), call=Previous(code,candidateStore), slotRead=Previous(code,call), node=Previous(code,slotRead);
            MethodInfo get=call<0?null:code[call].operand as MethodInfo; int slot=slotRead<0?-1:Local(code[slotRead],false);
            if(get==null || code[call].opcode!=OpCodes.Call || get.DeclaringType!=typeof(NetNode) || get.Name!="GetSegment"
                || get.IsStatic || get.ReturnType!=typeof(ushort) || get.GetParameters().Length!=1 || get.GetParameters()[0].ParameterType!=typeof(int)
                || slot<0 || !TypeIs(original,code[slotRead],slot,typeof(int)) || !TypeIs(original,code[index],candidate,typeof(ushort))
                || !Element(original,code,node,loop.Cursor,typeof(NetNode),loop.Start,loop.Load)) return false;
            int init=LastStore(code,slot,node), zero=Previous(code,init), jump=Next(code,init);
            if(zero<loop.Start || code[zero].opcode!=OpCodes.Ldc_I4_0 || !Branch(code[jump].opcode,OpCodes.Br)) return false;
            int check=Skip(code,LabelIndex(code,code[jump].operand)), eight=Next(code,check), back=Next(code,eight);
            int store=Previous(code,check), add=Previous(code,store), one=Previous(code,add), load=Previous(code,one);
            if(check<0 || Local(code[check],false)!=slot || code[eight].opcode!=OpCodes.Ldc_I4_8 || !Branch(code[back].opcode,OpCodes.Blt)
                || code[add].opcode!=OpCodes.Add || code[one].opcode!=OpCodes.Ldc_I4_1 || Local(code[load],false)!=slot || Local(code[store],true)!=slot
                || !OnlyWrites(code,slot,loop.Start,loop.Load,new[]{init,store}) || !NoAddress(code,slot,loop.Start,loop.Load)
                || !OnlyWrites(code,candidate,loop.Start,loop.Load,new[]{candidateStore}) || !NoAddress(code,candidate,loop.Start,loop.Load)) return false;
            int inner=Skip(code,LabelIndex(code,code[back].operand));
            if(inner<0 || inner!=ElementStart(code,node)) return false;
            int exists=Next(code,candidateStore), empty=Next(code,exists), direction=NextN(code,empty,6);
            int current=Next(code,direction), differs=Next(code,current);
            if(Local(code[exists],false)!=candidate || !Branch(code[empty].opcode,OpCodes.Brfalse)
                || Skip(code,LabelIndex(code,code[empty].operand))!=load || !Field(code[direction],typeof(NetSegment),"m_startNode")
                || !Element(original,code,Previous(code,direction),candidate,typeof(NetSegment),loop.Start,loop.Load)
                || Local(code[current],false)!=loop.Cursor || !Branch(code[differs].opcode,OpCodes.Bne_Un)
                || Skip(code,LabelIndex(code,code[differs].operand))!=load) return false;
            if(cached)
            {
                int tempInit=Previous(code,zero), tempZero=Previous(code,tempInit);
                if(tempZero<loop.Start || code[tempZero].opcode!=OpCodes.Ldc_I4_0 || Local(code[tempInit],true)!=temp
                    || !OnlyWrites(code,temp,loop.Start,update,new[]{tempInit,capture}) || !NoAddress(code,temp,loop.Start,update)) return false;
            }
            if(!OnlyWrites(code,loop.Cursor,loop.Start,loop.Load,new[]{update})) return false;
            return original.Name!="UpdateMeshData" || Ring(original,code,loop,update,true);
        }
        private static bool LastStop(MethodBase original,List<CodeInstruction> code,Loop loop,int flag)
        {
            if(!TypeIs(original,code[loop.Condition],flag,typeof(bool))) return false;
            int falseStore=Next(code,loop.Start), update=-1, trueStore=-1;
            if(code[loop.Start].opcode!=OpCodes.Ldc_I4_0 || Local(code[falseStore],true)!=flag) return false;
            for(int i=loop.Start;i<loop.Load;++i)
            {
                FieldInfo f=code[i].operand as FieldInfo;
                if(code[i].opcode==OpCodes.Ldfld && f!=null && f.DeclaringType==typeof(NetSegment) && f.Name=="m_endNode")
                { if(update>=0) return false; update=Next(code,i); loop.Cursor=Local(code[update],true); }
                if(Local(code[i],true)==flag && i!=falseStore)
                { if(trueStore>=0 || code[Previous(code,i)].opcode!=OpCodes.Ldc_I4_1) return false; trueStore=i; }
            }
            if(update<0 || !TypeIs(original,code[update],loop.Cursor,typeof(ushort)) || trueStore<update || !OnlyWrites(code,flag,loop.Start,loop.Back,new[]{falseStore,trueStore})
                || !NoAddress(code,flag,loop.Start,loop.Back) || !SegmentAdvance(original,code,loop,update,false)) return false;
            int read=Next(code,update), self=Next(code,read), first=Next(code,self), compare=Next(code,first);
            int result=Next(code,compare), ret=Next(code,result);
            if(Local(code[read],false)!=loop.Cursor || Argument(original,code[self])!=0 || !Field(code[first],typeof(TransportLine),"m_stops")
                || !Branch(code[compare].opcode,OpCodes.Bne_Un) || Skip(code,LabelIndex(code,code[compare].operand))!=Previous(code,trueStore)
                || Local(code[result],false)!=loop.Cursor || code[ret].opcode!=OpCodes.Ret) return false;
            return true;
        }
        private static bool DeserializeAdvance(MethodBase original,List<CodeInstruction> code,Loop loop)
        {
            int zeroStore=LastStore(code,loop.Cursor,loop.Load), zero=Previous(code,zeroStore), skip=Previous(code,zero), capture=Previous(code,skip);
            if(zero<loop.Start || code[zero].opcode!=OpCodes.Ldc_I4_0 || !Branch(code[skip].opcode,OpCodes.Br)
                || Skip(code,LabelIndex(code,code[skip].operand))!=Next(code,zeroStore)) return false;
            int field=Previous(code,capture), element=Previous(code,field), index=Previous(code,element), candidate=Local(code[index],false);
            int candidateStore=LastStore(code,candidate,index), call=Previous(code,candidateStore), input=Previous(code,call);
            if(candidate<0 || Local(code[capture],true)!=loop.Cursor || !Field(code[field],typeof(NetSegment),"m_endNode")
                || !Element(original,code,element,candidate,typeof(NetSegment),loop.Start,loop.Load)
                || !StopCall(code[call],"GetNextSegment") || Local(code[input],false)!=loop.Cursor
                || !OnlyWrites(code,candidate,loop.Start,loop.Load,new[]{candidateStore}) || !NoAddress(code,candidate,loop.Start,loop.Load)) return false;
            int check=Next(code,candidateStore), branch=Next(code,check);
            return Local(code[check],false)==candidate && Branch(code[branch].opcode,OpCodes.Brfalse)
                && Skip(code,LabelIndex(code,code[branch].operand))==zero
                && OnlyWrites(code,loop.Cursor,loop.Start,loop.Load,new[]{capture,zeroStore}) && Ring(original,code,loop,zeroStore,false);
        }

        private static bool Seed(MethodBase original,List<CodeInstruction> code,Loop loop,string name)
        {
            int seed=LastStore(code,loop.Cursor,loop.Start);
            if(seed<0 || !NoAddress(code,loop.Cursor,seed+1,loop.Start) || !OnlyWrites(code,loop.Cursor,seed+1,loop.Start,new int[0])) return false;
            if(name=="FindBuildingLine" || name=="FindOutgoingConnectionStop" || name=="AddPaths") return BuildingSeed(original,code,seed,loop.Start);
            if(name=="GetLineCount") return GridSeed(original,code,seed,loop.Start);
            if(name=="GetStopPosition")
            {
                if(Argument(original,code[Previous(code,seed)])!=4) return false;
                int definition=-1;
                for(int i=0;i<seed;++i) if(ArgumentStore(original,code[i])==4) definition=i;
                if(definition<0 || !LineRoot(original,code,Previous(code,definition),loop.Start,0)) return false;
                for(int i=definition+1;i<loop.Exit;++i) if(ArgumentStore(original,code[i])==4) return false;
                return true;
            }
            return LineRoot(original,code,Previous(code,seed),loop.Start,0);
        }
        private static bool LineRoot(MethodBase original,List<CodeInstruction> code,int value,int end,int depth)
        {
            if(value<0 || depth>5) return false;
            int alias=Local(code[value],false);
            if(alias>=0)
            {
                int store=LastStore(code,alias,value);
                return store>=0 && Stable(code,alias,store+1,value) && LineRoot(original,code,Previous(code,store),end,depth+1);
            }
            if(!Field(code[value],typeof(TransportLine),"m_stops")) return false;
            int receiver=Previous(code,value), argument=Argument(original,code[receiver]);
            if(argument>=0) return original.DeclaringType==typeof(TransportLine) && argument==0
                || original.Name=="ReleaseLineImplementation" && argument==2;
            if(code[receiver].opcode!=OpCodes.Ldelema || !object.Equals(code[receiver].operand,typeof(TransportLine))) return false;
            int index=Previous(code,receiver), indexStart=index;
            if(original.Name=="CloneLine" && Argument(original,code[index])!=2) return false;
            if(original.Name=="OnSetTarget")
            {
                int lineID=Local(code[index],false), definition=LastStore(code,lineID,index), call=Previous(code,definition), self=Previous(code,call);
                MethodInfo get=call<0?null:code[call].operand as MethodInfo;
                if(lineID<0 || get==null || code[call].opcode!=OpCodes.Call || get.DeclaringType!=typeof(PublicTransportWorldInfoPanel)
                    || get.Name!="GetLineID" || get.IsStatic || get.ReturnType!=typeof(ushort) || get.GetParameters().Length!=0
                    || self<0 || Argument(original,code[self])!=0 || !Stable(code,lineID,definition+1,end)) return false;
            }
            if(Field(code[index],typeof(TransportTool),"m_line"))
            { indexStart=Previous(code,index); if(Argument(original,code[indexStart])!=0 || original.DeclaringType!=typeof(TransportTool)) return false; }
            int buffer=Previous(code,indexStart), pool=Previous(code,buffer), manager=Previous(code,pool);
            if(Field(code[buffer],typeof(Array16<TransportLine>),"m_buffer") && Field(code[pool],typeof(TransportManager),"m_lines"))
                return Receiver(original,code,manager,typeof(TransportManager),end);
            int local=Local(code[buffer],false);
            if(local<0 || !TypeIs(original,code[buffer],local,typeof(TransportLine[]))) return false;
            int init=LastStore(code,local,buffer), array=Previous(code,init), source=Previous(code,array), root=Previous(code,source);
            return init>=0 && Field(code[array],typeof(Array16<TransportLine>),"m_buffer") && Field(code[source],typeof(TransportManager),"m_lines")
                && Receiver(original,code,root,typeof(TransportManager),end) && Stable(code,local,init+1,buffer);
        }
        private static bool BuildingSeed(MethodBase original,List<CodeInstruction> code,int store,int end)
        {
            int field=Previous(code,store), element=Previous(code,field), index=Previous(code,element);
            int indexStart = original.Name=="FindBuildingLine" ? index : Previous(code,index);
            int buffer=Previous(code,indexStart), pool=Previous(code,buffer), manager=Previous(code,pool);
            if(manager<0 || !Field(code[field],typeof(Building),"m_netNode") || code[element].opcode!=OpCodes.Ldelema
                || !object.Equals(code[element].operand,typeof(Building)) || !Field(code[buffer],typeof(Array16<Building>),"m_buffer")
                || !Field(code[pool],typeof(BuildingManager),"m_buildings") || !Receiver(original,code,manager,typeof(BuildingManager),end)) return false;
            if(original.Name=="FindBuildingLine") return Argument(original,code[index])==0;
            if(original.Name=="FindOutgoingConnectionStop")
            {
                // sourceBuilding is the only field between vehicle argument and pool index.
                int arg=Previous(code,index);
                int actualBuffer=Previous(code,arg), actualPool=Previous(code,actualBuffer), actualManager=Previous(code,actualPool);
                return Field(code[index],typeof(Vehicle),"m_sourceBuilding") && Argument(original,code[arg])==2
                    && Field(code[actualBuffer],typeof(Array16<Building>),"m_buffer") && Field(code[actualPool],typeof(BuildingManager),"m_buildings")
                    && Receiver(original,code,actualManager,typeof(BuildingManager),end);
            }
            MethodInfo get=code[index].operand as MethodInfo;
            return get!=null && code[index].opcode==OpCodes.Call && get.DeclaringType==typeof(InstanceID) && get.Name=="get_Building"
                && Argument(original,code[Previous(code,index)])==1;
        }
        private static bool GridSeed(MethodBase original,List<CodeInstruction> code,int store,int end)
        {
            int element=Previous(code,store), add=Previous(code,element), x=Previous(code,add), mul=Previous(code,x), width=Previous(code,mul), z=Previous(code,width), grid=Previous(code,z), manager=Previous(code,grid);
            return manager>=0 && code[element].opcode==OpCodes.Ldelem_U2 && code[add].opcode==OpCodes.Add && code[mul].opcode==OpCodes.Mul
                && code[width].opcode==OpCodes.Ldc_I4 && object.Equals(code[width].operand,270)
                && Local(code[x],false)>=0 && TypeIs(original,code[x],Local(code[x],false),typeof(int))
                && Local(code[z],false)>=0 && TypeIs(original,code[z],Local(code[z],false),typeof(int))
                && Field(code[grid],typeof(NetManager),"m_nodeGrid") && Receiver(original,code,manager,typeof(NetManager),end);
        }
        private static bool Element(MethodBase original,List<CodeInstruction> code,int element,int cursor,Type item,int start,int end)
        {
            int index=Previous(code,element), buffer=Previous(code,index), pool=Previous(code,buffer), manager=Previous(code,pool);
            return manager>=start && code[element].opcode==OpCodes.Ldelema && object.Equals(code[element].operand,item)
                && Local(code[index],false)==cursor && Field(code[buffer],typeof(Array16<>).MakeGenericType(item),"m_buffer")
                && Field(code[pool],typeof(NetManager),item==typeof(NetNode)?"m_nodes":"m_segments")
                && Receiver(original,code,manager,typeof(NetManager),end);
        }
        private static int ElementStart(List<CodeInstruction> code,int element) { return Previous(code,Previous(code,Previous(code,Previous(code,element)))); }
        private static bool Link(MethodBase original,List<CodeInstruction> code,int store,int cursor,string name,int start,int end)
        {
            int field=Previous(code,store), element=Previous(code,field);
            return field>=0 && Field(code[field],typeof(NetNode),name) && Element(original,code,element,cursor,typeof(NetNode),start,end);
        }
        private static bool Receiver(MethodBase original,List<CodeInstruction> code,int receiver,Type type,int end)
        {
            if(receiver<0) return false;
            if(code[receiver].opcode==OpCodes.Ldarg_0 && original.DeclaringType==type && !original.IsStatic) return true;
            MethodInfo call=code[receiver].operand as MethodInfo;
            if(call!=null && code[receiver].opcode==OpCodes.Call) return call.DeclaringType==typeof(Singleton<>).MakeGenericType(type)
                && call.IsStatic && call.Name=="get_instance" && call.ReturnType==type && call.GetParameters().Length==0;
            int local=Local(code[receiver],false), init=LastStore(code,local,receiver), source=Previous(code,init);
            return local>=0 && TypeIs(original,code[receiver],local,type) && source>=0
                && Receiver(original,code,source,type,init) && Stable(code,local,init+1,end);
        }
        private static bool StopCall(CodeInstruction code,string name)
        {
            MethodInfo method=code.operand as MethodInfo;
            return code.opcode==OpCodes.Call && method!=null && method.DeclaringType==typeof(TransportLine) && method.Name==name
                && method.IsStatic && method.ReturnType==typeof(ushort) && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType==typeof(ushort);
        }
        private static bool ContainsField(List<CodeInstruction> code,int start,int end,string type,string name,OpCode operation)
        {
            for(int i=start;i<end;++i) { FieldInfo f=code[i].operand as FieldInfo; if(f!=null && code[i].opcode==operation && f.DeclaringType.FullName==type && f.Name==name) return true; }
            return false;
        }
        private static int Argument(MethodBase original,CodeInstruction code)
        {
            if(code.opcode==OpCodes.Ldarg_0) return 0; if(code.opcode==OpCodes.Ldarg_1) return 1;
            if(code.opcode==OpCodes.Ldarg_2) return 2; if(code.opcode==OpCodes.Ldarg_3) return 3;
            if(code.opcode!=OpCodes.Ldarg && code.opcode!=OpCodes.Ldarg_S && code.opcode!=OpCodes.Ldarga && code.opcode!=OpCodes.Ldarga_S) return -1;
            ParameterInfo p=code.operand as ParameterInfo; if(p!=null) return p.Position+(original.IsStatic?0:1);
            return code.operand is byte?(byte)code.operand:code.operand is int?(int)code.operand:code.operand is short?(short)code.operand:-1;
        }
        private static int ArgumentStore(MethodBase original,CodeInstruction code)
        {
            if(code.opcode!=OpCodes.Starg && code.opcode!=OpCodes.Starg_S) return -1;
            ParameterInfo p=code.operand as ParameterInfo;
            if(p!=null) return p.Position+(original.IsStatic?0:1);
            return code.operand is byte?(byte)code.operand:code.operand is int?(int)code.operand:code.operand is short?(short)code.operand:-1;
        }
        private static bool Stable(List<CodeInstruction> code,int local,int start,int end) { return NoAddress(code,local,start,end) && OnlyWrites(code,local,start,end,new int[0]); }
        private static bool NoAddress(List<CodeInstruction> code,int local,int start,int end)
        { for(int i=Math.Max(0,start);i<end && i<code.Count;++i) if(Address(code[i])==local) return false; return true; }
        private static bool OnlyWrites(List<CodeInstruction> code,int local,int start,int end,int[] allowed)
        { for(int i=Math.Max(0,start);i<end && i<code.Count;++i) if(Local(code[i],true)==local && Array.IndexOf(allowed,i)<0) return false; return true; }
        private static bool TypeIs(MethodBase original,CodeInstruction code,int local,Type type)
        { IList<LocalVariableInfo> locals=original.GetMethodBody().LocalVariables; return local>=0 && local<locals.Count && VariableType(code,local,locals)==type; }
        private static int NextN(List<CodeInstruction> code,int start,int count) { int p=start; for(int i=0;i<count;++i) p=Next(code,p); return p; }
        private static int LastStore(List<CodeInstruction> code,int local,int end) { for(int i=end-1;i>=0;--i) if(Local(code[i],true)==local) return i; return -1; }
        private static bool TypedLocals(MethodBase original, List<CodeInstruction> code)
        {
            MethodBody body = original.GetMethodBody();
            if (body == null) return false;
            IList<LocalVariableInfo> locals = body.LocalVariables;
            Dictionary<int, Type> extras = new Dictionary<int, Type>();
            foreach (CodeInstruction instruction in code)
            {
                int local = Local(instruction, false);
                if (local < 0) local = Local(instruction, true);
                if (local < 0) local = Address(instruction);
                string operation = instruction.opcode.Name ?? "";
                if (!operation.StartsWith("ldloc") && !operation.StartsWith("stloc")) continue;
                if (local < 0) return false;
                if (local < locals.Count)
                {
                    if (VariableType(instruction, local, locals) != locals[local].LocalType) return false;
                }
                else
                {
                    LocalBuilder builder = instruction.operand as LocalBuilder;
                    LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
                    Type declared = builder != null ? builder.LocalType : variable != null ? variable.LocalType : null;
                    if (declared == null) return false;
                    Type previous;
                    if (extras.TryGetValue(local, out previous) && previous != declared) return false;
                    extras[local] = declared;
                }
            }
            return true;
        }

        private static bool Field(CodeInstruction code, Type type, string name)
        {
            FieldInfo field = code.operand as FieldInfo;
            return code.opcode == OpCodes.Ldfld && field != null && field.Name == name
                && (type == null || field.DeclaringType == type);
        }
        private static bool Call(List<CodeInstruction> code, int index, Type type, string name)
        {
            MethodInfo call = index < 0 ? null : code[index].operand as MethodInfo;
            return call != null && code[index].opcode == OpCodes.Call && call.DeclaringType == type && call.Name == name;
        }
        private static bool Branch(OpCode actual, OpCode op)
        {
            return actual == op || (op == OpCodes.Br && actual == OpCodes.Br_S)
                || (op == OpCodes.Brtrue && actual == OpCodes.Brtrue_S) || (op == OpCodes.Beq && actual == OpCodes.Beq_S) || (op == OpCodes.Bne_Un && actual == OpCodes.Bne_Un_S) || (op == OpCodes.Brfalse && actual == OpCodes.Brfalse_S)
                || (op == OpCodes.Blt && actual == OpCodes.Blt_S) || (op == OpCodes.Ble && actual == OpCodes.Ble_S);
        }
        private static int LabelIndex(List<CodeInstruction> code, object operand)
        {
            if (!(operand is Label)) return -1;
            int result = -1;
            for (int i = 0; i < code.Count; ++i) if (code[i].labels.Contains((Label)operand))
            { if (result >= 0) return -1; result = i; }
            return result;
        }
        private static int Skip(List<CodeInstruction> code, int index)
        {
            if (index < 0) return -1;
            while (index < code.Count && code[index].opcode == OpCodes.Nop) ++index;
            return index < code.Count ? index : -1;
        }
        private static int Next(List<CodeInstruction> code, int index) { return index < 0 ? -1 : Skip(code, index + 1); }
        private static int Previous(List<CodeInstruction> code, int index)
        {
            --index;
            while (index >= 0 && code[index].opcode == OpCodes.Nop) --index;
            return index;
        }
        private static int Address(CodeInstruction code)
        {
            return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? LocalOperand(code.operand) : -1;
        }
        private static int Local(CodeInstruction code, bool store)
        {
            OpCode op = code.opcode;
            if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            return op == (store ? OpCodes.Stloc : OpCodes.Ldloc) || op == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)
                ? LocalOperand(code.operand) : -1;
        }
        private static int LocalOperand(object operand)
        {
            LocalBuilder builder = operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (operand is int) return (int)operand;
            if (operand is byte) return (byte)operand;
            if (operand is short) return (short)operand;
            return -1;
        }
        private static Type VariableType(CodeInstruction access, int index, IList<LocalVariableInfo> locals)
        {
            LocalBuilder builder = access.operand as LocalBuilder;
            if (builder != null) return builder.LocalType;
            LocalVariableInfo variable = access.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalType;
            return index >= 0 && index < locals.Count ? locals[index].LocalType : null;
        }
    }
}
