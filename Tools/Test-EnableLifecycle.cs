using System;
using System.Collections.Generic;
using MoreNetworkCapacity;

namespace CitiesHarmony.API
{
    internal static class HarmonyHelper
    {
        internal static readonly List<Action> Pending = new List<Action>();
        internal static void DoOnHarmonyReady(Action callback) { Pending.Add(callback); }
        internal static void Flush()
        {
            Action[] callbacks = Pending.ToArray(); Pending.Clear();
            foreach (Action callback in callbacks) callback();
        }
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)] internal sealed class HarmonyPatch : Attribute { }
    internal sealed class Harmony
    {
        internal static int Active, Attempts;
        internal static bool FailPatch, FailUnpatch;
        internal Harmony(string id) { }
        internal Processor CreateClassProcessor(Type type) { return new Processor(type); }
        internal void UnpatchAll(string id)
        {
            if (FailUnpatch) throw new Exception("injected unpatch failure");
            Active = 0;
        }
        internal sealed class Processor
        {
            private readonly Type type;
            internal Processor(Type t) { type = t; }
            internal void Patch()
            {
                ++Attempts;
                if (type.Name.StartsWith("SimulationStepDiagnostics"))
                    throw new Exception("diagnostic fixture should never be in the core patch list");
                if (FailPatch && type == typeof(ZFailureFixture))
                    throw new Exception("injected class failure");
                ++Active;
            }
        }
    }
}
[HarmonyLib.HarmonyPatch] internal class AFixture { }
[HarmonyLib.HarmonyPatch] internal class ZFailureFixture { }
[HarmonyLib.HarmonyPatch] internal class SimulationStepDiagnosticsFixture { }
namespace MoreNetworkCapacity
{
    internal static class LoadStageJournal
    {
        internal static readonly List<string> Stages = new List<string>();
        internal static string Status { get { return "test journal"; } }
        internal static void Record(string stage) { Stages.Add(stage); }
    }
    internal static class ErrorReport
    {
        internal static void Start() { }
        internal static void Stop() { }
    }
    internal static class GameVersion { internal static bool IsSupported { get { return true; } } }
    internal static class BuildInfo { internal const string Version = "test"; }
    internal static class Capacity { internal const int ExpandedNodeCapacity = 65024, ExpandedSegmentCapacity = 65024; }
    internal static class PatchState
    {
        internal static bool ExpansionEnabled;
        internal static void MarkEnabled() { ExpansionEnabled = true; }
        internal static void MarkDisabled() { ExpansionEnabled = false; }
    }
    internal static class NetManagerCustomizer { internal static void CancelDeferredLoopLimitActivation() { } }
    internal static class SimulationStepDiagnostics
    {
        internal const bool Requested = false;
        internal static void Stop() { }
        internal static void InstallEarly(HarmonyLib.Harmony harmony) { }
        internal static void InitializeEarly() { }
    }
    internal static class Logging
    {
        internal static void Info(string message) { }
        internal static void Error(string message) { Console.WriteLine(message); }
    }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static int Resets;
        internal static void Reset() { ++Resets; }
        internal static void ApplyEarly(HarmonyLib.Harmony harmony) { }
    }
}
namespace MoreNetworkCapacity.Patches
{
    internal static class NetworkColorOffsetPatch { internal static void ResetFinalPass() { } }
    internal static class RenderGroupMeshDataPatch
    {
        internal static bool Fail;
        internal static void Apply(HarmonyLib.Harmony harmony)
        { if (Fail) throw new Exception("injected initialization failure"); }
    }
}
internal static class EnableLifecycleTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            HarmonyBootstrap.Enable();
            HarmonyBootstrap.Disable();
            CitiesHarmony.API.HarmonyHelper.Flush();
            Check(HarmonyLib.Harmony.Attempts == 0, "cancelled callback installed patches");

            HarmonyLib.Harmony.FailPatch = true;
            HarmonyBootstrap.Enable(); CitiesHarmony.API.HarmonyHelper.Flush();
            Check(HarmonyLib.Harmony.Active == 0 && !PatchState.ExpansionEnabled
                && HarmonyBootstrap.HarmonyInstance == null, "partial installation survived failure");
            Check(LoadStageJournal.Stages.Contains("enable: before patch ZFailureFixture")
                && LoadStageJournal.Stages.Contains("enable failed at ZFailureFixture; before rollback"),
                "failing patch and rollback must have checkpoints");

            HarmonyLib.Harmony.FailPatch = false;
            MoreNetworkCapacity.Patches.RenderGroupMeshDataPatch.Fail = true;
            HarmonyBootstrap.Enable(); CitiesHarmony.API.HarmonyHelper.Flush();
            Check(HarmonyLib.Harmony.Active == 0 && !PatchState.ExpansionEnabled,
                "late initialization failure was not rolled back");

            MoreNetworkCapacity.Patches.RenderGroupMeshDataPatch.Fail = false;
            int attempts = HarmonyLib.Harmony.Attempts;
            HarmonyBootstrap.Enable(); HarmonyBootstrap.Disable(); HarmonyBootstrap.Enable();
            CitiesHarmony.API.HarmonyHelper.Flush();
            Check(HarmonyLib.Harmony.Attempts - attempts == 2 && HarmonyLib.Harmony.Active == 2
                && PatchState.ExpansionEnabled, "stale callback or retry state invalid");

            HarmonyLib.Harmony.FailUnpatch = true;
            try { HarmonyBootstrap.Disable(); } catch (Exception) { }
            attempts = HarmonyLib.Harmony.Attempts;
            HarmonyBootstrap.Enable(); CitiesHarmony.API.HarmonyHelper.Flush();
            Check(!PatchState.ExpansionEnabled && HarmonyBootstrap.HarmonyInstance != null
                && HarmonyLib.Harmony.Attempts == attempts, "reinstalled over failed rollback");
            HarmonyLib.Harmony.FailUnpatch = false;
            HarmonyBootstrap.Disable();
            Check(HarmonyLib.Harmony.Active == 0 && HarmonyBootstrap.HarmonyInstance == null,
                "cleanup failed");
            Console.WriteLine("PASS: pending cancellation, partial and late failure rollback, retry, diagnostic exclusion, failed-unpatch protection.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
