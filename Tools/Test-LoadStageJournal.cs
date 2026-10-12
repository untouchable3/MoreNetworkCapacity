using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using MoreNetworkCapacity;

namespace MoreNetworkCapacity
{
    internal static class BuildInfo { internal const string Version = "journal-test"; }
}

internal static class LoadStageJournalTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            if (args[0] == "--exit-after-checkpoint")
            {
                StageJournalWriter child = new StageJournalWriter(args[1], "child-test");
                child.Record("enable: before Harmony creation");
                // No logging callback, async report, finally handler, or shutdown hook.
                Environment.Exit(23);
                return 23;
            }
            string root = Path.Combine(args[0], Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "journal");
            StageJournalWriter writer = new StageJournalWriter(directory, "test-version");
            Check(!Directory.Exists(directory), "Constructing the writer must not perform I/O");
            writer.Record("enable: waiting for Harmony");
            string[] files = Directory.GetFiles(directory, "MNC-stage-*.txt");
            Check(files.Length == 1 && File.ReadAllText(files[0]).Contains("waiting for Harmony"),
                "Checkpoint must be readable immediately when Record returns");
            for (int i = 0; i < 200; ++i) writer.Record("load " + i + " " + new string('x', 4000));
            string snapshot = File.ReadAllText(files[0]);
            Check(snapshot.Contains("load 199") && !snapshot.Contains("load 0 "), "Snapshot must retain recent stages: " + writer.Status);
            Check(new FileInfo(files[0]).Length < 16384, "Snapshot must stay bounded across repeated loads");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Successful replacement leaves no temporary files");
            // A failure during the next write must preserve the previous complete snapshot.
            Directory.CreateDirectory(files[0] + ".tmp");
            writer.Record("failed new write");
            Check(File.ReadAllText(files[0]) == snapshot && writer.Status.StartsWith("write failed:"),
                "Checkpoint I/O failure must preserve the previous readable file");
            writer.Record("do not retry this write");
            Check(File.ReadAllText(files[0]) == snapshot, "Failed writer must stop retrying");
            StageJournalWriter blocked = new StageJournalWriter(files[0], "test-version");
            blocked.Record("failure must not escape");
            Check(blocked.Status.StartsWith("write failed:"), "Unavailable directory must be isolated");

            string childDirectory = Path.Combine(root, "child");
            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--exit-after-checkpoint \"" + childDirectory + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using (Process process = Process.Start(start))
            {
                Check(process.WaitForExit(10000) && process.ExitCode == 23, "Checkpoint test child failed to exit");
            }
            files = Directory.GetFiles(childDirectory, "MNC-stage-*.txt");
            Check(files.Length == 1 && File.ReadAllText(files[0]).Contains("before Harmony creation"),
                "Immediate process exit must leave the completed checkpoint without error logging");
            Console.WriteLine("PASS: synchronous readable checkpoint, bounded replacement, prior snapshot survives failed write, retry suppression and immediate child-process exit.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
