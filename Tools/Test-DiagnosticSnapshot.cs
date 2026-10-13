using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MoreNetworkCapacity;

namespace UnityEngine
{
    public static class Application { public static string persistentDataPath, dataPath, version = "game-test", unityVersion = "unity-test"; }
    public static class SystemInfo { public static string operatingSystem = "test OS", graphicsDeviceName = "test GPU", graphicsDeviceVersion = "test API"; public static int graphicsShaderLevel = 30; }
    public struct Color { public float r, g, b, a; }
    public class Texture { public int GetInstanceID() { return 42; } }
    public sealed class Texture2D : Texture
    {
        public int width = 512, height = 512, Reads;
        public string format = "ARGB32";
        public bool FailRead;
        public Color GetPixel(int x, int y)
        {
            ++Reads;
            if (FailRead) throw new InvalidOperationException("not readable");
            return new Color { r = 1, g = 1, b = 1, a = 1 };
        }
    }
    public static class Shader { public static Texture Global; public static Texture GetGlobalTexture(string name) { return Global; } }
}
namespace ColossalFramework
{
    public static class Singleton<T>
    {
        public static bool exists;
        public static T Value;
        public static T instance { get { if (!exists) throw new Exception("Forbidden: create a game manager"); return Value; } }
    }
}
namespace ColossalFramework.Plugins
{
    public sealed class PluginManager
    {
        public static readonly PluginManager instance = new PluginManager();
        public IEnumerable<PluginInfo> GetPluginsInfo() { return new[] { new PluginInfo() }; }
        public sealed class PluginInfo
        {
            public bool isEnabled { get { return true; } }
            public string name { get { return "plugin-fixture"; } }
            public ulong publishedFileID { get { return 123; } }
            public List<Assembly> GetAssemblies() { return new List<Assembly> { Assembly.GetExecutingAssembly() }; }
            public object userModInstance { get { throw new Exception("Must not instantiate a user mod"); } }
        }
    }
}
namespace HarmonyLib
{
    public sealed class Patch
    {
        public string owner = "fixture-owner";
        public int priority = 400;
        public string[] before, after;
        public MethodInfo PatchMethod { get { return typeof(NetManager).GetMethod("UpdateColorMap"); } }
    }
    public sealed class Patches
    {
        public List<Patch> Prefixes = new List<Patch> { new Patch() };
        public List<Patch> Postfixes = new List<Patch>(), Transpilers = new List<Patch>(), Finalizers = new List<Patch>();
    }
    public static class Harmony { public static Patches GetPatchInfo(MethodBase method) { return new Patches(); } }
}
public sealed class Buffer<T> { public T[] m_buffer = new T[65024]; }
public struct NetNode { public void RefreshJunctionData() { } public void RenderInstance() { } public void PopulateGroupData() { } }
public struct NetSegment { public void RenderInstance() { } }
public sealed class NetManager { public Buffer<NetNode> m_nodes = new Buffer<NetNode>(); public Buffer<NetSegment> m_segments = new Buffer<NetSegment>(); public void UpdateColorMap() { } }
public sealed class BuildingManager { public void UpdateColorMap() { } }
public sealed class RenderManager
{
    public static int OBJECT_COLORMAP_WIDTH = 512, OBJECT_COLORMAP_HEIGHT = 512;
    public ushort[] m_indices = new ushort[180224];
    public UnityEngine.Texture2D m_objectColorMap;
    public void Awake() { }
}
namespace MoreNetworkCapacity
{
    public class Mod { }
    internal static class BuildInfo { internal const string Version = "snapshot-test"; }
    internal static class LoadStageJournal { internal static string LastStage = "stage-test", Status = "journal-test"; }
    internal static class PatchState { internal static bool ExpansionEnabled = true, SaveExpanded = true; }
    internal static class NetManagerDeserializeState { internal static bool LoadingExpanded = false; }
    internal static class Capacity
    {
        internal static int NodeLoopLimit = 65024, SegmentLoopLimit = 65024, NodeStorageSize = 65024;
        internal const int SegmentColorIndexBase = 49152;
        internal static int GetColorIndexCapacity() { return 180224; }
        internal static int GetNodeColorIndexBase() { return 114176; }
        internal static int GetEventColorIndexBase() { return 179200; }
    }
}
internal static class DiagnosticSnapshotTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
            UnityEngine.Application.persistentDataPath = root;
            UnityEngine.Application.dataPath = root;
            DiagnosticBundleInput menu = DiagnosticSnapshot.Capture();
            Check(menu.Summary.Contains("NetManager not present") && menu.Summary.Contains("RenderManager not present")
                && !menu.Summary.Contains("Forbidden"), "Main-menu export must not create game managers");
            Check(!Directory.Exists(root) && menu.Mods.Contains("plugin-fixture") && menu.Patches.Contains("fixture-owner"),
                "Snapshot must collect metadata without file writes or user-mod instantiation");
            UnityEngine.Texture2D texture = new UnityEngine.Texture2D();
            ColossalFramework.Singleton<NetManager>.Value = new NetManager();
            ColossalFramework.Singleton<NetManager>.exists = true;
            ColossalFramework.Singleton<RenderManager>.Value = new RenderManager { m_objectColorMap = texture };
            ColossalFramework.Singleton<RenderManager>.exists = true;
            UnityEngine.Shader.Global = texture;
            DiagnosticBundleInput city = DiagnosticSnapshot.Capture();
            Check(texture.Reads == 8 && city.Summary.Contains("matches manager=True") && city.Summary.Contains("Node buffer=65024"),
                "Live rendering capture must use only eight samples and identify global binding");
            texture.FailRead = true;
            UnityEngine.Shader.Global = new UnityEngine.Texture();
            DiagnosticBundleInput partial = DiagnosticSnapshot.Capture();
            Check(texture.Reads == 9 && partial.Summary.Contains("Pixel samples unavailable")
                && partial.Summary.Contains("matches manager=False") && partial.Mods.Contains("plugin-fixture"),
                "Unreadable texture must stop sampling but preserve other diagnostics");
            Console.WriteLine("PASS: main-menu singleton guard, zero snapshot disk writes, cached mod metadata, bounded color sampling, global-binding mismatch and partial-state export.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
