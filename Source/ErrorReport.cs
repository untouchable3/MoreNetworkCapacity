using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace MoreNetworkCapacity
{
    // Event driven: no simulation hook, timer, or normal-operation disk writes.
    internal static class ErrorReport
    {
        private const int MaxErrors = 8;
        private const int MaxFileBytes = 262144;
        private static readonly object Sync = new object();
        private static readonly Queue<string> Recent = new Queue<string>();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static bool _listening, _initialized, _worker, _ioFailed;
        private static string _path, _header;

        internal static void Start()
        {
            try
            {
                lock (Sync)
                {
                    if (_listening) return;
                    if (!_initialized)
                    {
                        string directory = Path.Combine(Application.persistentDataPath,
                            "MoreNetworkCapacity\\ErrorReports");
                        _path = Path.Combine(directory, "MNC-error-"
                            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")
                            + "-" + Process.GetCurrentProcess().Id + ".txt");
                        _header = "More Network Capacity error report\r\nMNC: " + BuildInfo.Version
                            + "\r\nGame: " + Application.version
                            + "\r\nUnity: " + Application.unityVersion
                            + "\r\nGame assembly MVID: "
                            + typeof(NetManager).Assembly.ManifestModule.ModuleVersionId
                            + "\r\nTimes are UTC. Only MNC errors or Unity errors referencing MNC are captured."
                            + "\r\nThis report does not establish which mod caused the error."
                            + "\r\nNative crashes and silent corruption may not produce a report."
                            + "\r\nLimits: 8 distinct errors per process, 32 recent messages, 256 KiB per file."
                            + "\r\nReview paths and other personal details before sharing.\r\n";
                        _initialized = true;
                    }
                    Application.logMessageReceivedThreaded += OnUnityLog;
                    _listening = true;
                }
            }
            catch (Exception exception)
            {
                // Never route reporter failures through Logging.Error.
                UnityEngine.Debug.LogWarning("[MoreNetworkCapacity] Error reporting unavailable: "
                    + exception.GetType().Name);
            }
        }

        internal static void Stop()
        {
            lock (Sync)
            {
                if (!_listening) return;
                Application.logMessageReceivedThreaded -= OnUnityLog;
                _listening = false;
                // Already queued reports finish; no main-thread wait or reset race.
            }
        }

        internal static void Remember(string level, string message)
        {
            lock (Sync)
            {
                if (!_listening || _ioFailed || Seen.Count >= MaxErrors) return;
                if (Recent.Count == 32) Recent.Dequeue();
                Recent.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss") + " " + level + " "
                    + Limit(message, 512));
            }
        }

        internal static void Capture(string message, string stackTrace)
        {
            lock (Sync)
            {
                if (!_listening || _ioFailed || Seen.Count >= MaxErrors) return;
                string boundedMessage = Limit(message, 4096);
                string boundedStack = Limit(stackTrace, 8192);
                if (!Seen.Add(boundedMessage + "\n" + boundedStack)) return;
                StringBuilder report = new StringBuilder();
                report.AppendLine("\r\n--- Error " + Seen.Count + "/" + MaxErrors + " ---");
                report.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
                report.AppendLine("Last enable/load checkpoint: " + LoadStageJournal.LastStage);
                report.AppendLine("Checkpoint journal: " + LoadStageJournal.Status);
                // Read only scalar state; do not enumerate live game buffers.
                report.AppendLine("Expansion enabled=" + PatchState.ExpansionEnabled
                    + ", save expanded=" + PatchState.SaveExpanded
                    + ", loading expanded=" + NetManagerDeserializeState.LoadingExpanded);
                report.AppendLine("Loop limits=" + Capacity.NodeLoopLimit + "/"
                    + Capacity.SegmentLoopLimit + ", node storage=" + Capacity.NodeStorageSize);
                report.AppendLine(boundedMessage);
                report.AppendLine(boundedStack);
                report.AppendLine("Recent MNC messages (bounded):");
                foreach (string line in Recent) report.AppendLine(line);
                Pending.Enqueue(report.ToString());
                if (_worker) return;
                _worker = true;
                try
                {
                    if (!ThreadPool.QueueUserWorkItem(WritePending))
                        throw new InvalidOperationException("Could not queue error report writer.");
                }
                catch
                {
                    _worker = false;
                    _ioFailed = true;
                    Pending.Clear();
                }
            }
        }

        private static void OnUnityLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // MNC's own Error entry point captures before emitting the Unity log.
            if (message != null && message.StartsWith("[MoreNetworkCapacity] ", StringComparison.Ordinal)) return;
            if ((message != null && message.IndexOf("MoreNetworkCapacity.", StringComparison.Ordinal) >= 0)
                || (stackTrace != null && stackTrace.IndexOf("MoreNetworkCapacity.", StringComparison.Ordinal) >= 0))
                Capture(message, stackTrace);
        }

        private static void WritePending(object unused)
        {
            try
            {
                bool newFile = !File.Exists(_path);
                if (newFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, _header + AssemblySummary(), Encoding.UTF8);
                    TrimReports();
                }
                while (true)
                {
                    string text;
                    lock (Sync)
                    {
                        if (Pending.Count == 0) { _worker = false; return; }
                        text = Pending.Dequeue();
                    }
                    using (FileStream stream = new FileStream(_path, FileMode.Append,
                        FileAccess.Write, FileShare.Read))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        if (stream.Length + bytes.Length <= MaxFileBytes)
                            stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
            catch (Exception exception)
            {
                lock (Sync) { _ioFailed = true; Pending.Clear(); _worker = false; }
                UnityEngine.Debug.LogWarning("[MoreNetworkCapacity] Could not write error report: "
                    + exception.GetType().Name + ". Use the game's output_log.txt instead.");
                return;
            }
            finally
            {
                // Place an easy-to-find location in the game log after each write batch.
                UnityEngine.Debug.Log("[MoreNetworkCapacity] Error report location: " + _path);
            }
        }

        private static string AssemblySummary()
        {
            StringBuilder summary = new StringBuilder("\r\nLoaded assemblies (names/versions, up to 128):\r\n");
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && i < 128; ++i)
                    summary.AppendLine(Limit(assemblies[i].GetName().ToString(), 256));
                if (assemblies.Length > 128) summary.AppendLine("(additional assemblies omitted)");
            }
            catch { summary.AppendLine("(assembly summary unavailable)"); }
            return summary.ToString();
        }

        private static void TrimReports()
        {
            ReportFileRetention.Trim(_path, "MNC-error-*.txt");
        }

        private static string Limit(string value, int maximum)
        {
            if (value == null) return string.Empty;
            return value.Length <= maximum ? value : value.Substring(0, maximum) + " [truncated]";
        }
    }
}
