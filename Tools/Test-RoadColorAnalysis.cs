using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

class RoadColorAnalysisTest
{
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }

    public static bool SkipUi() { return false; }

    static void Sequence(Assembly mod, object first, object second)
    {
        var type=mod.GetType("MoreNetworkCapacity.Diagnostics.RoadColorAnalyzer",true);
        mod.GetType("MoreNetworkCapacity.UiLanguage",true).GetField("choice",Members).SetValue(null,2);
        var pickerType=mod.GetType("MoreNetworkCapacity.Diagnostics.InspectorPickTool",true);
        var panel=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        var picker=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(pickerType);
        new HarmonyLib.Harmony("mnc.test.color-sequence").Patch(type.GetMethod("Draw",Members),
            new HarmonyLib.HarmonyMethod(typeof(RoadColorAnalysisTest).GetMethod("SkipUi")),null,null,null);
        type.GetField("picker",Members).SetValue(panel,picker);
        type.GetField("comparison",Members).SetValue(panel,true);
        type.GetField("picking",Members).SetValue(panel,true);
        var one=InstanceID.Empty; one.NetSegment=65023;
        var two=InstanceID.Empty; two.NetNode=41000;
        var accept=type.GetMethod("Accept",Members);
        var undo=type.GetMethod("UndoSelection",Members);
        var a=type.GetField("a",Members); var b=type.GetField("b",Members);
        accept.Invoke(panel,new object[]{one,first});
        var mark=type.GetMethod("Mark",Members);
        type.GetMethod("Sample",Members).Invoke(panel,new object[]{two,UnityEngine.Vector3.zero});
        Check(b.GetValue(panel)==null,"Picked second before marking first");
        mark.Invoke(panel,new object[]{true});
        accept.Invoke(panel,new object[]{two,second});
        mark.Invoke(panel,new object[]{false});
        var report=(string)first.GetType().GetMethod("MakeReport",Members).Invoke(null,new object[]{first,second,false});
        Check(report.Contains("Visual observation  Abnormal") && report.Contains("Visual observation  Normal"),"Missing per-object observations");
        Check((bool)first.GetType().GetField("Abnormal",Members).GetValue(first),"Second mark overwrote first");
        type.GetMethod("Sample",Members).Invoke(panel,new object[]{one,UnityEngine.Vector3.zero});
        Check(a.GetValue(panel)==first && b.GetValue(panel)==second,"Extra click changed completed comparison");
        Check(((InstanceID)pickerType.GetField("Pinned",Members).GetValue(picker)).NetSegment==65023,"First object not pinned");
        undo.Invoke(panel,null);
        Check(a.GetValue(panel)==first && b.GetValue(panel)==null,"Undo must preserve first sample");
        accept.Invoke(panel,new object[]{one,first});
        Check(b.GetValue(panel)==first,"Could not reselect second object");
        undo.Invoke(panel,null); undo.Invoke(panel,null);
        Check(a.GetValue(panel)==null && b.GetValue(panel)==null,"Second undo did not clear first selection");
        type.GetField("picker",Members).SetValue(panel,null);
        undo.Invoke(panel,null);
        Check(!(bool)type.GetField("picking",Members).GetValue(panel),"Empty undo did not exit");
        Console.WriteLine("PASS sequential picks, completed comparison lock, highlight, undo/reselect and exit");
    }
    public static bool Warning(string message) {Console.WriteLine(message);return false;}

    static void TraceTest(Assembly mod,Type sampleType,object sample)
    {
        var hooks=mod.GetType("MoreNetworkCapacity.Diagnostics.RoadRenderHooks",true);
        var traceType=mod.GetType("MoreNetworkCapacity.Diagnostics.RoadRenderTrace",true);
        var harmony=new HarmonyLib.Harmony("mnc.test.trace");
        harmony.Patch(mod.GetType("MoreNetworkCapacity.Logging").GetMethod("Info",Members),
            new HarmonyLib.HarmonyMethod(typeof(RoadColorAnalysisTest).GetMethod("Warning")),null,null,null);
        harmony.Patch(mod.GetType("MoreNetworkCapacity.Logging").GetMethod("Warning",Members),
            new HarmonyLib.HarmonyMethod(typeof(RoadColorAnalysisTest).GetMethod("Warning")),null,null,null);
        hooks.GetMethod("Start",Members).Invoke(null,null);
        Check((string)hooks.GetField("Status",Members).GetValue(null)=="active","Render observers did not install");
        var trace=Activator.CreateInstance(traceType,Members,null,new object[]{false,(ushort)65023,UnityEngine.Vector3.zero,(uint)1},null);
        var sync=traceType.GetField("sync",Members).GetValue(trace);
        var draw=traceType.GetMethod("Draw",Members);
        var worker=new System.Threading.Thread(delegate() { draw.Invoke(trace,new object[]{false,null}); });
        lock(sync)
        {
            worker.Start();
            Check(worker.Join(2000),"Render callback waited for the diagnostics lock");
        }
        Check((int)traceType.GetField("skippedCallbacks",Members).GetValue(trace)==1,"Skipped render observation was not counted");
        var uv=new UnityEngine.Vector4(.25f,.5f,.125f,.375f);
        var data=new[]{UnityEngine.Vector4.zero,uv,uv,UnityEngine.Vector4.zero};
        var observe=traceType.GetMethod("Vertices",Members);
        observe.Invoke(trace,new object[]{data,1,3,uv,4,5,6});
        sampleType.GetField("Trace",Members).SetValue(sample,trace);
        string report=(string)sampleType.GetMethod("MakeReport",Members).Invoke(null,new object[]{sample,null,false});
        Check(report.Contains("match: 2") && !report.Contains("MISMATCH"),"Wrong UV comparison interval");
        data[2]=UnityEngine.Vector4.zero;
        observe.Invoke(trace,new object[]{data,1,3,uv,4,5,6});
        report=(string)sampleType.GetMethod("MakeReport",Members).Invoke(null,new object[]{sample,null,false});
        Check(report.Contains("MISMATCH 1") && report.Contains("vertex=2"),"Wrong UV anomaly report");
        sampleType.GetField("Trace",Members).SetValue(sample,null);
        hooks.GetMethod("Stop",Members).Invoke(null,null);
        Console.WriteLine("PASS runtime render observers, vertex subrange, mismatch evidence and teardown");
    }
    static void Run()
    {
        var mod=Assembly.LoadFrom("MoreNetworkCapacity.dll");
        var sample=mod.GetType("MoreNetworkCapacity.Diagnostics.RoadColorSample",true);
        var add=sample.GetMethod("Add",Members);
        var diff=sample.GetMethod("Differences",Members);
        var report=sample.GetMethod("MakeReport",Members);
        var a=Activator.CreateInstance(sample,Members,null,new object[]{"NetSegment 65023"},null);
        var b=Activator.CreateInstance(sample,Members,null,new object[]{"NetSegment 65023"},null);
        foreach(object value in new[]{a,b}) add.Invoke(value,new object[]{"Road|Road","MSE"});
        add.Invoke(a,new object[]{"APR|APR","old"});
        add.Invoke(b,new object[]{"APR|APR","new"});
        add.Invoke(b,new object[]{"Shader|Shader","shader2"});
        var changes=(List<string>)diff.Invoke(a,new object[]{b,false});
        Check(changes.Count==2 && changes[0].Contains("old") && changes[0].Contains("new"),"Wrong differences");
        Check(((List<string>)diff.Invoke(a,new object[]{a,false})).Count==0,"Identical snapshots differ");
        string single=(string)report.Invoke(null,new object[]{a,null,false});
        Check(!single.Contains("Differences") && !single.Contains("shader2"),"Single sample polluted");
        string comparison=(string)report.Invoke(null,new object[]{a,b,false});
        Check(comparison.Contains("Differences (1") && comparison.Contains("65023") && comparison.Contains("shader2"),"Incomplete comparison");
        var node=Activator.CreateInstance(sample,Members,null,new object[]{"NetNode 41000"},null);
        add.Invoke(node,new object[]{"Node|Node","node only"});
        Check(((string)report.Invoke(null,new object[]{a,node,true})).Contains("NetNode 41000"),"Node missing from comparison");
        Check(((List<string>)sample.GetMethod("Compare",Members).Invoke(a,new object[]{b,false,false})).Count==3,"Missing equal comparison rows");
        TraceTest(mod,sample,a);
        Sequence(mod,a,node);
        var inputType=mod.GetType("MoreNetworkCapacity.DiagnosticBundleInput");
        var input=Activator.CreateInstance(inputType,true);
        foreach(string field in new[]{"Summary","Mods","Patches"}) inputType.GetField(field,Members).SetValue(input,"fixture");
        foreach(string field in new[]{"GameLogs","ReportDirectories"}) inputType.GetField(field,Members).SetValue(input,new string[0]);
        inputType.GetField("OutputDirectory",Members).SetValue(input,Environment.CurrentDirectory);
        inputType.GetField("RoadColorReport",Members).SetValue(input,comparison);
        var write=mod.GetType("MoreNetworkCapacity.DiagnosticBundleWriter").GetMethod("Write",Members);
        string zip=(string)write.Invoke(null,new object[]{input});
        File.WriteAllText("zip-name.txt",zip); File.WriteAllText("expected.txt",comparison);
        Console.WriteLine("PASS immutable samples, same-road and mixed-node differences, single/comparison reports and diagnostic export");
    }
    static int Main(){try {Run();return 0;}catch(Exception e){while(e!=null){Console.WriteLine(e.GetType()+" "+e.Message);e=e.InnerException;}return 1;}}
}
