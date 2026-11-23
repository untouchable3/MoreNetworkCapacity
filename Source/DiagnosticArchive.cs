using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MoreNetworkCapacity
{
    // Stored ZIP entries keep compatibility with the game's .NET 3.5 runtime.
    // No extra library or executable is required on a player's machine.
    internal sealed class DiagnosticArchive : IDisposable
    {
        private sealed class Entry
        {
            internal byte[] Name;
            internal uint Crc, Size, Offset;
        }
        private readonly BinaryWriter _writer;
        private readonly List<Entry> _entries = new List<Entry>();
        private bool _complete;

        internal DiagnosticArchive(string path)
        {
            _writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None));
        }

        internal void Add(string name, byte[] bytes)
        {
            if (_complete) throw new InvalidOperationException("Archive already completed.");
            if (name.StartsWith("/", StringComparison.Ordinal) || name.Contains("..")
                || name.Contains("\\") || name.Contains(":"))
                throw new ArgumentException("Unsafe archive entry name.");
            Entry entry = new Entry();
            entry.Name = Encoding.UTF8.GetBytes(name);
            if (entry.Name.Length > ushort.MaxValue) throw new ArgumentException("Entry name too long.");
            entry.Crc = Crc32(bytes);
            entry.Size = (uint)bytes.Length;
            WriteHeader(entry);
            _writer.Write(bytes);
            _entries.Add(entry);
        }

        private void WriteHeader(Entry entry)
        {
            entry.Offset = checked((uint)_writer.BaseStream.Position);
            _writer.Write(0x04034b50u);
            _writer.Write((ushort)20); _writer.Write((ushort)0x800); // UTF-8
            _writer.Write((ushort)0); _writer.Write((ushort)0); _writer.Write((ushort)33);
            _writer.Write(entry.Crc); _writer.Write(entry.Size); _writer.Write(entry.Size);
            _writer.Write((ushort)entry.Name.Length); _writer.Write((ushort)0);
            _writer.Write(entry.Name);
        }

        internal void AddSave(FileStream source)
        {
            long size = source.Length;
            long start = _writer.BaseStream.Position;
            if (size == 0 || size + start > uint.MaxValue - 1048576L)
                throw new IOException("Save is empty or exceeds ZIP32 capacity.");
            var entry = new Entry { Name = Encoding.UTF8.GetBytes("save/latest.crp"), Size = (uint)size };
            if (_complete) throw new InvalidOperationException("Archive already completed.");
            byte[] buffer = new byte[65536];
            uint crc = uint.MaxValue;
            try
            {
                WriteHeader(entry);
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    _writer.Write(buffer, 0, read);
                    crc = UpdateCrc(crc, buffer, read);
                }
                if (source.Position != size) throw new IOException("Save size changed.");
                long end = _writer.BaseStream.Position;
                _writer.BaseStream.Position = start + 14;
                entry.Crc = ~crc;
                _writer.Write(entry.Crc);
                _writer.BaseStream.Position = end;
                _entries.Add(entry);
            }
            catch
            {
                _writer.BaseStream.SetLength(start);
                _writer.BaseStream.Position = start;
                throw;
            }
        }

        internal void AddText(string name, string text)
        {
            Add(name, Encoding.UTF8.GetBytes(text));
        }

        internal void Complete()
        {
            if (_complete) return;
            uint offset = checked((uint)_writer.BaseStream.Position);
            foreach (Entry entry in _entries)
            {
                _writer.Write(0x02014b50u);
                _writer.Write((ushort)20); _writer.Write((ushort)20);
                _writer.Write((ushort)0x800); _writer.Write((ushort)0);
                _writer.Write((ushort)0); _writer.Write((ushort)33);
                _writer.Write(entry.Crc); _writer.Write(entry.Size); _writer.Write(entry.Size);
                _writer.Write((ushort)entry.Name.Length);
                _writer.Write((ushort)0); _writer.Write((ushort)0); _writer.Write((ushort)0);
                _writer.Write((ushort)0); _writer.Write(0u); _writer.Write(entry.Offset);
                _writer.Write(entry.Name);
            }
            uint size = checked((uint)_writer.BaseStream.Position - offset);
            _writer.Write(0x06054b50u); _writer.Write((ushort)0); _writer.Write((ushort)0);
            _writer.Write(checked((ushort)_entries.Count)); _writer.Write(checked((ushort)_entries.Count));
            _writer.Write(size); _writer.Write(offset); _writer.Write((ushort)0);
            _writer.Flush();
            _complete = true;
        }

        public void Dispose() { _writer.Close(); }

        private static readonly uint[] CrcTable = MakeCrcTable();
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                uint crc = n;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
                table[n] = crc;
            }
            return table;
        }

        private static uint UpdateCrc(uint crc, byte[] bytes, int count)
        {
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            return crc;
        }

        internal static uint Crc32(byte[] bytes) { return ~UpdateCrc(uint.MaxValue, bytes, bytes.Length); }
    }

    internal sealed class DiagnosticBundleInput
    {
        internal string OutputDirectory, Summary, Mods, Patches;
        internal string RoadColorReport;
        internal string[] SaveCandidates;
        internal string SaveLookupError;
        internal bool SaveIncluded;
        internal string[] GameLogs, ReportDirectories, CurrentReportPaths;
    }

    internal static class DiagnosticBundleWriter
    {
        internal const int LogLimit = 8 * 1024 * 1024;
        internal const int ReportLimit = 256 * 1024;

        internal static string Write(DiagnosticBundleInput input)
        {
            Directory.CreateDirectory(input.OutputDirectory);
            string path = Path.Combine(input.OutputDirectory, "MNC-diagnostics-"
                + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            string temporary = path + ".partial";
            try
            {
                using (DiagnosticArchive archive = new DiagnosticArchive(temporary))
                {
                    StringBuilder manifest = new StringBuilder(
                        "Captured on demand. Read errors/missing files are listed below.\r\n"
                        + "Logs are snapshots; concurrent logging may continue after capture.\r\n"
                        + "A tail may begin in the middle of a line/UTF-8 character.\r\n");
                    archive.AddText("summary.txt", input.Summary);
                    archive.AddText("mods.txt", input.Mods);
                    archive.AddText("render-patches.txt", input.Patches);
                    archive.AddText("READ-ME.txt", "Share this ZIP with the author together with:\r\n"
                        + "1. What you did and what went wrong.\r\n2. A screenshot if appearance is wrong.\r\n"
                        + "3. Whether this happened when enabling, loading, or playing.\r\n"
                        + "Review the files before sharing: logs may contain local paths and save names.\r\n"
                        + "A save is included only when requested and available. Nothing is uploaded.\r\n"
                        + "A checkpoint or an MNC stack frame does not establish the cause.\r\n"
                        + "ZIP entries are uncompressed for compatibility with the game's runtime.\r\n");
                    int logIndex = 0;
                    foreach (string log in input.GameLogs)
                        AddFile(archive, manifest, "logs/" + (++logIndex) + "-"
                            + Path.GetFileName(log), log, LogLimit);
                    HashSet<string> current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string report in input.CurrentReportPaths ?? new string[0])
                        if (!string.IsNullOrEmpty(report)) current.Add(Path.GetFullPath(report));
                    manifest.AppendLine("Report scope is based on exact paths from this MNC process, not file age or MNC version.");
                    manifest.AppendLine("Historical or unidentified reports are context; they do not establish an error in this launch.");
                    int currentErrors = 0, currentStages = 0, historical = 0;
                    HashSet<string> included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string directory in input.ReportDirectories)
                    {
                        foreach (string pattern in new[] { "MNC-stage-*.txt", "MNC-error-*.txt" })
                        {
                            try
                            {
                                if (!Directory.Exists(directory))
                                { manifest.AppendLine("Report directory missing: " + directory); continue; }
                                FileInfo[] files = new DirectoryInfo(directory).GetFiles(pattern);
                                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                                    {
                                        bool ac = current.Contains(a.FullName), bc = current.Contains(b.FullName);
                                        return ac == bc ? b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc) : ac ? -1 : 1;
                                    });
                                for (int i = 0; i < files.Length && i < 2; ++i)
                                {
                                    if (!included.Add(files[i].FullName)) continue;
                                    bool sameProcess = current.Contains(files[i].FullName);
                                    string entry = "reports/" + included.Count + "-" + files[i].Name;
                                    if (AddFile(archive, manifest, entry, files[i].FullName, ReportLimit))
                                    {
                                        if (sameProcess)
                                        {
                                            if (pattern == "MNC-error-*.txt") ++currentErrors; else ++currentStages;
                                        }
                                        else ++historical;
                                        manifest.AppendLine(entry + " | report scope=" + (sameProcess ? "current MNC process" : "historical or unidentified")
                                            + " | last modified UTC=" + files[i].LastWriteTimeUtc.ToString("u"));
                                    }
                                }
                                if (files.Length == 0) manifest.AppendLine("No " + pattern + " in " + directory);
                            }
                            catch (Exception exception)
                            { manifest.AppendLine("Report lookup failed: " + directory + " | " + exception.GetType().Name); }
                        }
                    }
                    manifest.AppendLine("Current MNC process error files captured=" + currentErrors
                        + "; checkpoint files captured=" + currentStages + "; historical or unidentified files=" + historical);
                    manifest.AppendLine("An absent MNC error file is not proof that the game or other mods had no errors; inspect the game log.");
                    if (!string.IsNullOrEmpty(input.RoadColorReport))
                    {
                        archive.AddText("road-color-analysis.txt", input.RoadColorReport);
                        manifest.AppendLine("road-color-analysis.txt | player-generated road color analysis");
                    }
                    if (input.SaveCandidates != null)
                        IncludeLatestSave(archive, manifest, input);
                    archive.AddText("manifest.txt", manifest.ToString());
                    archive.Complete();
                }
                File.Move(temporary, path);
                return path;
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
        }

        private static void IncludeLatestSave(DiagnosticArchive archive, StringBuilder manifest, DiagnosticBundleInput input)
        {
            try
            {
                if (input.SaveLookupError != null) throw new IOException(input.SaveLookupError);
                FileInfo newest = null;
                foreach (string path in input.SaveCandidates)
                {
                    var file = new FileInfo(path);
                    if (file.Exists && (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc ||
                        (file.LastWriteTimeUtc == newest.LastWriteTimeUtc && string.CompareOrdinal(file.FullName, newest.FullName) < 0)))
                        newest = file;
                }
                if (newest == null) throw new FileNotFoundException("No indexed local save found.");
                using (var source = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    archive.AddSave(source);
                    manifest.AppendLine("save/latest.crp | local file=" + newest.Name + " | bytes=" + source.Length
                        + " | modified UTC=" + File.GetLastWriteTimeUtc(newest.FullName).ToString("u"));
                    input.SaveIncluded = true;
                }
            }
            catch (IOException e) { manifest.AppendLine("Save attachment unavailable: " + e.Message); }
            catch (UnauthorizedAccessException e) { manifest.AppendLine("Save attachment unavailable: " + e.Message); }
        }

        private static bool AddFile(DiagnosticArchive archive, StringBuilder manifest,
            string name, string path, int maximum)
        {
            byte[] bytes;
            long originalLength;
            try { bytes = ReadTail(path, maximum, out originalLength); }
            catch (Exception exception)
            {
                manifest.AppendLine("Unavailable: " + path + " | " + exception.GetType().Name);
                return false;
            }
            archive.Add(name, bytes);
            manifest.AppendLine(name + " | source=" + path + " | original bytes="
                + originalLength + " | captured bytes=" + bytes.Length
                + (originalLength > maximum ? " | TRUNCATED: latest bytes only" : ""));
            return true;
        }

        internal static byte[] ReadTail(string path, int maximum, out long originalLength)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                originalLength = stream.Length;
                int length = (int)Math.Min(originalLength, maximum);
                byte[] bytes = new byte[length];
                stream.Seek(originalLength - length, SeekOrigin.Begin);
                int read = 0;
                while (read < length)
                {
                    int count = stream.Read(bytes, read, length - read);
                    if (count == 0) break;
                    read += count;
                }
                if (read != length) Array.Resize(ref bytes, read);
                return bytes;
            }
        }
    }
}
