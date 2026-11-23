using System;
using System.Collections;
using System.Reflection;
using System.IO;

public static class DiagnosticsConfigurationTests
{
    private static string[] directories;
    public static int Main(string[] args)
    {
        try
        {
            directories = args;
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                foreach (string directory in directories)
                {
                    string path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            Run();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); }

    private static void Run()
    {
        Assembly mod = Assembly.LoadFrom(Path.Combine(directories[0], "MoreNetworkCapacity.dll"));
        Type diagnostics = mod.GetType("MoreNetworkCapacity.SimulationStepDiagnostics", true);
        BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        bool expected = Environment.GetEnvironmentVariable("MNC_DIAGNOSTICS") == "1";
        Check((bool)diagnostics.GetField("Requested", statics).GetValue(null) == expected,
            "diagnostics opt-in incorrect");
        Type bootstrap = mod.GetType("MoreNetworkCapacity.HarmonyBootstrap", true);
        IEnumerable patches = (IEnumerable)bootstrap.GetMethod("GetPatchTypes", statics).Invoke(null, null);
        bool deferredFound = false;
        bool phaseFound = false, readStateFound = false, tlmReadStateFound = false;
        bool saveListFound = false, afterSavingFound = false;
        foreach (Type patch in patches)
        {
            Check(!patch.Name.StartsWith("SimulationStepDiagnostics"), "core owner contains tracing patch");
            if (patch.Name == "DeferredLoopLimitActivationPatch") deferredFound = true;
            if (patch.Name == "NodeSimulationPhasePatch") phaseFound = true;
            if (patch.Name == "SimulationDataReadStatePatch") readStateFound = true;
            if (patch.Name == "TLMStopStatisticsReadResetPatch") tlmReadStateFound = true;
            if (patch.Name == "SaveListMetadataRefreshPatch") saveListFound = true;
            if (patch.Name == "SaveListAfterSavingPatch") afterSavingFound = true;
        }
        Check(deferredFound, "functional first-frame activation lost");
        Check(phaseFound && readStateFound, "required signal phase or incoming-read lifecycle patch excluded");
        Check(tlmReadStateFound, "TLM statistics incoming-city reset patch excluded");
        Check(saveListFound && afterSavingFound, "save-list repair missing from core patch discovery");
        Type capacity = mod.GetType("MoreNetworkCapacity.Capacity", true);
        FieldInfo nodeLimit = capacity.GetField("NodeLoopLimit");
        object priorLimit = nodeLimit.GetValue(null);
        try
        {
            foreach (int value in new[] { 32768, 65024, 32768 })
            {
                nodeLimit.SetValue(null, value);
                Check((int)capacity.GetMethod("GetNodeSimulationPhaseDivisor").Invoke(null, null) == value,
                    "built phase divisor does not follow live node scheduling");
            }
        }
        finally { nodeLimit.SetValue(null, priorLimit); }
        diagnostics.GetField("_armed", statics).SetValue(null, 1);
        diagnostics.GetField("_enabled", statics).SetValue(null, 1);
        diagnostics.GetField("_completedFrames", statics).SetValue(null, 3);
        diagnostics.GetMethod("Stop", statics).Invoke(null, null);
        Check((int)diagnostics.GetField("_armed", statics).GetValue(null) == 0
            && (int)diagnostics.GetField("_enabled", statics).GetValue(null) == 0
            && (int)diagnostics.GetField("_completedFrames", statics).GetValue(null) == 0,
            "diagnostics stop/reload state invalid");
        Console.WriteLine("PASS: built DLL diagnostics opt-in=" + expected
            + ", core patch exclusion, functional activation retained, stop/reload reset.");
    }
}
