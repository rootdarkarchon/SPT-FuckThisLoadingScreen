using System.IO;

namespace FuckThisLoadingScreen
{
    /// <summary>
    /// One-time move of the data that 1.0.x/1.1.0 builds wrote to BepInEx/FuckThisLoadingScreen into the
    /// BepInEx/config/FuckThisLoadingScreen/&lt;kind&gt; layout. Existing destination entries are never overwritten;
    /// a source entry that collides stays where it is.
    /// </summary>
    internal static class LegacyDataMigration
    {
        private const string LegacyHideoutFolder = "HideoutResearch";
        private const string LegacyInventoryFolder = "TextureInventory";

        /// <summary>
        /// Moves the loading reports (files directly in the legacy root), the hideout research and texture inventory folders and the
        /// streamable texture index, then removes the legacy folders that ended up empty.
        /// </summary>
        /// <returns>The number of files and folders moved; 0 when there was nothing to migrate.</returns>
        internal static int Run(string legacyRoot, string reportsDirectory, string hideoutDirectory, string inventoryDirectory, string indexFile)
        {
            if (!Directory.Exists(legacyRoot)) return 0;

            var moved = MoveContents(Path.Combine(legacyRoot, LegacyHideoutFolder), hideoutDirectory);
            moved += MoveContents(Path.Combine(legacyRoot, LegacyInventoryFolder), inventoryDirectory);

            var cacheDirectory = Path.GetDirectoryName(indexFile);
            var indexName = Path.GetFileName(indexFile);
            foreach (var file in Directory.GetFiles(legacyRoot))
            {
                var name = Path.GetFileName(file);
                // Includes a leftover "<index>.tmp" from an interrupted save.
                var destination = name.StartsWith(indexName, System.StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(cacheDirectory, name)
                    : Path.Combine(reportsDirectory, name);
                moved += MoveFile(file, destination);
            }

            RemoveIfEmpty(legacyRoot);
            return moved;
        }

        private static int MoveContents(string source, string destination)
        {
            if (!Directory.Exists(source)) return 0;

            var moved = 0;
            foreach (var entry in Directory.GetFileSystemEntries(source))
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target)) continue;
                Directory.CreateDirectory(destination);
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target);
                moved++;
            }

            RemoveIfEmpty(source);
            return moved;
        }

        private static int MoveFile(string source, string destination)
        {
            if (File.Exists(destination)) return 0;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Move(source, destination);
            return 1;
        }

        private static void RemoveIfEmpty(string directory)
        {
            if (Directory.GetFileSystemEntries(directory).Length == 0) Directory.Delete(directory);
        }
    }
}
