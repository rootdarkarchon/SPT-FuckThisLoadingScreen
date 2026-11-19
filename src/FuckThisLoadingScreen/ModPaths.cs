using System;
using System.IO;
using System.Text;

namespace FuckThisLoadingScreen
{
    // Where this mod reads and writes files.
    //  - PluginDir: BepInEx/plugins/FuckThisLoadingScreen/, shipped files only (dlls, licenses). Read-only; never write there.
    //  - Root: BepInEx/config/FuckThisLoadingScreen/, everything the mod creates at runtime, one subfolder per kind.
    // The BepInEx .cfg itself stays at BepInEx/config/<GUID>.cfg. Folders are created lazily, only when used.
    // Nothing is pruned: files and run folders accumulate until the user deletes them.
    internal static class ModPaths
    {
        internal const string Cache = "cache";
        internal const string State = "state";
        internal const string Captures = "captures";
        internal const string Benchmarks = "benchmarks";
        internal const string Dumps = "dumps";
        internal const string Logs = "logs";

        internal const string LoadingReportsFolder = "loading-reports";
        internal const string HideoutResearchFolder = "hideout-research";
        internal const string TextureInventoryFolder = "texture-inventory";
        internal const string StreamingBudgetFolder = "streaming-budget";
        internal const string StreamableTextureIndexFile = "StreamableTextureIndex.txt";

        internal static readonly string Root = Path.Combine(BepInEx.Paths.ConfigPath, PluginInfo.ModName);

        /// <summary>
        /// Data folder of 1.0.x/1.1.0 builds, a sibling of the BepInEx config folder. Only read by the one-time migration.
        /// </summary>
        internal static readonly string LegacyRoot = Path.Combine(Path.GetDirectoryName(BepInEx.Paths.ConfigPath), PluginInfo.ModName);

        internal static readonly string PluginDir =
            Path.GetDirectoryName(typeof(ModPaths).Assembly.Location) ?? BepInEx.Paths.PluginPath;

        // <Root>/<kind>[/<subfolder>] without creating it.
        internal static string Locate(string kind, string subfolder = null)
        {
            return subfolder == null ? Path.Combine(Root, kind) : Path.Combine(Root, kind, subfolder);
        }

        // <Root>/<kind>[/<subfolder>], created on first use.
        internal static string Dir(string kind, string subfolder = null)
        {
            var path = Locate(kind, subfolder);
            Directory.CreateDirectory(path);
            return path;
        }

        // <Root>/<kind>/yyyyMMdd-HHmmss-<label>, a fresh folder for one run (capture, benchmark, dump).
        internal static string NewRun(string kind, string label)
        {
            var path = Path.Combine(Dir(kind), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Sanitize(label));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Sanitize(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return "run";
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(label.Length);
            foreach (var c in label.Trim())
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
            }

            return builder.ToString();
        }
    }
}
