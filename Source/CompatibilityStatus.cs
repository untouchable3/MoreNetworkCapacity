using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CitiesHarmony.API;
using ColossalFramework.Plugins;
using HarmonyLib;
using MoreNetworkCapacity.Compatibility;

namespace MoreNetworkCapacity
{
    internal sealed class CompatibilityStatus
    {
        private static readonly string[] Known = {
            "ImprovedPublicTransport2", "ImprovedPublicTransport3", "ImprovedPublicTransport4",
            "TrafficManager", "TransportLinesManager", "TransferManagerCE", "NodeControllerRenewal",
            "DirectConnectRoads", "EightyOne2", "IntersectionMarkingTool", "MoveIt",
            "CSURToolBox", "NetworkSkins", "AdaptiveRoads", "ZoningAdjuster", "SingleTrackAI", "NetworkMultitool"
        };
        private readonly Dictionary<Assembly, bool> plugins = new Dictionary<Assembly, bool>();
        private readonly Dictionary<Assembly, List<string>> methods = new Dictionary<Assembly, List<string>>();
        private readonly SortedDictionary<string, Assembly> loaded = new SortedDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> Names = new List<string>();
        private readonly bool patchInfoAvailable;

        internal CompatibilityStatus()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                loaded[assembly.GetName().Name] = assembly;
            foreach (PluginManager.PluginInfo plugin in PluginManager.instance.GetPluginsInfo())
                foreach (Assembly assembly in plugin.GetAssemblies())
                {
                    bool otherEnabled;
                    plugins.TryGetValue(assembly, out otherEnabled);
                    plugins[assembly] = otherEnabled || plugin.isEnabled;
                }
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                CollectMethods();
                patchInfoAvailable = true;
            }
            foreach (string name in Known)
                if (loaded.ContainsKey(name)) Names.Add(name);
            foreach (Assembly assembly in methods.Keys)
                if (plugins.ContainsKey(assembly) && !Names.Contains(assembly.GetName().Name))
                    Names.Add(assembly.GetName().Name);
            Names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Known)
                if (!loaded.ContainsKey(name)) Names.Add(name);
        }

        private void CollectMethods()
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                HarmonyLib.Patches info = Harmony.GetPatchInfo(method);
                if (info == null || !info.Owners.Contains(HarmonyBootstrap.HarmonyId) || method.DeclaringType == null) continue;
                Assembly assembly = method.DeclaringType.Assembly;
                List<string> list;
                if (!methods.TryGetValue(assembly, out list))
                {
                    list = new List<string>();
                    methods.Add(assembly, list);
                }
                list.Add(method.DeclaringType.Name + "." + method.Name);
            }
        }

        // IPT4's Instance getter may create default settings. Inspect only the registered live setting.
        private static bool? ReadStopsSetting(Assembly assembly)
        {
            try
            {
                const BindingFlags fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Type domainType = assembly.GetType("CSLModsCommon.Manager.Domain", true);
                object domain = domainType.GetField("_defaultDomain", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                if (domain == null) return null;
                Type managerType = assembly.GetType("CSLModsCommon.Manager.SettingManager", true);
                object[] args = { managerType, null };
                if (!(bool)domainType.GetMethod("TryGetManager", fields).Invoke(domain, args)) return null;
                IDictionary settings = (IDictionary)managerType.GetField("_settings", fields).GetValue(args[1]);
                Type settingType = assembly.GetType("ImprovedPublicTransport.ModSetting", true);
                object source = settings[settingType];
                if (source == null) return null;
                object setting = source.GetType().GetProperty("Setting", fields).GetValue(source, null);
                if (!settingType.IsInstanceOfType(setting)) return null;
                return (bool)settingType.GetProperty("EnableStopsAndStations", fields).GetValue(setting, null);
            }
            catch (Exception) { return null; }
        }
        internal string PluginState(string name)
        {
            Assembly assembly;
            if (!loaded.TryGetValue(name, out assembly)) return UiLanguage.T("未加载", "Not loaded");
            bool enabled;
            if (!plugins.TryGetValue(assembly, out enabled)) return UiLanguage.T("已加载", "Loaded");
            return enabled ? UiLanguage.T("已启用", "Enabled") : UiLanguage.T("已停用", "Disabled");
        }

        internal string Version(string name)
        {
            Assembly assembly;
            return loaded.TryGetValue(name, out assembly) ? assembly.GetName().Version.ToString() : "—";
        }

        internal string PatchCount(string name)
        {
            Assembly assembly;
            if (!loaded.TryGetValue(name, out assembly)) return "—";
            if (!patchInfoAvailable) return "?";
            List<string> list;
            return methods.TryGetValue(assembly, out list) ? list.Count.ToString() : "0";
        }

        internal string[] PatchedMethods(string name)
        {
            Assembly assembly;
            List<string> list;
            if (!loaded.TryGetValue(name, out assembly) || !methods.TryGetValue(assembly, out list)) return new string[0];
            string[] result = list.ToArray();
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        internal string Describe(string name)
        {
            Assembly assembly;
            if (!loaded.TryGetValue(name, out assembly)) return UiLanguage.T("当前未加载此程序集。", "Assembly not loaded.");
            StringBuilder text = new StringBuilder();
            Guid supported = Guid.Empty;
            if (name == "ImprovedPublicTransport2") supported = Ipt2NodeCompatibility.SupportedBuild;
            if (name == "ImprovedPublicTransport3") supported = Ipt3NodeCompatibility.SupportedBuild;
            if (name == "ImprovedPublicTransport4") supported = Ipt4NodeCompatibility.SupportedBuild;
            if (supported != Guid.Empty)
                text.AppendLine(assembly.ManifestModule.ModuleVersionId == supported
                    ? UiLanguage.T("构建匹配  已识别", "Build match  Recognized")
                    : UiLanguage.T("构建匹配  非已适配版本", "Build match  Unrecognized"));
            if (name == "ImprovedPublicTransport4")
            {
                bool enabled;
                bool? setting = supported == assembly.ManifestModule.ModuleVersionId &&
                    plugins.TryGetValue(assembly, out enabled) && enabled ? ReadStopsSetting(assembly) : null;
                text.Append(UiLanguage.T("候车限流功能开关  ", "Stops & Stations setting  "));
                text.AppendLine(setting.HasValue
                    ? (setting.Value ? UiLanguage.T("开启", "On") : UiLanguage.T("关闭", "Off"))
                    : UiLanguage.T("未知", "Unknown"));
            }
            if (!patchInfoAvailable) text.AppendLine(UiLanguage.T("Harmony 补丁状态不可用", "Harmony patch status unavailable"));
            else if (!methods.ContainsKey(assembly))
                text.AppendLine(UiLanguage.T("未发现 MNC 直接挂接的方法。", "No direct MNC patches found."));
            return text.ToString().TrimEnd();
        }
    }
}
