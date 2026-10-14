using System;
using System.Reflection;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace HarmonyLib { internal class Harmony { } }
namespace MoreNetworkCapacity
{
    internal static class Logging
    {
        internal static int Errors;
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { ++Errors; }
    }
}
namespace MoreNetworkCapacity.Patches
{
    internal static class NetworkColorOffsetPatch { internal static void ApplyFinalPass(Harmony h) { } }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal class CompatBase
    {
        internal static int Calls;
        internal static void Reset() { }
        internal static void Apply(Harmony h) { ++Calls; }
    }
    internal class TMPECompatibility : CompatBase
    {
        internal static bool FailEarly, FailLoad, FailApply, Expanded;
        internal new static void Apply(Harmony h)
        { if (FailApply) throw new InvalidOperationException("bad TMPE final buffers"); ++Calls; }
        internal static void ApplyEarly(Harmony h) { if (FailEarly) throw new InvalidOperationException("bad TMPE pattern"); }
        internal static void PrepareForNetworkDeserialize(Harmony h, bool expanded)
        { Expanded = expanded; if (FailLoad) throw new InvalidOperationException("bad TMPE load pattern"); }
    }
    internal class AdaptiveRoadsCompatibility : CompatBase
    {
        internal static bool Fail;
        internal static void PrepareForNetworkDeserialize(Harmony h, bool expanded)
        { if (Fail) throw new Exception("optional compatibility failure"); }
        internal static void Retry(Harmony h) { }
    }
    internal class NetworkSkinsCompatibility : CompatBase { }
    internal class NodeControllerCompatibility : CompatBase { }
    internal class EightyOne2Compatibility : CompatBase
    { internal static int EarlyCalls; internal static void ApplyEarly(Harmony h) { ++EarlyCalls; } }
    internal class StaticBufferCompatibility : CompatBase
    {
        internal static int RefreshCalls;
        internal static void ApplyAll(Harmony h) { ++RefreshCalls; }
        internal static void ApplyEarly(Harmony h) { ++Calls; }
    }
    internal class ZoningAdjusterCompatibility
    { internal static int EarlyCalls; internal static void Apply() { } internal static void Reset() { }
        internal static void ApplyEarly(Harmony h) { ++EarlyCalls; } }
    internal static class ArrayCompatibility
    {
        internal static FieldInfo FindField(Type type, string name) { return null; }
        internal static FieldInfo FindBackingField(Type type, string name) { return null; }
    }
}
public static class TMPEFailureGateTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Main()
    {
        try
        {
            TMPECompatibility.FailEarly = true; bool refused = false;
            try { ThirdPartyCapacityCompatibility.ApplyEarly(null); } catch (InvalidOperationException) { refused = true; }
            Check(refused && CompatBase.Calls == 0, "Required early TMPE failure swallowed");
            TMPECompatibility.FailEarly = false; TMPECompatibility.FailLoad = true; refused = false;
            try { ThirdPartyCapacityCompatibility.PrepareForNetworkDeserialize(null, true); } catch (InvalidOperationException) { refused = true; }
            Check(refused && TMPECompatibility.Expanded, "Required TMPE load failure swallowed");
            TMPECompatibility.FailApply = true; refused = false;
            int calls = CompatBase.Calls;
            try { ThirdPartyCapacityCompatibility.ApplyAll(null); } catch (InvalidOperationException) { refused = true; }
            Check(refused && CompatBase.Calls == calls, "Required final TMPE failure swallowed");
            TMPECompatibility.FailApply = false;
            TMPECompatibility.FailLoad = false; AdaptiveRoadsCompatibility.Fail = true;
            int errors = MoreNetworkCapacity.Logging.Errors;
            ThirdPartyCapacityCompatibility.PrepareForNetworkDeserialize(null, false);
            Check(!TMPECompatibility.Expanded && MoreNetworkCapacity.Logging.Errors == errors + 1,
                "Vanilla flag lost or unrelated optional failure handling changed");
            Check(EightyOne2Compatibility.EarlyCalls == 1 && ZoningAdjusterCompatibility.EarlyCalls == 1,
                "Early optional serialization preparation missing");
            AdaptiveRoadsCompatibility.Fail = false;
            ThirdPartyCapacityCompatibility.ApplyEarly(null);
            Check(EightyOne2Compatibility.EarlyCalls == 2 && ZoningAdjusterCompatibility.EarlyCalls == 2,
                "Enable-time optional serialization hooks missing");
            Console.WriteLine("PASS: actual dispatcher gates required TMPE enable/load/final failures, preserves vanilla flags/optional isolation and installs early serialization hooks.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
