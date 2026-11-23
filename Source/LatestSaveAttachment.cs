using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework.IO;
using ColossalFramework.Packaging;

namespace MoreNetworkCapacity
{
    internal static class LatestSaveAttachment
    {
        internal static void SelectCandidates(DiagnosticBundleInput input)
        {
            input.SaveCandidates = new string[0];
            try
            {
                string root = Path.GetFullPath(DataLocation.saveLocation).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Package.Asset asset in PackageManager.FilterAssets(new[] { UserAssetType.SaveGameMetaData }))
                {
                    if (asset == null || asset.package == null) continue;
                    string path = asset.package.packagePath;
                    if (string.IsNullOrEmpty(path) || PackageManager.IsCloudPath(path)) continue;
                    path = Path.GetFullPath(path);
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Path.GetExtension(path), ".crp", StringComparison.OrdinalIgnoreCase))
                        paths.Add(path);
                }
                input.SaveCandidates = new List<string>(paths).ToArray();
            }
            catch (Exception error) { input.SaveLookupError = error.Message; }
        }
    }
}
