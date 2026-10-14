using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Plugins;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class DiagnosticSnapshot
    {
        internal static string OutputDirectory
        {
            get
            {
                string root = Application.persistentDataPath;
                if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("Game data path is unavailable.");
                return Path.Combine(root, "MoreNetworkCapacity/Diagnostics");
            }
        }

        // Called only by the settings button on the Unity thread. No Unity objects
        // are retained by the background writer and no game state is changed.
        internal static DiagnosticBundleInput Capture()
        {
            StringBuilder summary = new StringBuilder();
            summary.AppendLine("MNC on-demand diagnostics (point-in-time, not an atomic simulation snapshot)");
            summary.AppendLine("MNC version: " + BuildInfo.Version);
            summary.AppendLine("UTC: " + DateTime.UtcNow.ToString("u"));
            summary.AppendLine("Game: " + Application.version + " | Unity: " + Application.unityVersion);
            summary.AppendLine("Game assembly MVID: " + typeof(NetManager).Assembly.ManifestModule.ModuleVersionId);
            summary.AppendLine("MNC assembly MVID: " + typeof(Mod).Assembly.ManifestModule.ModuleVersionId);
            TrySection(summary, "Platform", delegate {
                summary.AppendLine(SystemInfo.operatingSystem);
                summary.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " | "
                    + SystemInfo.graphicsDeviceVersion + " | shader level=" + SystemInfo.graphicsShaderLevel);
            });
            summary.AppendLine("Enable/load checkpoint: " + LoadStageJournal.LastStage);
            summary.AppendLine("Checkpoint journal: " + LoadStageJournal.Status);
            summary.AppendLine("Expansion enabled=" + PatchState.ExpansionEnabled + ", save expanded="
                + PatchState.SaveExpanded + ", loading expanded=" + NetManagerDeserializeState.LoadingExpanded);
            summary.AppendLine("Node/segment loop limits=" + Capacity.NodeLoopLimit + "/" + Capacity.SegmentLoopLimit
                + ", node storage=" + Capacity.NodeStorageSize);
            TrySection(summary, "Live network", delegate {
                NetManager manager = Singleton<NetManager>.exists ? Singleton<NetManager>.instance : null;
                if (manager == null) { summary.AppendLine("NetManager not present (main menu or unloaded)."); return; }
                summary.AppendLine("Node buffer=" + (manager.m_nodes == null || manager.m_nodes.m_buffer == null
                    ? -1 : manager.m_nodes.m_buffer.Length) + ", segment buffer="
                    + (manager.m_segments == null || manager.m_segments.m_buffer == null ? -1 : manager.m_segments.m_buffer.Length));
            });
            TrySection(summary, "Object color map", delegate { CaptureColorMap(summary); });
            TrySection(summary, "Simulation and pause state", delegate { CaptureSimulationState(summary); });
            string persistent = Application.persistentDataPath;
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Colossal Order/Cities_Skylines/MoreNetworkCapacity/ErrorReports");
            DiagnosticBundleInput input = new DiagnosticBundleInput();
            input.OutputDirectory = OutputDirectory;
            input.Summary = summary.ToString();
            input.Mods = CaptureMods();
            input.Patches = CapturePatches();
            input.GameLogs = new[] { Path.Combine(Application.dataPath, "output_log.txt"),
                Path.Combine(persistent, "Player.log") };
            input.ReportDirectories = new[] { Path.Combine(persistent, "MoreNetworkCapacity/ErrorReports"), legacy };
            return input;
        }

        private static void CaptureColorMap(StringBuilder text)
        {
            RenderManager manager = Singleton<RenderManager>.exists ? Singleton<RenderManager>.instance : null;
            if (manager == null) { text.AppendLine("RenderManager not present."); return; }
            Texture2D texture = manager.m_objectColorMap;
            Texture global = Shader.GetGlobalTexture("_ObjectColorMap");
            text.AppendLine("Configured dimensions=" + RenderManager.OBJECT_COLORMAP_WIDTH + "x"
                + RenderManager.OBJECT_COLORMAP_HEIGHT + ", required entries=" + Capacity.GetColorIndexCapacity());
            text.AppendLine("Render index array=" + (manager.m_indices == null ? -1 : manager.m_indices.Length));
            text.AppendLine("Segment base=" + Capacity.SegmentColorIndexBase + ", node base="
                + Capacity.GetNodeColorIndexBase() + ", event base=" + Capacity.GetEventColorIndexBase());
            text.AppendLine("Global _ObjectColorMap=" + (global == null ? "missing" : global.GetInstanceID().ToString())
                + ", matches manager=" + (global == texture));
            if (texture == null) { text.AppendLine("Manager color texture missing."); return; }
            text.AppendLine("Manager texture=" + texture.GetInstanceID() + " | " + texture.width + "x"
                + texture.height + " | format=" + texture.format);
            // Eight samples only: never read/copy the whole texture or scan every node.
            foreach (int index in new[] { 0, 1, Capacity.SegmentColorIndexBase, 86016,
                Capacity.GetNodeColorIndexBase(), Capacity.GetNodeColorIndexBase() + 32768,
                Capacity.GetEventColorIndexBase(), Capacity.GetColorIndexCapacity() - 1 })
            {
                if (index < 0 || (long)index >= (long)texture.width * texture.height) continue;
                try
                {
                    Color color = texture.GetPixel(index % texture.width, index / texture.width);
                    text.AppendLine("Pixel " + index + " RGBA=" + color.r.ToString("F4", CultureInfo.InvariantCulture)
                        + "," + color.g.ToString("F4", CultureInfo.InvariantCulture) + ","
                        + color.b.ToString("F4", CultureInfo.InvariantCulture) + "," + color.a.ToString("F4", CultureInfo.InvariantCulture));
                }
                catch (Exception exception) { text.AppendLine("Pixel samples unavailable: " + exception.GetType().Name); break; }
            }
        }

        private static void CaptureSimulationState(StringBuilder text)
        {
            text.AppendLine("Captured on export. Opening Options may itself force a pause; a true pause flag alone does not identify its owner.");
            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            if (simulation == null) text.AppendLine("SimulationManager not present.");
            else
            {
                CaptureFields(text, simulation, new[] { "m_simulationPaused", "m_forcedSimulationPause",
                    "m_simulationSpeed", "m_selectedSimulationSpeed", "m_hasActions", "m_inSimulationStep",
                    "m_currentFrameIndex", "m_referenceFrameIndex" });
                FieldInfo threadField = FindInstanceField(simulation.GetType(), "m_simulationThread");
                Thread thread = threadField == null ? null : threadField.GetValue(simulation) as Thread;
                text.AppendLine("Simulation thread=" + (thread == null ? "unavailable" : "alive=" + thread.IsAlive + ", state=" + thread.ThreadState));
            }
            LoadingManager loading = Singleton<LoadingManager>.exists ? Singleton<LoadingManager>.instance : null;
            if (loading == null) text.AppendLine("LoadingManager not present.");
            else CaptureFields(text, loading, new[] { "m_currentlyLoading", "m_renderDataReady", "m_simulationDataReady" });
            // Field reads only: never initialize a tool or try to clear a pause lock.
            ToolManager tools = Singleton<ToolManager>.exists ? Singleton<ToolManager>.instance : null;
            if (tools == null) text.AppendLine("ToolManager not present.");
            else
            {
                // ToolManager inherits its ToolController field from SimulationManagerBase.
                FieldInfo properties = FindInstanceField(tools.GetType(), "m_properties");
                object controller = properties == null ? null : properties.GetValue(tools);
                if (controller == null) text.AppendLine("ToolController unavailable.");
                else CaptureFields(text, controller, new[] { "m_currentTool", "m_nextTool" });
            }
        }

        private static FieldInfo FindInstanceField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        private static void CaptureFields(StringBuilder text, object instance, string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    FieldInfo field = FindInstanceField(instance.GetType(), name);
                    if (field == null) { text.AppendLine(name + "=unavailable"); continue; }
                    object value = field.GetValue(instance);
                    text.AppendLine(name + "=" + (value == null ? "null" : value.GetType().IsValueType
                        ? value.ToString() : value.GetType().FullName));
                }
                catch (Exception exception) { text.AppendLine(name + "=unavailable: " + exception.GetType().Name); }
            }
        }

        private static string CaptureMods()
        {
            StringBuilder text = new StringBuilder("Enabled plugin names, Workshop ids and assembly versions\r\n");
            TrySection(text, "Plugins", delegate {
                int count = 0;
                foreach (PluginManager.PluginInfo plugin in PluginManager.instance.GetPluginsInfo())
                {
                    if (!plugin.isEnabled) continue;
                    if (++count > 512) { text.AppendLine("(additional plugins omitted)"); break; }
                    // These getters read cached metadata. Do not instantiate userModInstance.
                    text.AppendLine(Limit(plugin.name, 256) + " | Workshop=" + plugin.publishedFileID);
                    foreach (Assembly assembly in plugin.GetAssemblies())
                        text.AppendLine("  " + Limit(assembly.GetName().ToString(), 512));
                }
            });
            TrySection(text, "Loaded assemblies", delegate {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && i < 512; ++i)
                    text.AppendLine(Limit(assemblies[i].GetName().ToString(), 512));
            });
            return Limit(text.ToString(), 512 * 1024);
        }

        private static string CapturePatches()
        {
            StringBuilder text = new StringBuilder("Harmony render/pause patch metadata (not the final combined IL)\r\n");
            CaptureMethodPatches(text, typeof(RenderManager), "Awake");
            CaptureMethodPatches(text, typeof(NetManager), "UpdateColorMap");
            CaptureMethodPatches(text, typeof(NetNode), "RefreshJunctionData");
            CaptureMethodPatches(text, typeof(NetNode), "RenderInstance");
            CaptureMethodPatches(text, typeof(NetNode), "PopulateGroupData");
            CaptureMethodPatches(text, typeof(NetSegment), "RenderInstance");
            CaptureMethodPatches(text, typeof(BuildingManager), "UpdateColorMap");
            foreach (string name in new[] { "set_SimulationPaused", "set_ForcedSimulationPaused", "set_SelectedSimulationSpeed" })
                CaptureMethodPatches(text, typeof(SimulationManager), name);
            Type bindings = typeof(NetManager).Assembly.GetType("Bindings", false);
            if (bindings != null) CaptureMethodPatches(text, bindings, "set_isSimulationPaused");
            Type shortcuts = typeof(NetManager).Assembly.GetType("KeyShortcuts", false);
            if (shortcuts != null) CaptureMethodPatches(text, shortcuts, "SimulationPause");
            return Limit(text.ToString(), 512 * 1024);
        }

        private static void CaptureMethodPatches(StringBuilder text, Type type, string name)
        {
            TrySection(text, type.FullName + "." + name, delegate {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.Name != name) continue;
                    text.AppendLine(method.ToString());
                    HarmonyLib.Patches patches = Harmony.GetPatchInfo(method);
                    if (patches == null) { text.AppendLine("  No Harmony patches"); continue; }
                    AppendPatches(text, "Prefix", patches.Prefixes);
                    AppendPatches(text, "Postfix", patches.Postfixes);
                    AppendPatches(text, "Transpiler", patches.Transpilers);
                    AppendPatches(text, "Finalizer", patches.Finalizers);
                }
            });
        }

        private static void AppendPatches(StringBuilder text, string kind, IEnumerable<Patch> patches)
        {
            int count = 0;
            foreach (Patch patch in patches)
            {
                if (++count > 64) { text.AppendLine("  (additional patches omitted)"); break; }
                MethodInfo method = patch.PatchMethod;
                text.AppendLine("  " + kind + " | owner=" + Limit(patch.owner, 256)
                    + " | priority=" + patch.priority + " | method=" + (method == null ? "unknown"
                    : method.DeclaringType.FullName + "." + method.Name)
                    + " | before=" + string.Join(",", patch.before ?? new string[0])
                    + " | after=" + string.Join(",", patch.after ?? new string[0]));
            }
        }

        private static void TrySection(StringBuilder text, string name, Action action)
        {
            text.AppendLine("\r\n[" + name + "]");
            try { action(); }
            catch (Exception exception) { text.AppendLine("Unavailable: " + exception.GetType().Name + " | " + exception.Message); }
        }
        private static string Limit(string value, int maximum)
        {
            if (value == null) return "(none)";
            return value.Length <= maximum ? value : value.Substring(0, maximum) + " [truncated]";
        }
    }
}
