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
            entry.Offset = checked((uint)_writer.BaseStream.Position);
            _writer.Write(0x04034b50u);
            _writer.Write((ushort)20); _writer.Write((ushort)0x800); // UTF-8
            _writer.Write((ushort)0); _writer.Write((ushort)0); _writer.Write((ushort)33);
            _writer.Write(entry.Crc); _writer.Write(entry.Size); _writer.Write(entry.Size);
            _writer.Write((ushort)entry.Name.Length); _writer.Write((ushort)0);
            _writer.Write(entry.Name); _writer.Write(bytes);
            _entries.Add(entry);
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

        internal static uint Crc32(byte[] bytes)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int i = 0; i < 8; ++i)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
            }
            return ~crc;
        }
    }

    internal sealed class DiagnosticBundleInput
    {
        internal string OutputDirectory, Summary, Mods, Patches;
        internal string[] GameLogs, ReportDirectories;
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
                        + "No save files or screenshots are included; nothing is uploaded.\r\n"
                        + "A checkpoint or an MNC stack frame does not establish the cause.\r\n"
                        + "ZIP entries are uncompressed for compatibility with the game's runtime.\r\n");
                    int logIndex = 0;
                    foreach (string log in input.GameLogs)
                        AddFile(archive, manifest, "logs/" + (++logIndex) + "-"
                            + Path.GetFileName(log), log, LogLimit);
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
                                    { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
                                for (int i = 0; i < files.Length && i < 2; ++i)
                                {
                                    if (!included.Add(files[i].FullName)) continue;
                                    AddFile(archive, manifest, "reports/" + included.Count + "-"
                                        + files[i].Name, files[i].FullName, ReportLimit);
                                }
                                if (files.Length == 0) manifest.AppendLine("No " + pattern + " in " + directory);
                            }
                            catch (Exception exception)
                            { manifest.AppendLine("Report lookup failed: " + directory + " | " + exception.GetType().Name); }
                        }
                    }
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

        private static void AddFile(DiagnosticArchive archive, StringBuilder manifest,
            string name, string path, int maximum)
        {
            byte[] bytes;
            long originalLength;
            try { bytes = ReadTail(path, maximum, out originalLength); }
            catch (Exception exception)
            {
                manifest.AppendLine("Unavailable: " + path + " | " + exception.GetType().Name);
                return;
            }
            archive.Add(name, bytes);
            manifest.AppendLine(name + " | source=" + path + " | original bytes="
                + originalLength + " | captured bytes=" + bytes.Length
                + (originalLength > maximum ? " | TRUNCATED: latest bytes only" : ""));
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
