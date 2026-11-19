using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // EXPERIMENTAL: mark eligible textures of EFT item/weapon bundles mip-streamable while Unity reads them.
    // An uncompressed bundle stores each Texture2D's serialized m_StreamingMipmaps byte at a fixed file offset.
    // Instead of copying or rewriting the bundle, the load is served through a read-only stream over the
    // original file that returns 1 at exactly those offsets. Nothing is written to disk or buffered in memory.
    // Offsets are found by a background scan at startup and kept in a small index file (bundle path, length,
    // timestamp, offsets), so loads never scan; a bundle not indexed yet loads natively.
    internal sealed class StreamingBundlePatch
    {
        internal static StreamingBundlePatch Active;
        internal static readonly string[] DefaultFolders = { "assets/content/items", "assets/content/weapons" };
        private readonly string sourceRoot, indexPath;
        private readonly string[] folders, roots;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> offsets = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly List<Open> open = new List<Open>();
        private System.Threading.CancellationTokenSource indexing;
        private double nextSweep;
        internal int Bundles, Textures, Native, Failures;

        private sealed class Entry { internal long Length, Ticks; internal long[] Offsets; }
        private sealed class Open { internal AssetBundleCreateRequest Request; internal AssetBundle Bundle; internal Stream Stream; }

        internal StreamingBundlePatch(string sourceRoot, IEnumerable<string> folders, string indexPath, Action<string> log)
        {
            this.sourceRoot = Normalize(sourceRoot) + "/";
            this.folders = new List<string>(folders).ToArray();
            var list = new List<string>();
            foreach (string folder in this.folders) list.Add(Normalize(Path.Combine(sourceRoot, folder)) + "/");
            roots = list.ToArray();
            this.indexPath = indexPath;
            this.log = log;
        }

        internal static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        internal int OpenStreams => open.Count;

        #region Load redirection

        internal static void Install(Harmony harmony)
        {
            var load = AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.LoadFromFileAsync), new[] { typeof(string) });
            if (load?.GetMethodBody() == null) throw new MissingMethodException("AssetBundle.LoadFromFileAsync(string) is unavailable");
            // Last: another mod (Rupture) may already substitute this load; then the original does not run.
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(StreamingBundlePatch), nameof(Prefix)) { priority = Priority.Last });
        }

        internal static bool Prefix(string __0, ref AssetBundleCreateRequest __result, bool __runOriginal)
        {
            if (!__runOriginal || __0 == null || Active == null) return true;
            try
            {
                var request = Active.Load(__0);
                if (request == null) return true;
                __result = request;
                return false;
            }
            catch (Exception error)
            {
                Active.Failures++;
                Active.log?.Invoke("Streamable texture patch skipped " + Path.GetFileName(__0) + "; original bundle loads: " + error.GetType().Name + ": " + error.Message);
                return true;
            }
        }

        /// <summary>Start a patched load, or return null when the bundle is out of scope, not indexed yet or has nothing to patch.</summary>
        internal AssetBundleCreateRequest Load(string path)
        {
            string key = Key(path);
            if (key == null) return null;
            long[] flags = Indexed(key, new FileInfo(path));
            if (flags == null) { Native++; return null; }
            if (flags.Length == 0) return null;
            var stream = new FlagPatchStream(path, flags);
            AssetBundleCreateRequest request;
            try { request = AssetBundle.LoadFromStreamAsync(stream); }
            catch { stream.Dispose(); throw; }
            open.Add(new Open { Request = request, Stream = stream });
            Bundles++; Textures += flags.Length;
            if (Bundles == 1 || Bundles % 100 == 0)
                log?.Invoke("Streamable texture patch: bundles=" + Bundles + ", textures=" + Textures + ", not indexed yet (native)=" + Native + ", open streams=" + open.Count + ", skipped=" + Failures + ".");
            return request;
        }

        internal bool InScope(string path) => Key(path) != null;

        /// <summary>Index key: path relative to the source root, or null when outside the patched folders.</summary>
        private string Key(string path)
        {
            string normalized = Normalize(path);
            foreach (string root in roots)
                if (normalized.StartsWith(root, StringComparison.Ordinal)) return normalized.Substring(sourceRoot.Length);
            return null;
        }

        /// <summary>Close streams whose bundle was unloaded or failed to load. AssetBundle.Unload is native-only, so poll.</summary>
        internal void Sweep(double now)
        {
            while (messages.TryDequeue(out string message)) log?.Invoke(message);
            if (open.Count == 0 || now < nextSweep) return;
            nextSweep = now + 1000;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var entry = open[i];
                if (entry.Bundle == null)
                {
                    if (!entry.Request.isDone) continue;
                    entry.Bundle = entry.Request.assetBundle;
                    // A failed load never had a bundle object; an unloaded one compares equal to null.
                    if (!ReferenceEquals(entry.Bundle, null) && entry.Bundle) continue;
                }
                else if (entry.Bundle) continue;
                entry.Stream.Dispose();
                open.RemoveAt(i);
            }
        }

        internal void Stop()
        {
            indexing?.Cancel();
            // Bundles still loaded keep reading their stream; leave those open for the process lifetime.
            for (int i = open.Count - 1; i >= 0; i--)
                if (open[i].Request.isDone && !open[i].Request.assetBundle) { open[i].Stream.Dispose(); open.RemoveAt(i); }
        }

        #endregion

        #region Offsets index

        private long[] Indexed(string key, FileInfo info)
        {
            lock (gate)
                return offsets.TryGetValue(key, out var entry) && entry.Length == info.Length && entry.Ticks == info.LastWriteTimeUtc.Ticks ? entry.Offsets : null;
        }

        /// <summary>Load the saved index, then scan new or changed bundles on a background thread and save.</summary>
        internal System.Threading.Tasks.Task StartIndexing()
        {
            var cancellation = indexing = new System.Threading.CancellationTokenSource();
            return System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    int loaded = LoadIndex();
                    var files = new List<string>();
                    foreach (string folder in folders)
                    {
                        string directory = Path.Combine(sourceRoot, folder);
                        if (Directory.Exists(directory)) files.AddRange(Directory.EnumerateFiles(directory, "*.bundle", SearchOption.AllDirectories));
                    }
                    var pending = files.FindAll(path => { string key = Key(path); return key != null && Indexed(key, new FileInfo(path)) == null; });
                    int workers = ScanWorkers;
                    if (pending.Count > 0)
                        messages.Enqueue("Streamable texture index: scanning " + pending.Count + " of " + files.Count + " bundles on " + workers + " background threads. Takes a while the first time; " +
                            "later launches rescan only changed bundles. Until a bundle is scanned it loads without the patch.");
                    double nextReport = 10;
                    int scanned = IndexFiles(pending, cancellation.Token, out int failed, done =>
                    {
                        if (timer.Elapsed.TotalSeconds < nextReport) return;
                        nextReport = timer.Elapsed.TotalSeconds + 10;
                        messages.Enqueue("Streamable texture index: " + done + "/" + pending.Count + " scanned (" + (100 * done / pending.Count) + "%, " + timer.Elapsed.TotalSeconds.ToString("0") + " s).");
                    }, workers);
                    // Saved even when cancelled, so a partial scan is not repeated.
                    if (scanned > 0) SaveIndex();
                    if (cancellation.IsCancellationRequested) return;
                    int eligible = 0, textures = 0;
                    lock (gate) foreach (var entry in offsets.Values) if (entry.Offsets.Length > 0) { eligible++; textures += entry.Offsets.Length; }
                    messages.Enqueue("Streamable texture index ready in " + timer.Elapsed.TotalSeconds.ToString("0.0") + " s: " + files.Count + " bundles (" + loaded + " from saved index, " +
                        scanned + " scanned, " + failed + " unreadable); " + eligible + " bundles with " + textures + " eligible textures.");
                }
                catch (Exception error)
                {
                    messages.Enqueue("Streamable texture index failed; unindexed bundles load natively: " + error);
                    try { SaveIndex(); } catch { }
                }
            });
        }

        /// <summary>Scan threads: half the logical processors, 1-8 (more threads were slower under Mono).</summary>
        internal static int ScanWorkers => Math.Max(1, Math.Min(8, Environment.ProcessorCount / 2));

        /// <summary>
        /// Scan bundles whose index entry is missing or stale on <paramref name="workers"/> below-normal threads,
        /// each with its own AssetsManager; stops early when cancelled. Unreadable bundles are indexed as having
        /// nothing to patch. <paramref name="progress"/> runs under the index lock.
        /// </summary>
        internal int IndexFiles(IList<string> files, System.Threading.CancellationToken token, out int failed, Action<int> progress = null, int workers = 1)
        {
            int next = -1, scanned = 0, failures = 0;
            Exception fault = null;
            void Work()
            {
                // No template cache: each bundle is read with its own type tree.
                var manager = new AssetsManager();
                try
                {
                    int at;
                    while (!token.IsCancellationRequested && (at = System.Threading.Interlocked.Increment(ref next)) < files.Count)
                    {
                        string path = files[at], key = Key(path);
                        if (key == null) continue;
                        var info = new FileInfo(path);
                        if (Indexed(key, info) != null) continue;
                        long[] found;
                        try { found = FindFlagOffsets(manager, path); }
                        catch { found = Array.Empty<long>(); System.Threading.Interlocked.Increment(ref failures); }
                        finally { manager.UnloadAll(); }
                        lock (gate)
                        {
                            offsets[key] = new Entry { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Offsets = found };
                            scanned++;
                            progress?.Invoke(scanned);
                        }
                    }
                }
                catch (Exception error) { fault = error; }
                finally { manager.UnloadAll(true); }
            }
            var threads = new List<System.Threading.Thread>();
            for (int i = 1; i < Math.Max(1, workers); i++)
            {
                var thread = new System.Threading.Thread(Work) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "FTLS texture index " + i };
                thread.Start();
                threads.Add(thread);
            }
            Work();
            foreach (var thread in threads) thread.Join();
            failed = failures;
            if (fault != null) throw new AggregateException("Streamable texture index worker failed", fault);
            return scanned;
        }

        // One line per bundle: relative path|length|ticks|offset,offset,...
        internal int LoadIndex()
        {
            if (indexPath == null || !File.Exists(indexPath)) return 0;
            int count = 0;
            foreach (string line in File.ReadLines(indexPath))
            {
                string[] parts = line.Split('|');
                if (parts.Length != 4 || !long.TryParse(parts[1], out long length) || !long.TryParse(parts[2], out long ticks)) continue;
                var found = new List<long>();
                bool valid = true;
                foreach (string text in parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (long.TryParse(text, out long offset)) found.Add(offset); else valid = false;
                if (!valid) continue;
                found.Sort();
                lock (gate) offsets[parts[0]] = new Entry { Length = length, Ticks = ticks, Offsets = found.ToArray() };
                count++;
            }
            return count;
        }

        internal void SaveIndex()
        {
            if (indexPath == null) return;
            var lines = new List<string>();
            lock (gate)
                foreach (var pair in offsets)
                    lines.Add(pair.Key + "|" + pair.Value.Length + "|" + pair.Value.Ticks + "|" + string.Join(",", pair.Value.Offsets));
            lines.Sort(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath));
            string temporary = indexPath + ".tmp";
            File.WriteAllLines(temporary, lines);
            if (File.Exists(indexPath)) File.Delete(indexPath);
            File.Move(temporary, indexPath);
        }

        /// <summary>
        /// Absolute file offsets of m_StreamingMipmaps for eligible textures in an uncompressed bundle:
        /// 2D, mipmapped, pixels in an external stream, not CPU-readable, not exempt from the mipmap limit,
        /// not yet streamable. Compressed bundles return none.
        /// </summary>
        internal static long[] FindFlagOffsets(AssetsManager manager, string path)
        {
            var bundle = manager.LoadBundleFile(path, false);
            if (bundle.file.DataIsCompressed) return Array.Empty<long>();
            long dataStart = bundle.file.Header.GetFileDataOffset();
            var result = new List<long>();
            var directories = bundle.file.BlockAndDirInfo.DirectoryInfos;
            for (int index = 0; index < directories.Count; index++)
            {
                // UnityFS directory flag 4 marks a serialized assets file (not a .resS stream).
                if ((directories[index].Flags & 4) == 0) continue;
                var file = manager.LoadAssetsFileFromBundle(bundle, index, false);
                var reader = file.file.Reader;
                foreach (var texture in file.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    var template = manager.GetTemplateBaseField(file, texture);
                    reader.Position = texture.GetAbsoluteByteOffset(file.file);
                    long flag = -1;
                    bool streaming = true, readable = true, ignore = false;
                    int mips = 0, dimension = 0;
                    long external = 0;
                    foreach (var child in template.Children)
                    {
                        if (child.Name == "m_StreamingMipmaps") flag = reader.Position;
                        var value = child.MakeValue(reader);
                        switch (child.Name)
                        {
                            case "m_MipCount": mips = value.AsInt; break;
                            case "m_IsReadable": readable = value.AsBool; break;
                            case "m_IgnoreMipmapLimit": ignore = value.AsBool; break;
                            case "m_StreamingMipmaps": streaming = value.AsBool; break;
                            case "m_TextureDimension": dimension = value.AsInt; break;
                            case "m_StreamData": external = value["size"].AsLong; break;
                        }
                        if (child.Name == "m_StreamData") break;
                    }
                    if (flag < 0 || streaming || readable || ignore || mips <= 1 || dimension != 2 || external <= 0) continue;
                    result.Add(dataStart + directories[index].Offset + flag);
                }
            }
            result.Sort();
            return result.ToArray();
        }

        #endregion

        /// <summary>
        /// Read-only, seekable view of a file that returns 1 at the given offsets. Only a byte that holds 0 is
        /// changed, so a stale offset can never write anything but the flag. No reads happen before Unity's.
        /// Unity may read from loading/streaming threads, so all access is serialized.
        /// </summary>
        internal sealed class FlagPatchStream : Stream
        {
            private readonly FileStream file;
            private readonly long[] flags;
            private readonly object gate = new object();
            private long position;

            internal FlagPatchStream(string path, long[] flags)
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
                this.flags = flags;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (gate)
                {
                    file.Position = position;
                    int read = file.Read(buffer, offset, count);
                    if (read > 0)
                    {
                        long end = position + read;
                        int first = Array.BinarySearch(flags, position);
                        if (first < 0) first = ~first;
                        for (int i = first; i < flags.Length && flags[i] < end; i++)
                        {
                            int at = offset + (int)(flags[i] - position);
                            if (buffer[at] == 0) buffer[at] = 1;
                        }
                        position = end;
                    }
                    return read;
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                lock (gate)
                {
                    long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : file.Length + offset;
                    if (target < 0) throw new IOException("seek before start of bundle");
                    return position = target;
                }
            }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => file.Length;
            public override long Position { get { lock (gate) return position; } set => Seek(value, SeekOrigin.Begin); }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) lock (gate) file.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
