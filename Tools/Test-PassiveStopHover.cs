using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;

class StopHoverTest
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static int Refreshes;
    public static bool SkipUi() { Refreshes++; return false; }
    static int Main()
    {
        try
        {
            var asm = Assembly.LoadFrom("MoreNetworkCapacity.dll");
            var inspector = asm.GetType("MoreNetworkCapacity.Diagnostics.Inspector", true);
            var overlay = asm.GetType("MoreNetworkCapacity.Diagnostics.StopHoverOverlay",true);
            var harmony = new Harmony("mnc.test.passiveHover");
            harmony.CreateClassProcessor(overlay).Patch();
            harmony.Patch(inspector.GetMethod("RefreshControls", Members),
                new HarmonyMethod(typeof(StopHoverTest).GetMethod("SkipUi")),null,null,null);
            var instance = FormatterServices.GetUninitializedObject(inspector);
            var toggle = inspector.GetMethod("Toggle", Members);
            var mode = inspector.GetField("mode",Members);
            var pickTool = inspector.GetField("tool",Members);
            var stop = Enum.Parse(mode.FieldType,"Stop");
            var hover = overlay.GetField("Hovered",Members);
            foreach (string exit in new[]{"Toggle","OnDisable"})
            {
                toggle.Invoke(instance,new[]{stop});
                if (!Equals(mode.GetValue(instance),stop) || pickTool.GetValue(instance)!=null)
                    throw new Exception("Stop hover required an active picking tool");
                var id = InstanceID.Empty;
                id.NetNode = 65023;
                hover.SetValue(null,id);
                if (exit=="Toggle") toggle.Invoke(instance,new[]{stop});
                else inspector.GetMethod(exit,Members).Invoke(instance,null);
                if (mode.GetValue(instance).ToString()!="None" || ((InstanceID)hover.GetValue(null)).Index!=0)
                    throw new Exception("Passive hover was not cleared");
            }
            if (Refreshes!=3) throw new Exception("Unexpected control updates");
            Console.WriteLine("PASS passive stop toggle, no tool controller required, overlay install and stop/disable cleanup");
            return 0;
        }
        catch(Exception e) {while(e!=null){Console.WriteLine(e.GetType()+" "+e.Message);e=e.InnerException;}return 1;}
    }
}
