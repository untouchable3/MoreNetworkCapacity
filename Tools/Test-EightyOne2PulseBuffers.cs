using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using ColossalFramework.IO;
using HarmonyLib;

class ElectricityBufferTests
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    public static ElectricityManager Manager;
    public static int NodeCount;
    public static bool Quiet(string message) { Console.WriteLine(message); return false; }

    // Supply a headless manager, persisted node width and logging sink at the scene boundaries only.
    public static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction c in instructions)
        {
            MethodInfo m = c.operand as MethodInfo;
            if (m != null && m.Name == "get_instance" && m.ReturnType == typeof(ElectricityManager))
            { c.opcode = OpCodes.Ldsfld; c.operand = typeof(ElectricityBufferTests).GetField("Manager"); }
            else if (m != null && m.Name == "GetElectricityNodeGroupLength")
            { c.opcode = OpCodes.Ldsfld; c.operand = typeof(ElectricityBufferTests).GetField("NodeCount"); }
            else if (m != null && m.DeclaringType.FullName == "AlgernonCommons.Logging" && m.Name == "Message")
            { c.opcode = OpCodes.Pop; c.operand = null; }
            yield return c;
        }
    }

    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Run(string fixturePath)
    {
        Assembly mnc = Assembly.LoadFrom("MoreNetworkCapacity.dll"), tiles = Assembly.LoadFrom("EightyOne2.dll");
        Type owner = tiles.GetType("EightyOne2.Patches.ExpandedElectricityManager",true);
        MethodInfo getter = owner.GetMethod("get_PulseUnits",Members);
        Check(((Array)getter.Invoke(null,null)).Length == 32768,"Cold queue length");
        var harmony = new Harmony("mnc.offline.power");
        Type logging = mnc.GetType("MoreNetworkCapacity.Logging",true);
        foreach(string name in new[]{"Info","Warning"}) harmony.Patch(logging.GetMethod(name,Members),new HarmonyMethod(typeof(ElectricityBufferTests).GetMethod("Quiet")),null,null,null);
        Type compat = mnc.GetType("MoreNetworkCapacity.Compatibility.EightyOne2Compatibility",true);
        compat.GetMethod("ApplyEarly",Members).Invoke(null,new object[]{harmony});
        compat.GetMethod("Apply",Members,null,new[]{typeof(Harmony)},null).Invoke(null,new object[]{harmony});
        Array units = (Array)getter.Invoke(null,null);
        Check(units.Length == 65024,"Stale getter after warm-up");
        Type container = tiles.GetType("EightyOne2.Serialization.ElectricityDataContainer",true);
        foreach(string name in new[]{"Serialize","Deserialize"}) {
            var scene = new HarmonyMethod(typeof(ElectricityBufferTests).GetMethod("Scene"));
            scene.priority = Priority.Last;
            harmony.Patch(container.GetMethod(name,Members),null,null,scene,null);
        }
        Manager=(ElectricityManager)FormatterServices.GetUninitializedObject(typeof(ElectricityManager));
        NodeCount=32768;
        Manager.m_nodeGroups=new ushort[NodeCount];
        byte[] original=File.ReadAllBytes(fixturePath);
        var ctor=typeof(DataSerializer).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic)[0];
        var data=(IDataContainer)Activator.CreateInstance(container,true);
        using(var stream=new MemoryStream(original)) {
            var reader=(DataSerializer)ctor.Invoke(new object[]{stream,DataSerializer.Mode.Memory,false});
            reader.ReadUInt32(); reader.ReadUInt8(); reader.ReadUInt8(); reader.ReadUniqueString();
            data.Deserialize(reader);
            Check(stream.Position==stream.Length,"Incomplete original block");
        }
        FieldInfo gridField=typeof(ElectricityManager).GetField("m_electricityGrid",Members);
        var grid=(ElectricityManager.Cell[])gridField.GetValue(Manager);
        Check(grid.Length==462*462,"Source grid");
        using(var baseline=new MemoryStream()) {
            var writer=(DataSerializer)ctor.Invoke(new object[]{baseline,DataSerializer.Mode.Memory,true});
            data.Serialize(writer);
            byte[] body=baseline.ToArray();
            int headerLength=original.Length-body.Length;
            Check(headerLength>0 && headerLength<512,"Unexpected original payload length");
            for(int i=0;i<body.Length;i++) Check(body[i]==original[headerLength+i],"Original payload changed");
        }
        NodeCount=65024;
        Array.Resize(ref Manager.m_nodeGroups,NodeCount);
        var pulseType=units.GetType().GetElementType();
        object marker=units.GetValue(65023);
        pulseType.GetField("m_x").SetValue(marker,(ushort)123);
        pulseType.GetField("m_z").SetValue(marker,(ushort)234);
        units.SetValue(marker,65023);
        var converted=new ElectricityManager.PulseUnit[65024];
        var save=tiles.GetType("EightyOne2.Patches.ElectricityManagerDataPatches").GetMethod("CustomSerialize",Members);
        var central=(ElectricityManager.Cell[])save.Invoke(null,new object[]{grid,new ElectricityManager.PulseGroup[1024],converted});
        Check(central.Length==65536 && converted[65023].m_x==123 && converted[65023].m_z==234,"High queue or central grid");
        for(int i=0;i<central.Length;i++) Check(central[i].Equals(grid[(i/256+103)*462+i%256+103]),"Cell mismatch");
        // Exercise the full 81 Tiles electricity payload, including all outer cells.
        using(var stream=new MemoryStream()) {
            var writer=(DataSerializer)ctor.Invoke(new object[]{stream,DataSerializer.Mode.Memory,true});
            Manager.m_nodeGroups[65023]=0;
            typeof(ElectricityManager).GetField("m_pulseGroupCount",Members).SetValue(Manager,1);
            typeof(ElectricityManager).GetField("m_pulseUnitStart",Members).SetValue(Manager,65023);
            typeof(ElectricityManager).GetField("m_pulseUnitEnd",Members).SetValue(Manager,0);
            pulseType.GetField("m_node").SetValue(marker,(ushort)65023);
            units.SetValue(marker,65023);
            data.Serialize(writer);
            stream.Position=0;
            var reader=(DataSerializer)ctor.Invoke(new object[]{stream,DataSerializer.Mode.Memory,false});
            Manager.m_nodeGroups[65023]=65535;
            data.Deserialize(reader);
            Check(stream.Position==stream.Length && Manager.m_nodeGroups[65023]==0,"Expanded block round trip");
            Check(units.GetValue(0).Equals(marker),"Wrapped queue entry lost on reload");
            var again=(ElectricityManager.Cell[])gridField.GetValue(Manager);
            for(int i=0;i<grid.Length;i++) Check(grid[i].Equals(again[i]),"Outer grid mismatch");
        }
        Console.WriteLine("PASS warm queue, high slot, central cells and complete electricity block round trip.");
    }
    static int Main(string[] args) {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: ElectricityBufferTests <electricity-block-fixture>"); return 2; }
        try { Run(args[0]); return 0; } catch(Exception ex) { while(ex!=null){Console.WriteLine(ex.GetType()+" "+ex.Message);ex=ex.InnerException;}return 1; }
    }
}
