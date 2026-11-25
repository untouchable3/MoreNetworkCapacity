using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;

namespace MoreNetworkCapacity
{
    internal static class GameVersion
    {
        private const string TargetAssemblyHash =
            "EA319CA84D96EA004FC413762370AE32F08279ED9EE6B164BE97016B05265FAD";

        private static readonly Guid TargetModuleVersionId =
            new Guid("34370a81-9d28-47db-b3d0-af5b4ab4c9a1");

        private const string PreviousAssemblyHash =
            "DF557994A03C93D8BB4A14F80B1FBCC0FB29D547658442445D078D069BCDD9FE";

        private static readonly Guid PreviousModuleVersionId =
            new Guid("e8d46364-9995-4176-87e0-ae3192b9a20b");

        private static bool _checked;
        private static bool _supported;

        public static bool IsSupported
        {
            get
            {
                EnsureChecked();
                return _supported;
            }
        }

        private static void EnsureChecked()
        {
            if (_checked)
            {
                return;
            }

            _checked = true;
            Assembly assembly = typeof(NetManager).Assembly;
            Guid mvid = assembly.ManifestModule.ModuleVersionId;

            bool previousBuild = mvid == PreviousModuleVersionId;
            if (mvid != TargetModuleVersionId && !previousBuild)
            {
                Logging.Error("Assembly-CSharp MVID does not match the supported build.");
                return;
            }

            string location = ResolveAssemblyPath(assembly);
            if (location == null)
            {
                Logging.Error(
                    "Could not locate the loaded Assembly-CSharp file.");
                return;
            }

            string hash = ComputeHash(location);
            string expectedHash = previousBuild ? PreviousAssemblyHash : TargetAssemblyHash;
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                Logging.Error(
                    "Assembly-CSharp SHA-256 does not match the supported build: "
                    + location);
                return;
            }

            _supported = true;
            Logging.Info("Supported game assembly detected: " + location);
        }

        private static string ResolveAssemblyPath(Assembly assembly)
        {
            string location = assembly.Location;
            if (IsExistingFile(location))
            {
                return location;
            }

            string modulePath =
                assembly.ManifestModule.FullyQualifiedName;
            if (IsExistingFile(modulePath))
            {
                return modulePath;
            }

            string dataPath = Application.dataPath;
            if (!string.IsNullOrEmpty(dataPath))
            {
                string candidate = Path.Combine(
                    Path.Combine(dataPath, "Managed"),
                    "Assembly-CSharp.dll");
                if (IsExistingFile(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsExistingFile(string path)
        {
            return !string.IsNullOrEmpty(path)
                && File.Exists(path);
        }

        private static string ComputeHash(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] bytes = sha.ComputeHash(stream);
                    return BitConverter.ToString(bytes).Replace("-", string.Empty);
                }
            }
        }
    }
}
