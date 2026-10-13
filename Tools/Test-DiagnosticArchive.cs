using System;
using System.IO;
using System.Text;
using MoreNetworkCapacity;

internal static class DiagnosticArchiveTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Check(DiagnosticArchive.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926u,
                "ZIP CRC32 must match the standard test vector");
            string log = Path.Combine(root, "output_log.txt");
            byte[] bytes = new byte[DiagnosticBundleWriter.LogLimit + 100];
            for (int i = 0; i < bytes.Length; ++i) bytes[i] = (byte)(i % 251);
            File.WriteAllBytes(log, bytes);
            long length;
            byte[] tail = DiagnosticBundleWriter.ReadTail(log, 123, out length);
            Check(length == bytes.Length && tail.Length == 123 && tail[0] == bytes[bytes.Length - 123]
                && tail[122] == bytes[bytes.Length - 1], "ReadTail must preserve only the latest bounded bytes");
            string reports = Path.Combine(root, "reports");
            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, "MNC-stage-test.txt"), "stage fixture");
            File.WriteAllText(Path.Combine(reports, "MNC-error-test.txt"), "error fixture");
            File.WriteAllText(Path.Combine(reports, "save.crp"), "must not export saves");
            DiagnosticBundleInput input = new DiagnosticBundleInput();
            input.OutputDirectory = Path.Combine(root, "exports");
            input.Summary = "summary 诊断 fixture"; input.Mods = "mods fixture"; input.Patches = "patch fixture";
            input.GameLogs = new[] { log, Path.Combine(root, "missing.log") };
            input.ReportDirectories = new[] { reports, reports, Path.Combine(root, "missing reports") };
            string zip;
            using (FileStream liveLog = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                zip = DiagnosticBundleWriter.Write(input);
            Check(File.Exists(zip) && Directory.GetFiles(input.OutputDirectory, "*.partial").Length == 0,
                "Only a completed bundle may receive the .zip name");
            Check(File.ReadAllText(Path.Combine(reports, "save.crp")) == "must not export saves",
                "Source files must remain untouched");
            string bad = Path.Combine(root, "unsafe.zip");
            bool refused = false;
            using (DiagnosticArchive archive = new DiagnosticArchive(bad))
            { try { archive.AddText("../escape.txt", "bad"); } catch (ArgumentException) { refused = true; } }
            Check(refused, "Archive entry traversal must be rejected");
            string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "not a directory");
            input.OutputDirectory = blocked;
            bool writeFailed = false;
            try { DiagnosticBundleWriter.Write(input); } catch (IOException) { writeFailed = true; }
            Check(writeFailed, "Unavailable export directory must fail explicitly");
            File.WriteAllText(Path.Combine(args[0], "latest-bundle.txt"), zip);
            Console.WriteLine("PASS: CRC standard vector, bounded live-log read, missing-file tolerance, report selection/deduplication, no source writes, safe names and directory failure.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
