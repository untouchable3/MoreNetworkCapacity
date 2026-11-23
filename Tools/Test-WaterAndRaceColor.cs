using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

class WaterAndRaceColorTests
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static bool Quiet(string message) { return false; }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static void RaceColor(Assembly mnc, Harmony harmony)
    {
        var transpiler = mnc.GetType("MoreNetworkCapacity.Patches.NetworkColorOffsetPatch").GetMethod("Transpiler", Members);
        var writer = typeof(EventManager).GetMethod("UpdateColorMap", Members);
        var reader = typeof(EventManager).GetMethod("GetEventRouteRoadColorIndex", Members);
        var instructions = (IEnumerable<CodeInstruction>)transpiler.Invoke(null,
            new object[]{writer, PatchProcessor.GetOriginalInstructions(writer)});
        int slot = -1;
        bool sawRaceColor = false;
        foreach (var ins in instructions)
        {
            var method = ins.operand as MethodInfo;
            if (method != null && method.Name == "GetRaceRoadColorIndex")
                slot = (int)method.Invoke(null, null);
            if (Equals(ins.operand, 119040)) throw new Exception("Writer still uses old race color slot");
            var field = ins.operand as FieldInfo;
            if (field != null && field.Name == "m_raceRoadColor")
            {
                Check(slot == 179456, "Wrong race road write index");
                sawRaceColor = true;
            }
        }
        Check(sawRaceColor, "Race road color write missing");
        harmony.Patch(reader, null, null, new HarmonyMethod(transpiler), null);
        RenderManager.OBJECT_COLORMAP_WIDTH = RenderManager.OBJECT_COLORMAP_HEIGHT = 512;
        var uv = (UnityEngine.Vector2)reader.Invoke(null, new object[]{(ushort)0, false});
        Check((int)(uv.x*512) + 512*(int)(uv.y*512) == slot, "Reader and writer disagree");
        Console.WriteLine("PASS race road reader and writer use " + slot);
    }

    static readonly Dictionary<int, UnityEngine.Color> Pixels = new Dictionary<int, UnityEngine.Color>();

    public static void RecordPixel(UnityEngine.Texture2D texture, int x, int y, UnityEngine.Color color)
    {
        Pixels.Add(x + 512*y, color);
    }

    public static IEnumerable<CodeInstruction> CapturePixels(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var ins in instructions)
        {
            var method = ins.operand as MethodInfo;
            if (method != null && method.DeclaringType == typeof(UnityEngine.Texture2D) && method.Name == "SetPixel")
            {
                ins.opcode = System.Reflection.Emit.OpCodes.Call;
                ins.operand = typeof(WaterAndRaceColorTests).GetMethod("RecordPixel");
            }
            yield return ins;
        }
    }

    static void EventRefresh(Assembly mnc, Harmony harmony)
    {
        var type = typeof(EventManager);
        var events = (EventManager)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
        var props = (EventProperties)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(EventProperties));
        props.m_eventRoadColor = UnityEngine.Color.red;
        props.m_raceRoadColor = UnityEngine.Color.blue;
        type.BaseType.GetField("m_properties", Members).SetValue(events, props);
        var routes = new FastList<EventRouteData>();
        routes.m_buffer = new EventRouteData[256];
        routes.m_size = 256;
        routes.m_buffer[255].m_flags = (EventRouteData.Flags)1;
        routes.m_buffer[255].m_color = new UnityEngine.Color32(0,255,0,255);
        type.GetField("m_eventRoutes",Members).SetValue(events,routes);
        var refresh = mnc.GetType("MoreNetworkCapacity.Patches.RenderManagerAwakePatch")
            .GetMethod("QueueEventColorRefresh", Members, null, new[]{type}, null);
        refresh.Invoke(null,new object[]{events}); // Before Awake creates its update lock.
        type.GetField("m_colorUpdateLock", Members).SetValue(events,new object());
        var init = type.GetField("m_colorUpdateInit", Members);
        init.SetValue(events,true);
        type.GetField("m_colorUpdateMin",Members).SetValue(events,258);
        type.GetField("m_colorUpdateMax",Members).SetValue(events,-1);
        var writer = type.GetMethod("UpdateColorMap", Members);
        var offsets = mnc.GetType("MoreNetworkCapacity.Patches.NetworkColorOffsetPatch").GetMethod("Transpiler",Members);
        harmony.Patch(writer,null,null,new HarmonyMethod(offsets),null);
        harmony.Patch(writer,null,null,new HarmonyMethod(typeof(WaterAndRaceColorTests).GetMethod("CapturePixels")),null);
        foreach (int baseSlot in new[]{179200,179200,118784,179200})
        {
            if (baseSlot == 118784) harmony.Unpatch(writer,offsets);
            Pixels.Clear();
            refresh.Invoke(null,new object[]{events});
            refresh.Invoke(null,new object[]{events});
            Check((bool)writer.Invoke(events,new object[]{null}), "No event color refresh");
            Check(Pixels.Count == 3 && Pixels[baseSlot].Equals(UnityEngine.Color.red) &&
                Pixels[baseSlot+255].Equals(UnityEngine.Color.green) && Pixels[baseSlot+256].Equals(UnityEngine.Color.blue),
                "Incorrect route or default color");
            Check((bool)init.GetValue(events) && !(bool)writer.Invoke(events,new object[]{null}), "Refresh stayed dirty");
            if (baseSlot == 118784) harmony.Patch(writer,null,null,new HarmonyMethod(offsets),null);
        }
        Console.WriteLine("PASS real event writer refreshed both defaults and route 255 across expanded, vanilla and re-enabled layouts");
    }

    static string lastSaveError;
    public static bool RecordSaveError(string message, bool save)
    {
        if (save) lastSaveError = message;
        return false;
    }

    static void Run()
    {
        var mnc = Assembly.LoadFrom("MoreNetworkCapacity.dll");
        var tiles = Assembly.LoadFrom("EightyOne2.dll");
        var harmony = new Harmony("mnc.test.water.race");
        foreach (string name in new[]{"Info","Warning"})
            harmony.Patch(mnc.GetType("MoreNetworkCapacity.Logging").GetMethod(name, Members),
                new HarmonyMethod(typeof(WaterAndRaceColorTests).GetMethod("Quiet")), null, null, null);
        var diagnostics = mnc.GetType("MoreNetworkCapacity.Patches.SaveFailureDiagnostics",true);
        harmony.CreateClassProcessor(diagnostics).Patch();
        var capture = mnc.GetType("MoreNetworkCapacity.ErrorReport").GetMethod("Capture",Members,null,
            new[]{typeof(string),typeof(string),typeof(bool)}, null);
        harmony.Patch(capture,new HarmonyMethod(typeof(WaterAndRaceColorTests).GetMethod("RecordSaveError")),null,null,null);
        RaceColor(mnc, harmony);
        EventRefresh(mnc, harmony);
        var water = tiles.GetType("EightyOne2.Patches.ExpandedWaterManager");
        var getters = new List<MethodInfo>();
        foreach (string channel in new[]{"Water","Sewage","Heating"})
        {
            var getter = water.GetMethod("get_" + channel + "PulseUnits", Members);
            Check(((Array)getter.Invoke(null,null)).Length == 32768, "Unexpected initial queue");
            getters.Add(getter);
        }
        var compat = mnc.GetType("MoreNetworkCapacity.Compatibility.EightyOne2Compatibility");
        foreach (string name in new[]{"ApplyEarly","Apply"})
            compat.GetMethod(name, Members, null, new[]{typeof(Harmony)}, null).Invoke(null, new object[]{harmony});
        var save = tiles.GetType("EightyOne2.Patches.WaterManagerDataPatches").GetMethod("CustomSerialize", Members);
        foreach (int count in new[]{32768,65024})
        {
            var queues = new WaterManager.PulseUnit[3][];
            for (int i=0; i<3; i++)
            {
                Array units = (Array)getters[i].Invoke(null,null);
                Check(units.Length == 65024, "Stale " + getters[i].Name);
                object unit = units.GetValue(count-1);
                unit.GetType().GetField("m_x").SetValue(unit, (ushort)(123+i));
                units.SetValue(unit,count-1);
                queues[i] = new WaterManager.PulseUnit[count];
            }
            var grid = (Array)save.Invoke(null, new object[]{new WaterManager.Cell[462*462],queues[0],queues[1],queues[2]});
            Check(grid.Length == 65536, "Wrong grid size");
            for (int i=0; i<3; i++) Check(queues[i][count-1].m_x == 123+i, "Wrong last unit for " + getters[i].Name);
            Console.WriteLine("PASS water, sewage and heating save at queue length " + count);
        }
        var emptyQueue = new WaterManager.PulseUnit[10];
        try
        {
            save.Invoke(null,new object[]{new WaterManager.Cell[0],emptyQueue,emptyQueue,emptyQueue});
            throw new Exception("Expected original save failure");
        }
        catch (TargetInvocationException error)
        {
            Check(error.InnerException is IndexOutOfRangeException, "Save exception changed");
        }
        Check(lastSaveError != null && lastSaveError.Contains("WaterManagerDataPatches.CustomSerialize") &&
            lastSaveError.Contains("Grid length=0") && lastSaveError.Contains("HeatingPulseUnits argument=10 field=65024 getter=65024"),
            "Missing failure-time buffer lengths: " + lastSaveError);
        Console.WriteLine("PASS save hooks installed, original exception and failure-time buffer lengths preserved");
    }
    static int Main()
    {
        try { Run(); return 0; }
        catch(Exception e) { while(e!=null){Console.WriteLine(e.GetType()+" "+e.Message+" "+e.StackTrace);e=e.InnerException;}return 1; }
    }
}
