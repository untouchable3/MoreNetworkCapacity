using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MoreNetworkCapacity
{
    internal static class LoadStageJournal
    {
        private static readonly object Sync = new object();
        private static StageJournalWriter _writer;
        private static bool _unavailable;

        internal static void Record(string stage)
        {
            // No Unity, Harmony, or live game data access before the first checkpoint.
            lock (Sync)
            {
                if (_unavailable) return;
                try
                {
                    if (_writer == null)
                    {
                        string directory = Path.Combine(Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                            "Colossal Order\\Cities_Skylines\\MoreNetworkCapacity\\ErrorReports");
                        _writer = new StageJournalWriter(directory, BuildInfo.Version);
                    }
                    _writer.Record(stage);
                }
                catch { _unavailable = true; }
            }
        }

        internal static string LastStage
        {
            get { lock (Sync) { return _writer == null ? "not started" : _writer.LastStage; } }
        }

        internal static string Status
        {
            get { lock (Sync) { return _unavailable ? "unavailable" : _writer == null
                ? "not started" : _writer.Status; } }
        }
    }

    internal sealed class StageJournalWriter
    {
        private readonly object _sync = new object();
        private readonly Queue<string> _recent = new Queue<string>();
        private readonly string _path, _header;
        private bool _failed, _written;
        private string _failure;
        private string _lastStage = "not started";

        internal StageJournalWriter(string directory, string version)
        {
            _path = Path.Combine(directory, "MNC-stage-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")
                + "-" + Process.GetCurrentProcess().Id + ".txt");
            _header = "More Network Capacity enable/load checkpoints\r\nMNC: " + version
                + "\r\nTimes are UTC. Latest eight checkpoints; written BEFORE the named operation."
                + "\r\nAn unfinished stage is a location clue, not proof of a crash or its cause."
                + "\r\nNo checkpoints are written during steady simulation.\r\n";
        }

        internal string LastStage { get { lock (_sync) { return _lastStage; } } }
        internal string Status { get { lock (_sync) { return _failed ? "write failed: " + _failure + "; " + _path : _path; } } }

        internal void Record(string stage)
        {
            lock (_sync)
            {
                if (_failed) return;
                try
                {
                    if (stage == null) stage = "unknown";
                    stage = stage.Replace('\r', ' ').Replace('\n', ' ');
                    if (stage.Length > 512) stage = stage.Substring(0, 512);
                    _lastStage = stage;
                    if (_recent.Count == 8) _recent.Dequeue();
                    _recent.Enqueue(DateTime.UtcNow.ToString("u") + " | " + stage);
                    StringBuilder text = new StringBuilder(_header);
                    foreach (string line in _recent) text.AppendLine(line);
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    string temporary = _path + ".tmp";
                    // Flush and close before the risky operation proceeds. Replacing the
                    // snapshot preserves the previous complete file if interrupted mid-write.
                    using (FileStream stream = new FileStream(temporary, FileMode.Create,
                        FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                    if (File.Exists(_path)) File.Replace(temporary, _path, null);
                    else File.Move(temporary, _path);
                    if (!_written)
                    {
                        _written = true;
                        ReportFileRetention.Trim(_path, "MNC-stage-*.txt");
                    }
                }
                catch (Exception exception)
                {
                    _failed = true;
                    string detail = exception.Message;
                    if (detail.Length > 256) detail = detail.Substring(0, 256);
                    _failure = exception.GetType().Name + ": " + detail;
                }
            }
        }
    }

    internal static class ReportFileRetention
    {
        internal static void Trim(string currentPath, string pattern)
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(Path.GetDirectoryName(currentPath)).GetFiles(pattern);
                Array.Sort(files, delegate(FileInfo left, FileInfo right)
                    { return right.Name.CompareTo(left.Name); });
                for (int i = 5; i < files.Length; ++i)
                {
                    if (files[i].FullName == currentPath) continue;
                    string name = Path.GetFileNameWithoutExtension(files[i].Name);
                    int processId;
                    if (!int.TryParse(name.Substring(name.LastIndexOf('-') + 1), out processId)) continue;
                    bool running = true;
                    try { using (Process process = Process.GetProcessById(processId))
                        { running = !process.HasExited; } }
                    catch (ArgumentException) { running = false; }
                    if (!running) files[i].Delete();
                }
            }
            catch { }
        }
    }
}
