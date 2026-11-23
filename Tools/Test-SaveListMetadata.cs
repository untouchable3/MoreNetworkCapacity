using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using MoreNetworkCapacity.Patches;

namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static bool operator ==(Object a, Object b)
        { return object.ReferenceEquals(a, b) || (object.ReferenceEquals(b, null) && !object.ReferenceEquals(a, null) && a.Destroyed); }
        public static bool operator !=(Object a, Object b) { return !(a == b); }
        public override bool Equals(object b) { return object.ReferenceEquals(this, b); }
        public override int GetHashCode() { return base.GetHashCode(); }
    }
}
namespace ColossalFramework.IO { public static class DataLocation { public static string saveLocation; } }
namespace ColossalFramework.Threading
{
    public class Dispatcher
    {
        public static Dispatcher currentSafe;
        public readonly Queue<Action> Work = new Queue<Action>();
        public bool Fail;
        public void Dispatch(Action a)
        {
            if (currentSafe == this) throw new InvalidOperationException("Already in the same thread. Call directly");
            if (Fail) throw new IOException("dispatch failure"); Work.Enqueue(a);
        }
        public void Drain()
        {
            Dispatcher previous = currentSafe; currentSafe = this;
            try { int n = 0; while (Work.Count != 0) { if (++n > 30) throw new Exception("refresh loop"); Work.Dequeue()(); } }
            finally { currentSafe = previous; }
        }
    }
    public static class ThreadHelper { public static readonly Dispatcher dispatcher = new Dispatcher(); }
}
namespace ColossalFramework.Packaging
{
    public class Package
    {
        public string packagePath;
        public class Asset { public Package package; }
    }
    public static class PackageManager
    {
        public static readonly List<Package.Asset> Assets = new List<Package.Asset>();
        public static readonly List<string> Calls = new List<string>();
        public static Action<string> Updating;
        public static int Revisions;
        public static bool IsCloudPath(string p) { return p.StartsWith("cloud:"); }
        public static IEnumerable<Package.Asset> FilterAssets(int[] types)
        { foreach (Package.Asset a in Assets) yield return a; }
        public static void Update(string p)
        {
            Calls.Add(p);
            if (Updating != null) Updating(p);
        }
    }
}
public static class UserAssetType { public const int SaveGameMetaData = 101; }
public class LoadPanel : UnityEngine.Object
{
    public int Refreshes;
    public bool Throw;
    public void Refresh()
    {
        bool entered;
        if (!SaveListMetadataRefresh.Begin(this, out entered)) return;
        try { ++Refreshes; if (Throw) throw new InvalidDataException("real panel error"); }
        finally { SaveListMetadataRefresh.End(entered); }
    }
}
public class SavePanel : LoadPanel { public static bool isSaving; }
namespace MoreNetworkCapacity
{
    public static class Logging
    { public static readonly List<string> Warnings = new List<string>(); public static void Warning(string s) { Warnings.Add(s); } }
}
public static class SaveListMetadataTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ColossalFramework.Packaging.Package.Asset Asset(string path)
    { return new ColossalFramework.Packaging.Package.Asset { package = new ColossalFramework.Packaging.Package { packagePath = path } }; }
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 1 && args[1] == "real-stream") { RealStream(args[0], args[2]); return 0; }
            Run(args[0]); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Run(string root)
    {
        Directory.CreateDirectory(root);
        string saves = Path.Combine(root, "Saves"); Directory.CreateDirectory(saves);
        ColossalFramework.IO.DataLocation.saveLocation = saves;
        string autosave = Path.Combine(saves, "AutoSave.crp"); File.WriteAllText(autosave, "fixture");
        string outside = Path.Combine(root, "Outside.crp"); File.WriteAllText(outside, "outside");
        string sibling = Path.Combine(root, "SavesExtra"); Directory.CreateDirectory(sibling);
        string neighbor = Path.Combine(sibling, "AutoSave.crp"); File.WriteAllText(neighbor, "neighbor");
        var pm = ColossalFramework.Packaging.PackageManager.Assets;
        pm.Add(Asset(autosave)); pm.Add(Asset(autosave)); pm.Add(Asset(null)); pm.Add(Asset("cloud:AutoSave.crp"));
        pm.Add(Asset(outside)); pm.Add(Asset(neighbor)); pm.Add(Asset(Path.Combine(saves, "missing.crp")));
        var calls = ColossalFramework.Packaging.PackageManager.Calls;
        var dispatcher = ColossalFramework.Threading.ThreadHelper.dispatcher;
        ColossalFramework.Threading.Dispatcher.currentSafe = dispatcher;
        LoadPanel load = new LoadPanel(); SavePanel save = new SavePanel();
        SaveListMetadataRefresh.Reset(); SavePanel.isSaving = true;
        load.Refresh(); load.Refresh(); save.Refresh();
        Check(calls.Count == 0 && load.Refreshes == 0 && save.Refreshes == 0, "save in progress performed IO or rebuilt panels");
        QueueFromWorker(); dispatcher.Drain();
        Check(calls.Count == 0, "still-saving completion refreshed indexes");
        SavePanel.isSaving = false;
        QueueFromWorker();
        Check(load.Refreshes == 0, "worker completion touched Unity inline");
        dispatcher.Drain();
        Check(load.Refreshes == 1 && save.Refreshes == 1 && calls.Count == 2, "deferred panels not deduplicated/resumed");
        foreach (string p in calls) Check(p == autosave, "cloud, transient, duplicate or out-of-save path updated");
        calls.Clear(); bool changed = false;
        ColossalFramework.Packaging.PackageManager.Updating = delegate(string p)
        {
            // A package replacement raises a nested panel event and changes the collection.
            if (!changed) { changed = true; pm.Add(Asset(outside)); load.Refresh(); save.Refresh(); }
        };
        load.Refresh(); dispatcher.Drain();
        Check(changed && calls.Count == 3, "nested package events were recursive or snapshot invalidated");
        ColossalFramework.Packaging.PackageManager.Updating = delegate { throw new InvalidDataException("bad index"); };
        int before = load.Refreshes; load.Refresh(); dispatcher.Drain();
        Check(load.Refreshes == before + 1 && MoreNetworkCapacity.Logging.Warnings.Count != 0, "bad index swallowed normal panel refresh");
        ColossalFramework.Packaging.PackageManager.Updating = null;
        load.Throw = true; bool threw = false;
        try { load.Refresh(); } catch (InvalidDataException) { threw = true; }
        Check(threw, "original panel exception was suppressed"); load.Throw = false; load.Refresh();
        SavePanel.isSaving = true; load.Refresh(); SavePanel.isSaving = false;
        QueueFromWorker(); SaveListMetadataRefresh.Reset();
        before = load.Refreshes; dispatcher.Drain(); Check(load.Refreshes == before, "queued work survived reset/disable");
        SavePanel.isSaving = true; load.Refresh(); SavePanel.isSaving = false; load.Destroyed = true;
        QueueFromWorker(); dispatcher.Drain(); Check(load.Refreshes == before, "destroyed panel was invoked");
        load.Destroyed = false;
        dispatcher.Fail = true; QueueFromWorker(); dispatcher.Fail = false;
        Check(MoreNetworkCapacity.Logging.Warnings.Count >= 2, "dispatcher failure escaped save callback");
        int warnings = MoreNetworkCapacity.Logging.Warnings.Count;
        SavePanel.isSaving = true; load.Refresh(); SavePanel.isSaving = false;
        before = load.Refreshes; SaveListMetadataRefresh.QueuePending();
        Check(load.Refreshes == before + 1 && dispatcher.Work.Count == 0, "UI-thread refresh was not executed directly");
        Check(MoreNetworkCapacity.Logging.Warnings.Count == warnings, "same-thread dispatcher rejection was caught instead of prevented");
        MethodInfo finalizer = typeof(SaveListMetadataRefreshPatch).GetMethod("Finalizer", BindingFlags.NonPublic | BindingFlags.Static);
        Exception expected = new InvalidDataException("original");
        Check(object.ReferenceEquals(finalizer.Invoke(null, new object[] { expected, false }), expected), "Harmony finalizer changed original error");
        Console.WriteLine("PASS: save-in-progress deferral, worker dispatch and same-UI-thread direct execution, deduplication, snapshot mutation/reentry, path scope, real errors, destroyed panels and reset generation.");
    }
    private static void QueueFromWorker()
    {
        ColossalFramework.Threading.Dispatcher prior = ColossalFramework.Threading.Dispatcher.currentSafe;
        ColossalFramework.Threading.Dispatcher.currentSafe = null;
        try { SaveListMetadataRefresh.QueuePending(); }
        finally { ColossalFramework.Threading.Dispatcher.currentSafe = prior; }
    }
    private static void RealStream(string root, string managed)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        { string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; };
        Assembly a = Assembly.LoadFrom(Path.Combine(managed, "ColossalManaged.dll"));
        Type fileType = a.GetType("ColossalFramework.Packaging.FilePackage", true);
        Type packageType = a.GetType("ColossalFramework.Packaging.Package", true);
        Type assetType = a.GetType("ColossalFramework.Packaging.Package+Asset", true);
        Directory.CreateDirectory(root); string path = Path.Combine(root, "stream-replay.bin");
        object file = FormatterServices.GetUninitializedObject(fileType);
        object package = FormatterServices.GetUninitializedObject(packageType);
        object asset = FormatterServices.GetUninitializedObject(assetType);
        Set(fileType, file, "m_PackagePath", path); Set(packageType, package, "m_PackageImplementation", file);
        Set(assetType, asset, "m_Package", package); Set(assetType, asset, "m_AssetOffset", 1024L);
        using (FileStream s = File.Create(path)) { s.SetLength(1024); s.Position = 1024; s.WriteByte(0); s.WriteByte(3); s.Write(new byte[] {65,66,67}, 0, 3); }
        Check(ReadHeader(assetType, asset) == "ABC", "real original offset unreadable");
        // Replace the file with a smaller new generation, leaving the real cached Asset intact.
        using (FileStream s = File.Create(path)) { s.SetLength(100); s.Position = 100; s.WriteByte(0); s.WriteByte(3); s.Write(new byte[] {88,89,90}, 0, 3); }
        bool eof = false; try { ReadHeader(assetType, asset); } catch (EndOfStreamException) { eof = true; }
        Check(eof, "actual game Asset.GetStream did not reproduce stale-offset EOF");
        Set(assetType, asset, "m_AssetOffset", 100L);
        Check(ReadHeader(assetType, asset) == "XYZ", "new index did not restore actual game stream read");
        Console.WriteLine("PASS: actual game Asset.GetStream reproduces stale-offset EOF after overwrite; reindexed offset reads new header. Synthetic bytes, no Unity/native patch install or city load.");
    }
    private static void Set(Type t, object target, string name, object value)
    { t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(target, value); }
    private static string ReadHeader(Type t, object asset)
    {
        using (Stream s = (Stream)t.GetMethod("GetStream").Invoke(asset, null))
        using (BinaryReader r = new BinaryReader(s)) { return r.ReadBoolean() ? null : r.ReadString(); }
    }
}
