using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using ColossalFramework.UI;
using HarmonyLib;
using UnityEngine;

class StopHoverToolMatrix
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    public static Camera Camera;
    public static ToolController Controller;
    public static TransportManager Transport;
    public static NetManager Network;
    public static bool Modal, Inside;
    static int expectedMask, hits;
    static ToolBase active;
    static object inspector;
    static MethodInfo toggle, hover;
    static object stop;
    const ushort Node = 65023;

    static T Stub<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    public static bool Same(UnityEngine.Object a,UnityEngine.Object b) { return ReferenceEquals(a,b); }
    public static bool Different(UnityEngine.Object a,UnityEngine.Object b) { return !ReferenceEquals(a,b); }
    public static bool Ready() { return true; }
    public static Vector3 Mouse() { return Vector3.zero; }
    public static Ray Ray(Camera camera, Vector3 mouse) { return new Ray(); }
    public static float Far(Camera camera) { return 1000; }
    public static bool Intersect(TransportManager manager, Ray ray, float length, int mask, out Vector3 pos,
        out ushort line, out int index, out int segment)
    {
        if(mask != expectedMask) throw new Exception("Wrong line type mask: " + mask + " expected " + expectedMask);
        hits++;
        pos = Vector3.zero; line=1; index=0; segment=-1;
        return true;
    }
    public static bool SkipUi() { return false; }

    // Replace scene/native boundaries only. The d19 filter, GetStop, ownership checks,
    // actual ToolController getter and node validation all run unchanged.
    public static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var ins in instructions)
        {
            var m = ins.operand as MethodInfo;
            if(m != null)
            {
                string field=null, method=null;
                if(m.DeclaringType==typeof(UIView)) field=m.Name=="HasModalInput" ? "Modal" : "Inside";
                else if(m.Name=="get_main") field="Camera";
                else if(m.Name=="get_toolController") field="Controller";
                else if(m.Name=="get_exists") method="Ready";
                else if(m.Name=="get_instance") field=m.ReturnType==typeof(TransportManager) ? "Transport" : "Network";
                else if(m.DeclaringType==typeof(UnityEngine.Object)) method=m.Name=="op_Equality" ? "Same" : "Different";
                else if(m.Name=="get_mousePosition") method="Mouse";
                else if(m.Name=="ScreenPointToRay") method="Ray";
                else if(m.Name=="get_farClipPlane") method="Far";
                else if(m.DeclaringType==typeof(TransportManager) && m.Name=="RayCast") method="Intersect";
                if(field!=null) {ins.opcode=OpCodes.Ldsfld; ins.operand=typeof(StopHoverToolMatrix).GetField(field);}
                if(method!=null) {ins.opcode=OpCodes.Call; ins.operand=typeof(StopHoverToolMatrix).GetMethod(method);}
            }
            yield return ins;
        }
    }
    static void Select(ToolBase tool)
    {
        active=tool;
        foreach(string name in new[]{"m_currentTool","m_nextTool"}) typeof(ToolController).GetField(name,Members).SetValue(Controller,tool);
    }
    static void Sample(int mask, bool expectedHit)
    {
        expectedMask=mask;
        int before=hits;
        object[] args={-1};
        var result=(InstanceID)hover.Invoke(null,args);
        if(result.NetNode!=(expectedHit?Node:0) || hits-before!=(expectedHit?1:0) ||
            !ReferenceEquals(Controller.CurrentTool,active) || (expectedHit && (int)args[0]!=0))
            throw new Exception("Hover selection, tool ownership or UI gating changed");
    }
    static void Run()
    {
        var asm=Assembly.LoadFrom("MoreNetworkCapacity.dll");
        var type=asm.GetType("MoreNetworkCapacity.Diagnostics.Inspector",true);
        var h=new Harmony("mnc.test.stop-tool-matrix");
        hover=type.GetMethod("HoverStop",Members); toggle=type.GetMethod("Toggle",Members);
        h.Patch(hover,null,null,new HarmonyMethod(typeof(StopHoverToolMatrix).GetMethod("Scene")),null);
        h.Patch(type.GetMethod("RefreshControls",Members),new HarmonyMethod(typeof(StopHoverToolMatrix).GetMethod("SkipUi")),null,null,null);
        inspector=FormatterServices.GetUninitializedObject(type);
        stop=Enum.Parse(type.GetField("mode",Members).FieldType,"Stop");
        Camera=Stub<Camera>(); Controller=Stub<ToolController>(); Transport=Stub<TransportManager>(); Network=Stub<NetManager>();
        Transport.m_lines=Stub<Array16<TransportLine>>(); Transport.m_lines.m_buffer=new TransportLine[2]; Transport.m_lines.m_buffer[1].m_stops=Node;
        Network.m_nodes=Stub<Array16<NetNode>>(); Network.m_nodes.m_buffer=new NetNode[65024];
        Network.m_nodes.m_buffer[Node].m_flags=NetNode.Flags.Created; Network.m_nodes.m_buffer[Node].m_transportLine=1;
        var lineTool=Stub<TransportTool>(); lineTool.m_prefab=Stub<TransportInfo>(); Select(lineTool);
        toggle.Invoke(inspector,new[]{stop});
        int count=0;
        foreach(TransportInfo.TransportType kind in Enum.GetValues(typeof(TransportInfo.TransportType)))
        {
            lineTool.m_prefab.m_transportType=kind;
            Sample(1<<(int)kind,true); count++;
        }
        lineTool.m_prefab=null; Sample(-1,true);
        foreach(var tool in new ToolBase[]{Stub<DefaultTool>(),Stub<NetTool>(),Stub<BulldozeTool>(),Stub<BuildingTool>(),Stub<DistrictTool>(),Stub<TreeTool>(),Stub<PropTool>(),Stub<TerrainTool>()})
        { Select(tool); Sample(-1,true); }
        foreach(string pair in new[]{"MoveIt.dll|MoveIt.MoveItTool","NetworkMultitool.dll|NetworkMultitool.NetworkMultitoolTool"})
        {
            var parts=pair.Split('|');var toolType=Assembly.LoadFrom(parts[0]).GetType(parts[1],true);
            Select((ToolBase)FormatterServices.GetUninitializedObject(toolType)); Sample(-1,true);
        }
        Modal=true; Sample(-1,false); Modal=false; Inside=true; Sample(-1,false); Inside=false;
        toggle.Invoke(inspector,new[]{stop});
        if(!ReferenceEquals(Controller.CurrentTool,active)) throw new Exception("Stop exit stole tool");
        Console.WriteLine("PASS "+count+" transport type masks, missing prefab, 8 game tools, Move It, Network Multitool, live switch, UI/modal and exit");
    }
    static int Main() { try{Run();return 0;}catch(Exception e){while(e!=null){Console.WriteLine(e.GetType()+" "+e.Message+" "+e.StackTrace);e=e.InnerException;}return 1;} }
}
