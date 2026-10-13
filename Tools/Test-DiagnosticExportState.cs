using System;
using System.Threading;
using MoreNetworkCapacity;

namespace MoreNetworkCapacity
{
    internal class DiagnosticBundleInput { }
    internal static class DiagnosticSnapshot
    {
        internal static bool FailCapture;
        internal static int Captures;
        internal static string OutputDirectory { get { return "test output"; } }
        internal static DiagnosticBundleInput Capture()
        {
            ++Captures;
            if (FailCapture) throw new InvalidOperationException("injected capture failure");
            return new DiagnosticBundleInput();
        }
    }
    internal static class DiagnosticBundleWriter
    {
        internal static readonly ManualResetEvent Entered = new ManualResetEvent(false);
        internal static readonly ManualResetEvent Release = new ManualResetEvent(false);
        internal static bool FailWrite;
        internal static string Write(DiagnosticBundleInput input)
        {
            Entered.Set();
            if (!Release.WaitOne(5000, false)) throw new Exception("test writer timed out");
            if (FailWrite) throw new System.IO.IOException("injected write failure");
            return "test-output/bundle.zip";
        }
    }
    internal static class Logging
    {
        internal static void Info(string text) { }
        internal static void Warning(string text) { }
    }
}
internal static class DiagnosticExportStateTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
    private static void Wait()
    {
        for (int i = 0; i < 500 && DiagnosticExport.IsBusy; ++i) Thread.Sleep(10);
        Check(!DiagnosticExport.IsBusy, "Worker did not finish");
    }
    private static int Main()
    {
        try
        {
            Check(DiagnosticExport.Begin(), "First click must start export");
            Check(DiagnosticBundleWriter.Entered.WaitOne(5000, false), "No background writer");
            Check(!DiagnosticExport.Begin() && DiagnosticSnapshot.Captures == 1,
                "Concurrent clicks must not capture or write another bundle");
            DiagnosticBundleWriter.Release.Set(); Wait();
            Check(DiagnosticExport.LastMessage.Contains("ZIP saved"), "Success status missing");
            DiagnosticBundleWriter.FailWrite = true;
            Check(DiagnosticExport.Begin(), "Must permit another user-requested export"); Wait();
            Check(DiagnosticExport.LastMessage.Contains("IOException"), "Write failure must produce a user-visible status");
            DiagnosticSnapshot.FailCapture = true;
            Check(!DiagnosticExport.Begin() && !DiagnosticExport.IsBusy
                && DiagnosticExport.LastMessage.Contains("InvalidOperationException"),
                "Capture failure must not leave export busy or escape");
            Console.WriteLine("PASS: one writer, repeated-click rejection, completion feedback, retries on explicit clicks, background and capture failure isolation.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
