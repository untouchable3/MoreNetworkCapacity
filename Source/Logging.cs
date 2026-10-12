using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class Logging
    {
        private const string Prefix = "[MoreNetworkCapacity] ";

        public static void Info(string message)
        {
            ErrorReport.Remember("INFO", message);
            Debug.Log(Prefix + message);
        }

        public static void Warning(string message)
        {
            ErrorReport.Remember("WARN", message);
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            ErrorReport.Capture(message, null);
            Debug.LogError(Prefix + message);
        }
    }
}

