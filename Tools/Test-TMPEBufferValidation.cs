using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Capacity
    {
        internal const int ExpandedNodeCapacity = 65024, ExpandedSegmentCapacity = 65024;
    }
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { ++Errors; }
    }
}
public static class TMPEBufferValidationTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Refuses(Assembly assembly, string label)
    {
        try { TMPEBufferValidation.Validate(assembly); }
        catch (InvalidOperationException e) { Check(e.Message.Contains(label), "Wrong required-buffer failure"); return; }
        throw new Exception("Invalid TMPE preparation accepted: " + label);
    }
    private static Assembly Fixture()
    {
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName("TMPEBufferFixture"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("TMPEBufferFixture");
        Dictionary<string, TypeBuilder> types = new Dictionary<string, TypeBuilder>();
        int index = 0;
        foreach (TMPEBufferValidation.Target target in TMPEBufferValidation.Targets)
        {
            TypeBuilder type;
            if (!types.TryGetValue(target.Type, out type))
            {
                type = module.DefineType(target.Type, TypeAttributes.Public);
                type.DefineDefaultConstructor(MethodAttributes.Public);
                if (!target.Static) type.DefineField("Instance", type, FieldAttributes.Public | FieldAttributes.Static);
                types.Add(target.Type, type);
            }
            // Exercise both direct fields and compiler-generated property backing fields.
            string name = (++index % 2 == 0) ? "<" + target.Member + ">k__BackingField" : target.Member;
            type.DefineField(name, typeof(byte[]), FieldAttributes.Public | (target.Static ? FieldAttributes.Static : 0));
        }
        foreach (TypeBuilder builder in types.Values)
        {
            Type type = builder.CreateType(); FieldInfo instance = type.GetField("Instance");
            if (instance != null) instance.SetValue(null, Activator.CreateInstance(type));
        }
        string[] holders = { "NetNodeExtensions", "NetSegmentExtensions", "NetLaneExtensions" };
        string[] members = { "_nodeBuffer", "_segBuffer", "_laneBuffer" };
        for (int i = 0; i < holders.Length; ++i)
        {
            TypeBuilder holder = module.DefineType("TrafficManager.Util.Extensions." + holders[i], TypeAttributes.Public);
            holder.DefineField(members[i], typeof(byte[]), FieldAttributes.Public | FieldAttributes.Static);
            holder.CreateType();
        }
        foreach (TMPEBufferValidation.Target target in TMPEBufferValidation.Targets)
        {
            FieldInfo field = TMPEBufferValidation.Resolve(assembly, target);
            object value = target.Static ? null : field.DeclaringType.GetField("Instance").GetValue(null);
            field.SetValue(value, new byte[target.Length]);
        }
        return assembly;
    }
    public static int Main(string[] args)
    {
        try
        {
            // Metadata-only check against the installed DLL; never construct its managers.
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                string name = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in args) { string path = System.IO.Path.Combine(dir, name);
                    if (System.IO.File.Exists(path)) return Assembly.LoadFrom(path); }
                return null;
            };
            Assembly actual = Assembly.LoadFrom(System.IO.Path.Combine(args[0], "TrafficManager.dll"));
            foreach (TMPEBufferValidation.Target target in TMPEBufferValidation.Targets)
                TMPEBufferValidation.Resolve(actual, target);
            string[] holders = { "NetNodeExtensions", "NetSegmentExtensions", "NetLaneExtensions" };
            string[] members = { "_nodeBuffer", "_segBuffer", "_laneBuffer" };
            for (int i = 0; i < holders.Length; ++i)
            {
                Type type = actual.GetType("TrafficManager.Util.Extensions." + holders[i], true);
                FieldInfo field = type.GetField(members[i], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                Check(field != null && field.FieldType.IsArray, "Actual extension buffer changed");
            }
            Assembly fixture = Fixture(); TMPEBufferValidation.Validate(fixture);
            byte[][] expected = { new byte[65024], new byte[65024], new byte[262144] };
            for (int i = 0; i < holders.Length; ++i)
            {
                FieldInfo field = fixture.GetType("TrafficManager.Util.Extensions." + holders[i]).GetField(members[i]);
                field.SetValue(null, expected[i]);
                TMPEBufferValidation.ValidateReference(fixture, holders[i], members[i], expected[i]);
                // Equal length is insufficient: the old pool reference can contain stale city data.
                field.SetValue(null, new byte[expected[i].Length]); bool refused = false;
                try { TMPEBufferValidation.ValidateReference(fixture, holders[i], members[i], expected[i]); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "Same-sized stale network reference accepted");
                field.SetValue(null, null); refused = false;
                try { TMPEBufferValidation.ValidateReference(fixture, holders[i], members[i], expected[i]); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "Null network reference accepted");
                field.SetValue(null, expected[i]);
            }
            TMPEBufferValidation.ValidateRuntimeReferences(fixture, expected[0], expected[1], expected[2]);
            foreach (TMPEBufferValidation.Target target in TMPEBufferValidation.Targets)
            {
                FieldInfo field = TMPEBufferValidation.Resolve(fixture, target);
                object value = target.Static ? null : field.DeclaringType.GetField("Instance").GetValue(null);
                object original = field.GetValue(value);
                field.SetValue(value, new byte[target.Length - 1]); Refuses(fixture, target.Member);
                field.SetValue(value, null); Refuses(fixture, target.Member);
                field.SetValue(value, original);
            }
            TMPEBufferValidation.Target split = TMPEBufferValidation.Targets[3];
            FieldInfo splitField = TMPEBufferValidation.Resolve(fixture, split);
            object manager = splitField.DeclaringType.GetField("Instance").GetValue(null);
            splitField.SetValue(manager, new byte[73728]); object old = splitField.GetValue(manager);
            ArrayCompatibility.ExpandSplit(splitField, manager, 36864, 65024,
                delegate(int id, bool start) { throw new InvalidOperationException("injected migration failure"); }, "fixture");
            Check(MoreNetworkCapacity.Logging.Errors == 1 && Object.ReferenceEquals(old, splitField.GetValue(manager)),
                "Failed migration unexpectedly published a replacement");
            Refuses(fixture, split.Member);
            ArrayCompatibility.ExpandSplit(splitField, manager, 36864, 65024, null, "recovered fixture");
            TMPEBufferValidation.Validate(fixture);
            FieldInfo singleton = splitField.DeclaringType.GetField("Instance");
            singleton.SetValue(null, null); Refuses(fixture, split.Type); singleton.SetValue(null, manager);
            for (int missing = 0; missing < 4; ++missing)
            {
                bool refused = false;
                try { TMPEBufferValidation.ValidateIndexPatches(missing != 0, missing != 1, missing != 2, missing != 3); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "Missing split index patch accepted");
            }
            TMPEBufferValidation.ValidateIndexPatches(true, true, true, true);
            LaneFixture lane = new LaneFixture();
            TMPELaneConnectionPreparation.PrepareInstance(lane);
            Check(lane.Road.connectionDataBase_ != null && lane.Track.connectionDataBase_ != null, "Lane databases missing");
            lane.Fail = true; bool failed = false;
            try { TMPELaneConnectionPreparation.PrepareInstance(lane); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "Lane initialization exception swallowed");
            lane.Fail = false; lane.SkipTrack = true; failed = false;
            try { TMPELaneConnectionPreparation.PrepareInstance(lane); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "Uninitialized track database accepted");
            Type actualLane = actual.GetType("TrafficManager.Manager.Impl.LaneConnection.LaneConnectionManager", true);
            foreach (string name in new[] { "Road", "Track" })
            {
                FieldInfo member = actualLane.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Check(member != null && member.FieldType.GetField("connectionDataBase_", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null,
                    "Actual lane-connection database shape changed");
            }
            Console.WriteLine("PASS: 20 actual TMPE fields plus 3 live buffer identities, short/null/unavailable/stale refusal, swallowed migration failure refusal, recovery and required index gates.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
public sealed class LaneSubFixture { public object connectionDataBase_; }
public sealed class LaneFixture
{
    public LaneSubFixture Road = new LaneSubFixture(), Track = new LaneSubFixture();
    public bool Fail, SkipTrack;
    public void OnBeforeLoadData()
    {
        if (Fail) throw new Exception("injected lane failure");
        Road.connectionDataBase_ = new object();
        Track.connectionDataBase_ = SkipTrack ? null : new object();
    }
}
