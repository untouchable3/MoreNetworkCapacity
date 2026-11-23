using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using ColossalFramework.IO;
using ColossalFramework.Packaging;
using ColossalFramework.Threading;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    internal static class SaveListMetadataRefresh
    {
        // All panel and pending-list access takes place on the UI dispatcher.
        private static readonly List<object> Pending = new List<object>();
        private static bool _refreshing;
        private static int _generation;

        internal static void Reset()
        {
            Interlocked.Increment(ref _generation);
            Pending.Clear();
            _refreshing = false;
        }

        internal static bool Begin(object panel, out bool entered)
        {
            entered = false;
            if (_refreshing || SavePanel.isSaving)
            {
                if (!Pending.Contains(panel)) Pending.Add(panel);
                return false;
            }
            _refreshing = true;
            entered = true;
            try { RefreshLocalIndexes(); }
            catch (Exception error)
            {
                // Keep the game's normal per-save error handling for bad files.
                Logging.Warning("Save-list index refresh failed: " + error);
            }
            return true;
        }

        internal static void End(bool entered)
        {
            if (!entered) return;
            _refreshing = false;
            if (Pending.Count != 0 && !SavePanel.isSaving) QueuePending();
        }

        internal static void QueuePending()
        {
            int generation = Thread.VolatileRead(ref _generation);
            try
            {
                Action refreshPending = delegate
                {
                    if (generation != Thread.VolatileRead(ref _generation)
                        || _refreshing || SavePanel.isSaving) return;
                    object[] panels = Pending.ToArray();
                    Pending.Clear();
                    foreach (object panel in panels)
                    {
                        // Unity may have destroyed a panel while saving/unloading.
                        UnityEngine.Object unityPanel = panel as UnityEngine.Object;
                        if (unityPanel == null) continue;
                        try
                        {
                            MethodInfo refresh = AccessTools.Method(panel.GetType(), "Refresh", Type.EmptyTypes, null);
                            if (refresh != null) refresh.Invoke(panel, null);
                        }
                        catch (Exception error)
                        {
                            Logging.Warning("Deferred save-list refresh failed: " + error);
                        }
                    }
                };
                // Dispatcher.Dispatch rejects calls from its own thread. End()
                // already runs on the UI dispatcher; completion may run on a worker.
                Dispatcher dispatcher = ThreadHelper.dispatcher;
                if (Dispatcher.currentSafe == dispatcher) refreshPending();
                else dispatcher.Dispatch(refreshPending);
            }
            catch (Exception error)
            {
                Logging.Warning("Could not dispatch save-list refresh: " + error);
            }
        }

        private static void RefreshLocalIndexes()
        {
            string root = Path.GetFullPath(DataLocation.saveLocation)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Package.Asset asset in PackageManager.FilterAssets(
                new[] { UserAssetType.SaveGameMetaData }))
            {
                if (asset == null || asset.package == null) continue;
                string path = asset.package.packagePath;
                if (string.IsNullOrEmpty(path) || PackageManager.IsCloudPath(path)) continue;
                try
                {
                    path = Path.GetFullPath(path);
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Path.GetExtension(path), ".crp", StringComparison.OrdinalIgnoreCase)
                        && File.Exists(path)) paths.Add(path);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            // Finish enumeration before Update replaces package objects and raises events.
            foreach (string path in paths)
            {
                try { PackageManager.Update(path); }
                catch (Exception error)
                {
                    Logging.Warning("Save-list index update failed for " + path + ": " + error);
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class SaveListMetadataRefreshPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(LoadPanel), "Refresh", Type.EmptyTypes, null);
            yield return AccessTools.Method(typeof(SavePanel), "Refresh", Type.EmptyTypes, null);
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(object __instance, out bool __state)
        {
            return SaveListMetadataRefresh.Begin(__instance, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            SaveListMetadataRefresh.End(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(SavePanel), "OnSavingComplete", new[] { typeof(bool), typeof(bool) })]
    internal static class SaveListAfterSavingPatch
    {
        private static void Postfix()
        {
            // The game calls this from its packaging worker, after closing the file
            // and clearing isSaving. Never touch packages or Unity panels on that worker.
            SaveListMetadataRefresh.QueuePending();
        }
    }
}
