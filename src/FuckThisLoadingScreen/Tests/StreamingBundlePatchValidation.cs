using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine;

internal static class StreamingBundlePatchValidation
{
    // Real EFT bundles (uncompressed), read in place: three eligible textures; one streamable + one eligible; none eligible.
    private const string Eligible = "assets/content/items/ammo/patrons/patron_6mm_airsoft.bundle";
    private const string Mixed = "assets/content/items/mods/pistol grips/pistolgrip_p226_sig_p226_std_fde.bundle";
    private const string Ineligible = "assets/content/items/mods/mounts/mount_keymod_vltor_6_inch_casv_rail_section_lod1_diff.bundle";

    internal static void Installed(Action<bool, string> check, string install, string project)
    {
        string windows = Path.Combine(install, "EscapeFromTarkov_Data", "StreamingAssets", "Windows");
        string eligible = Path.Combine(windows, Eligible), mixed = Path.Combine(windows, Mixed), ineligible = Path.Combine(windows, Ineligible);
        string root = Path.Combine(project, "streaming-bundle-patch-validation", Guid.NewGuid().ToString("N"));
        string index = Path.Combine(root, "index.txt");

        long[] flags = Find(eligible);
        check(flags.Length == 3 && Find(mixed).Length == 1 && Find(ineligible).Length == 0,
            "Flag offsets cover exactly the eligible textures of real EFT bundles");

        // The patching stream differs from the file only at the flag offsets, for reads crossing them at odd sizes.
        byte[] raw = File.ReadAllBytes(eligible), served;
        using (var stream = new StreamingBundlePatch.FlagPatchStream(eligible, flags))
        using (var copy = new MemoryStream())
        {
            var buffer = new byte[4093];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) copy.Write(buffer, 0, read);
            served = copy.ToArray();
            stream.Seek(flags[1], SeekOrigin.Begin);
            check(stream.ReadByte() == 1 && stream.Position == flags[1] + 1, "Seeking to a flag serves the patched byte");
        }
        var differences = Enumerable.Range(0, raw.Length).Where(i => raw[i] != served[i]).Select(i => (long)i).ToArray();
        check(served.Length == raw.Length && differences.SequenceEqual(flags) && flags.All(f => raw[f] == 0 && served[f] == 1),
            "Patching stream changes only the streaming flags, 0 -> 1");
        using (var stream = new StreamingBundlePatch.FlagPatchStream(eligible, new[] { 0L }))
            check(stream.ReadByte() == raw[0] && raw[0] != 0, "Patching stream leaves an offset that does not hold false unchanged");

        var patch = new StreamingBundlePatch(windows, StreamingBundlePatch.DefaultFolders, index, null);
        check(patch.InScope(eligible) && !patch.InScope(Path.Combine(windows, "assets/content/locations/x.bundle")), "Only item/weapon bundles are in scope");
        check(patch.Load(eligible) == null && patch.Native == 1, "A bundle not indexed yet loads natively without scanning");
        int scanned = patch.IndexFiles(new[] { eligible, mixed, ineligible, Path.Combine(windows, "assets/content/locations/x.bundle") }, System.Threading.CancellationToken.None, out int failed, null, 3);
        check(scanned == 3 && failed == 0, "Indexing scans in-scope bundles only: scanned=" + scanned + " failed=" + failed);
        check(patch.Load(ineligible) == null && patch.Native == 1, "Indexed bundles without eligible textures load natively");
        patch.SaveIndex();
        var reloaded = new StreamingBundlePatch(windows, StreamingBundlePatch.DefaultFolders, index, null);
        check(reloaded.LoadIndex() == 3 && reloaded.IndexFiles(new[] { eligible, mixed, ineligible }, System.Threading.CancellationToken.None, out _) == 0,
            "A saved index is reused without rescanning unchanged bundles");
        File.WriteAllLines(index, File.ReadAllLines(index).Select(line => line.StartsWith(Eligible, StringComparison.Ordinal) ? line.Replace("|" + new FileInfo(eligible).Length + "|", "|1|") : line));
        var stale = new StreamingBundlePatch(windows, StreamingBundlePatch.DefaultFolders, index, null);
        stale.LoadIndex();
        check(stale.Load(eligible) == null && stale.Native == 1, "An index entry for a different file version is ignored");

        var harmony = new Harmony("ftls.streaming-bundle-patch.installed");
        var loaded = new List<AssetBundle>();
        try
        {
            StreamingBundlePatch.Install(harmony);
            StreamingBundlePatch.Active = patch;
            AssetBundleCreateRequest replaced = null;
            check(StreamingBundlePatch.Prefix(eligible, ref replaced, false) && replaced == null, "A load already replaced by another prefix is left alone");

            // Textures are implicit prefab dependencies, not container assets: compare loaded objects before/after.
            var before = new HashSet<Texture2D>(Resources.FindObjectsOfTypeAll<Texture2D>());
            var bundle = AssetBundle.LoadFromFileAsync(eligible).assetBundle;
            if (bundle != null) { loaded.Add(bundle); bundle.LoadAllAssets(); }
            var textures = Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => !before.Contains(t)).ToArray();
            check(bundle != null && textures.Length == 3 && textures.All(t => t.streamingMipmaps) && patch.OpenStreams == 1,
                "Patched LoadFromFileAsync loads eligible textures as mip-streamable: " + string.Join(", ", textures.Select(t => t.name + "=" + t.streamingMipmaps)));

            before = new HashSet<Texture2D>(Resources.FindObjectsOfTypeAll<Texture2D>());
            bundle = AssetBundle.LoadFromFileAsync(mixed).assetBundle;
            if (bundle != null) { loaded.Add(bundle); bundle.LoadAllAssets(); }
            textures = Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => !before.Contains(t)).ToArray();
            check(bundle != null && textures.Length == 2 && textures.All(t => t.streamingMipmaps), "Already-streaming textures are unchanged and eligible ones stream");

            patch.Sweep(1e12);
            check(patch.OpenStreams == 2, "Streams of loaded bundles stay open");
            foreach (var item in loaded) item.Unload(true);
            loaded.Clear();
            patch.Sweep(2e12);
            check(patch.OpenStreams == 0 && patch.Bundles == 2 && patch.Textures == 4, "Unloaded bundles release their streams");
        }
        finally
        {
            foreach (var item in loaded) item.Unload(true);
            StreamingBundlePatch.Active = null;
            harmony.UnpatchSelf();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static long[] Find(string path)
    {
        var manager = new AssetsTools.NET.Extra.AssetsManager();
        try { return StreamingBundlePatch.FindFlagOffsets(manager, path); }
        finally { manager.UnloadAll(true); }
    }
}
