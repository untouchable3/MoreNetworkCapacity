using System;
using System.Reflection;
using System.Reflection.Emit;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal static class Logging
    {
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
        internal static void Error(string text) { }
    }
    internal static class Metadata { internal static bool IsExpandedSave() { return false; } }
    internal static class HarmonyBootstrap { internal static HarmonyLib.Harmony HarmonyInstance { get { return null; } } }
    internal static class Capacity
    {
        internal const int ExpandedNodeCapacity = 65024, ExpandedSegmentCapacity = 65024;
        internal const int VanillaNodeCapacity = 32768, VanillaSegmentCapacity = 36864;
        internal const int VanillaNodeColorIndexBase = 86016;
        internal static int GetAdaptiveRoadsSerializationCapacity() { return 4194304; }
    }
    internal static class NetManagerCustomizer
    {
        internal static bool LoopLimitActivationPending;
        internal static int Activations;
        internal static void ActivateDeferredLoopLimits() { ++Activations; LoopLimitActivationPending = false; }
    }
}
namespace MoreNetworkCapacity.Patches
{
    internal static class NetworkColorOffsetPatch
    {
        internal static bool LatePassPending = true;
        internal static int LatePasses, LifecyclePasses;
        internal static void ReapplyFinalPass(HarmonyLib.Harmony harmony) { ++LatePasses; LatePassPending = false; }
        internal static void ApplyAfterAdaptiveRoadsLoad(HarmonyLib.Harmony harmony) { ++LifecyclePasses; }
    }
}
namespace MoreNetworkCapacity.Compatibility
{
    internal delegate object IndexedElementFactory(int index);
    internal static class ThirdPartyCapacityCompatibility
    {
        internal static Assembly Assembly;
        internal static int AssemblyReads;
        internal static Assembly FindLoadedAssembly(string name) { ++AssemblyReads; return Assembly; }
        internal static Type FindType(Assembly assembly, string name, string label) { return typeof(FakeManager); }
        internal static FieldInfo FindField(Type type, string name) { return type.GetField(name); }
        internal static void RetryAdaptiveRoads(HarmonyLib.Harmony harmony) { AdaptiveRoadsCompatibility.Retry(harmony); }
    }
    internal static class ArrayCompatibility
    {
        internal static FakeManager Instance;
        internal static int InstanceReads, Expansions;
        internal static object GetStaticInstance(Type type, string property, string field, string label)
        { ++InstanceReads; return Instance; }
        internal static void ExpandDirect(FieldInfo field, object target, int oldSize, int size,
            IndexedElementFactory factory, bool create, string label) { ++Expansions; }
    }
    public struct FakeElement { public void Init(ushort id) { } }
    public sealed class FakeManager
    {
        public FakeElement[] NodeBuffer = new FakeElement[65024];
        public FakeElement[] SegmentBuffer = new FakeElement[65024];
        public FakeElement[] SegmentEndBuffer = new FakeElement[130048];
        public ulong[] m_updatedNodes = new ulong[1016];
        public ulong[] m_updatedSegments = new ulong[1016];
    }
}
public static class SimulationRetryTests
{
    private static readonly BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Static;
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static void Set(string field, object value)
    { typeof(AdaptiveRoadsCompatibility).GetField(field, Statics).SetValue(null, value); }
    private static void Call(string method, params object[] args)
    { typeof(AdaptiveRoadsCompatibility).GetMethod(method, Statics).Invoke(null, args); }
    private static void Ready()
    {
        foreach (string field in new[] { "_runtimePatchesReady", "_createHookInstalled",
            "_deserializeHookInstalled", "_lifeCycleLoadHookInstalled", "_asymPavementColorPrefixPatched" }) Set(field, true);
    }
    public static int Main()
    {
        try
        {
            Action step = (Action)Delegate.CreateDelegate(typeof(Action),
                typeof(MoreNetworkCapacity.Patches.DeferredLoopLimitActivationPatch).GetMethod("Postfix", Statics));
            AdaptiveRoadsCompatibility.Reset();
            ThirdPartyCapacityCompatibility.Assembly = null;
            MoreNetworkCapacity.NetManagerCustomizer.LoopLimitActivationPending = true;
            for (int i = 0; i < 100000; ++i) step();
            Check(ThirdPartyCapacityCompatibility.AssemblyReads == 1, "absent mod kept scanning assemblies");
            Check(MoreNetworkCapacity.NetManagerCustomizer.Activations == 1, "deferred activation changed");
            Check(MoreNetworkCapacity.Patches.NetworkColorOffsetPatch.LatePasses == 1, "late color pass lost or repeated");

            // Trigger the real assembly-load subscription, without patching a game.
            Ready();
            ArrayCompatibility.Instance = new FakeManager();
            Set("_preparedInstance", ArrayCompatibility.Instance);
            AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("AdaptiveRoads"), AssemblyBuilderAccess.Run);
            ThirdPartyCapacityCompatibility.Assembly = Assembly.GetExecutingAssembly();
            Check(AdaptiveRoadsCompatibility.RetryPending, "late assembly failed to resume compatibility");
            step();
            Check(!AdaptiveRoadsCompatibility.RetryPending, "ready compatibility did not stop polling");
            int reads = ArrayCompatibility.InstanceReads;
            for (int i = 0; i < 100000; ++i) step();
            Check(ArrayCompatibility.InstanceReads == reads, "ready state kept reading instance");

            // A new instance remains vanilla until its DeserializeImp hook.
            ArrayCompatibility.Instance = new FakeManager();
            Call("CreatePostfix", ArrayCompatibility.Instance);
            Check(ArrayCompatibility.Expansions == 0 && AdaptiveRoadsCompatibility.RetryPending,
                "ordinary-save Create expanded too early or failed to request work");
            Call("DeserializeImpPostfix", ArrayCompatibility.Instance);
            Check(ArrayCompatibility.Expansions == 5, "new instance was not prepared after reading");
            step();
            Check(!AdaptiveRoadsCompatibility.RetryPending, "replacement instance did not settle");
            Call("LifeCycleLoadPostfix");
            Check(AdaptiveRoadsCompatibility.RetryPending
                && MoreNetworkCapacity.Patches.NetworkColorOffsetPatch.LifecyclePasses == 1,
                "lifecycle color pass or wakeup lost");
            step();

            // Incomplete initialization must still receive bounded retries.
            ArrayCompatibility.Instance = null;
            AdaptiveRoadsCompatibility.Apply(null);
            int pendingReads = ArrayCompatibility.InstanceReads;
            for (int i = 0; i < 1000; ++i) step();
            Check(AdaptiveRoadsCompatibility.RetryPending && ArrayCompatibility.InstanceReads > pendingReads
                && ArrayCompatibility.InstanceReads - pendingReads <= 9, "pending initialization retry cadence invalid");
            ArrayCompatibility.Instance = new FakeManager();
            Set("_preparedInstance", ArrayCompatibility.Instance);
            Set("_asymPavementColorPrefixPatched", false);
            AdaptiveRoadsCompatibility.Apply(null);
            Check(AdaptiveRoadsCompatibility.RetryPending, "missing required hook treated as ready");

            CompatibilityRetryGate gate = new CompatibilityRetryGate(120);
            int generation;
            Check(gate.TryBegin(out generation), "initial retry missing");
            gate.Request(); gate.Complete(generation);
            Check(gate.Pending, "completion swallowed a newer request");
            Check(gate.TryBegin(out generation), "new request did not wake retry");
            gate.Complete(generation);
            Check(!gate.Pending, "completion failed");
            gate.Reset(); Check(gate.Pending, "reset failed to rearm");
            AdaptiveRoadsCompatibility.Reset();
            Console.WriteLine("PASS: 100,000 absent-mod steps use one assembly scan; 100,000 ready steps use zero instance reads.");
            Console.WriteLine("PASS: late assembly, instance replacement, vanilla read order, lifecycle color pass, pending retry, incomplete hooks, newer-request preservation and reset.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
