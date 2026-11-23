using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MoreNetworkCapacity
{
    internal static class DiagnosticExport
    {
        private static int _busy;
        private static volatile string _english = "Ready";
        private static volatile string _chinese = "就绪";
        private static volatile string _lastPath;
        internal static bool IsBusy { get { return Interlocked.CompareExchange(ref _busy, 0, 0) != 0; } }
        internal static string LastMessage { get { return _english; } }

        internal static string StatusMessage(bool chinese) { return chinese ? _chinese : _english; }

        internal static bool Begin() { return Begin(false); }

        internal static bool Begin(bool includeSave)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            _english = "Exporting…";
            _chinese = "正在导出…";
            try
            {
                DiagnosticBundleInput input = DiagnosticSnapshot.Capture();
                input.RoadColorReport = Diagnostics.RoadColorAnalyzer.Report;
                if (includeSave) LatestSaveAttachment.SelectCandidates(input);
                if (!ThreadPool.QueueUserWorkItem(delegate(object unused)
                {
                    try
                    {
                        _lastPath = DiagnosticBundleWriter.Write(input);
                        _english = "ZIP saved  " + Path.GetFileName(_lastPath);
                        _chinese = "ZIP 已保存  " + Path.GetFileName(_lastPath);
                        if (includeSave && !input.SaveIncluded)
                        {
                            _english += " (save unavailable; see manifest)";
                            _chinese += "（存档未附带，详见清单）";
                        }
                        Logging.Info("Diagnostic bundle exported: " + _lastPath);
                    }
                    catch (Exception exception) { Failed(exception); }
                    finally { Interlocked.Exchange(ref _busy, 0); }
                })) throw new InvalidOperationException("Could not queue export.");
                return true;
            }
            catch (Exception exception)
            {
                Failed(exception);
                Interlocked.Exchange(ref _busy, 0);
                return false;
            }
        }

        internal static void OpenFolder()
        {
            try
            {
                string directory = _lastPath == null ? DiagnosticSnapshot.OutputDirectory : Path.GetDirectoryName(_lastPath);
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception exception) { Failed(exception); }
        }

        private static void Failed(Exception exception)
        {
            _english = "Export/open failed  " + exception.GetType().Name;
            _chinese = "导出或打开失败  " + exception.GetType().Name;
            Logging.Warning("Diagnostic export/open failed: " + exception);
        }
    }
}
