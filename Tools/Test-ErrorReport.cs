using System;
using System.IO;
using System.Reflection;
using System.Threading;
using MoreNetworkCapacity;

namespace UnityEngine
{
    public enum LogType { Error, Assert, Warning, Log, Exception }
    public static class Application
    {
        public static string persistentDataPath, version = "test-game", unityVersion = "test-unity";
        public delegate void LogCallback(string message, string stack, LogType type);
        public static event LogCallback logMessageReceivedThreaded;
        public static int Subscribers { get { return logMessageReceivedThreaded == null ? 0
            : logMessageReceivedThreaded.GetInvocationList().Length; } }
        public static void Emit(string message, string stack, LogType type)
        { LogCallback callback = logMessageReceivedThreaded; if (callback != null) callback(message, stack, type); }
    }
    public static class Debug
    {
        public static void Log(string message) { Application.Emit(message, "", LogType.Log); }
        public static void LogWarning(string message) { Application.Emit(message, "", LogType.Warning); }
        public static void LogError(string message) { Application.Emit(message, "", LogType.Error); }
    }
}
public class NetManager { }
namespace MoreNetworkCapacity
{
    internal static class BuildInfo { internal const string Version = "test-mnc"; }
    internal static class PatchState { internal static bool ExpansionEnabled = true, SaveExpanded = false; }
    internal static class NetManagerDeserializeState { internal static bool LoadingExpanded = false; }
    internal static class Capacity { internal static int NodeLoopLimit = 65024,
        SegmentLoopLimit = 65024, NodeStorageSize = 65024; }
}
internal static class ErrorReportTests
{
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception(label); }
    private static void WaitWriter()
    {
        Type type = typeof(ErrorReport);
        object sync = type.GetField("Sync", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        FieldInfo worker = type.GetField("_worker", BindingFlags.Static | BindingFlags.NonPublic);
        for (int i = 0; i < 500; ++i)
        {
            lock (sync) { if (!(bool)worker.GetValue(null)) return; }
            Thread.Sleep(10);
        }
        throw new Exception("Background writer did not finish");
    }
    private static int Occurrences(string text, string token)
    { return (text.Length - text.Replace(token, "").Length) / token.Length; }
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            bool denied = args.Length > 1 && args[1] == "io-failure";
            bool concurrent = args.Length > 1 && args[1] == "concurrent";
            UnityEngine.Application.persistentDataPath = root;
            if (denied)
            {
                string blocked = Path.Combine(root, "blocked");
                File.WriteAllText(blocked, "not a directory");
                UnityEngine.Application.persistentDataPath = blocked;
            }
            ErrorReport.Start(); ErrorReport.Start();
            Check(UnityEngine.Application.Subscribers == 1, "Start must not duplicate subscriptions");
            if (concurrent)
            {
                Thread[] threads = new Thread[16];
                for (int i = 0; i < threads.Length; ++i)
                {
                    int index = i;
                    threads[i] = new Thread(delegate()
                    {
                        for (int j = 0; j < 2000; ++j)
                        {
                            Logging.Info("thread " + index);
                            Logging.Error("concurrent error " + index);
                        }
                    });
                    threads[i].Start();
                }
                foreach (Thread thread in threads) thread.Join();
                WaitWriter(); ErrorReport.Stop();
                string[] concurrentFiles = Directory.GetFiles(root, "MNC-error-*.txt", SearchOption.AllDirectories);
                Check(concurrentFiles.Length == 1 && Occurrences(File.ReadAllText(concurrentFiles[0]), "--- Error ") == 8,
                    "Concurrent callbacks must not lose accepted reports or create competing writers");
                Console.WriteLine("PASS: 32,000 concurrent error events produce at most eight accepted reports in one file.");
                return 0;
            }
            for (int i = 0; i < 10000; ++i) Logging.Info("normal " + i);
            Logging.Warning("optional compatibility warning");
            UnityEngine.Application.Emit("other mod error", "OtherMod.Update", UnityEngine.LogType.Exception);
            Check(Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories).Length == 0,
                "Normal logs and unrelated errors must not create reports");
            Logging.Error("first MNC error");
            WaitWriter();
            if (denied)
            {
                for (int i = 0; i < 10000; ++i) Logging.Error("retry after I/O failure");
                WaitWriter(); ErrorReport.Stop();
                Check(UnityEngine.Application.Subscribers == 0, "Stop after write failure");
                Console.WriteLine("PASS: report I/O failure does not escape, recurse, or retry indefinitely.");
                return 0;
            }
            for (int i = 0; i < 10000; ++i) Logging.Error("first MNC error");
            UnityEngine.Application.Emit("escaped exception", "MoreNetworkCapacity.Patches.Test.Prefix()",
                UnityEngine.LogType.Exception);
            WaitWriter();
            string[] files = Directory.GetFiles(root, "MNC-error-*.txt", SearchOption.AllDirectories);
            Check(files.Length == 1, "One report per process");
            string report = File.ReadAllText(files[0]);
            Check(Occurrences(report, "first MNC error") == 1, "Repeated errors and Unity forwarding are deduplicated");
            Check(report.Contains("escaped exception") && report.Contains("test-mnc")
                && report.Contains("test-game") && report.Contains("Game assembly MVID")
                && report.Contains("Loop limits=65024/65024") && report.Contains("Loaded assemblies"),
                "Report contains exception, versions and bounded context");
            Check(report.Contains("normal 9999") && !report.Contains("normal 0\r\n"), "Recent history is bounded");
            ErrorReport.Stop();
            Check(UnityEngine.Application.Subscribers == 0, "Stop removes own subscription");
            UnityEngine.Application.Emit("disabled error", "MoreNetworkCapacity.Test", UnityEngine.LogType.Exception);
            Logging.Error("disabled MNC error");
            ErrorReport.Start();
            for (int i = 0; i < 40; ++i)
            {
                Logging.Info(new string('x', 100000));
                Logging.Error("distinct " + i + new string('z', 100000));
            }
            WaitWriter(); ErrorReport.Stop();
            report = File.ReadAllText(files[0]);
            Check(!report.Contains("disabled error") && !report.Contains("disabled MNC error"), "Disabled reporter is silent");
            Check(Occurrences(report, "--- Error ") == 8, "Error budget survives disable/re-enable");
            Check(new FileInfo(files[0]).Length <= 262144, "Report size limit");
            Console.WriteLine("PASS: idle zero writes, unrelated-error filtering, duplicate suppression, escaped MNC error capture, bounded history/errors/file and lifecycle.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
