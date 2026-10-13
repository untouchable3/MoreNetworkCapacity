using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MoreNetworkCapacity
{
    internal static class DiagnosticExport
    {
        private static int _busy;
        private static volatile string _lastMessage = "Ready / 就绪";
        private static volatile string _lastPath;
        internal static bool IsBusy { get { return Interlocked.CompareExchange(ref _busy, 0, 0) != 0; } }
        internal static string LastMessage { get { return _lastMessage; } }

        internal static bool Begin()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
            _lastMessage = "Exporting... / 正在导出…";
            try
            {
                DiagnosticBundleInput input = DiagnosticSnapshot.Capture();
                if (!ThreadPool.QueueUserWorkItem(delegate(object unused)
                {
                    try
                    {
                        _lastPath = DiagnosticBundleWriter.Write(input);
                        _lastMessage = "ZIP saved / ZIP 已保存: " + Path.GetFileName(_lastPath);
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
            _lastMessage = "Export/open failed / 导出或打开失败: " + exception.GetType().Name;
            Logging.Warning("Diagnostic export/open failed: " + exception);
        }
    }
}
