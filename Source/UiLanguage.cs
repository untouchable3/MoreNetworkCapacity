using System;
using ColossalFramework;
using ColossalFramework.Globalization;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class UiLanguage
    {
        private const string Key = "MoreNetworkCapacity.UI.Language";
        private static int choice = -1;

        internal static int Choice
        {
            get
            {
                if (choice < 0) choice = Mathf.Clamp(PlayerPrefs.GetInt(Key, 0), 0, 2);
                return choice;
            }
            set
            {
                choice = Mathf.Clamp(value, 0, 2);
                PlayerPrefs.SetInt(Key, choice);
                PlayerPrefs.Save();
            }
        }

        internal static bool Chinese
        {
            get
            {
                if (Choice != 0) return Choice == 1;
                string language = LocaleManager.exists ? LocaleManager.instance.language : null;
                return !String.IsNullOrEmpty(language) && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static string T(string zh, string en) { return Chinese ? zh : en; }
    }
}
