using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Profiling;

namespace FuckThisLoadingScreen
{
    // RESEARCH: one-shot, on-demand inventory of every loaded texture, to explain how texture
    // quality, mipmap limits and streaming change resident texture memory. Observation only; a full
    // scan is acceptable because it runs once per explicit request, never per frame.
    internal static class TextureInventory
    {
        internal const string Header = "type,name,instance_id,width,height,depth,mip_count,graphics_format,readable,streaming,requested_mip,loaded_mip,ignore_limit,limit_group,active_limit,first_resident_mip,estimated_bytes,runtime_bytes";

        /// <summary>Write texture rows and a grouped summary; returns the one-line summary.</summary>
        internal static string Dump(string directory, string label)
        {
            Directory.CreateDirectory(directory);
            string prefix = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + label);
            var totals = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
            long all = 0;
            int count = 0;
            using (var csv = new StreamWriter(prefix + "-textures.csv", false, new UTF8Encoding(false)))
            {
                csv.WriteLine(Header);
                foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>())
                {
                    if (!texture)
                        continue;
                    var row = Describe(texture, out long bytes, out string key);
                    csv.WriteLine(row);
                    if (!totals.TryGetValue(key, out long[] total))
                        totals[key] = total = new long[2];
                    total[0]++;
                    total[1] += bytes;
                    all += bytes;
                    count++;
                }
            }
            var summary = new StringBuilder();
            summary.AppendLine("FTLS texture inventory: " + label);
            summary.AppendLine("globalTextureMipmapLimit=" + QualitySettings.globalTextureMipmapLimit + "; streamingMipmapsActive=" + QualitySettings.streamingMipmapsActive +
                "; streamingBudgetMB=" + QualitySettings.streamingMipmapsMemoryBudget + "; maxLevelReduction=" + QualitySettings.streamingMipmapsMaxLevelReduction);
            summary.AppendLine("Unity counters (bytes): current=" + Texture.currentTextureMemory + "; desired=" + Texture.desiredTextureMemory + "; total=" + Texture.totalTextureMemory +
                "; nonStreaming=" + Texture.nonStreamingTextureMemory + "; graphicsMemorySizeMB=" + SystemInfo.graphicsMemorySize);
            foreach (string group in TextureMipmapLimitGroups.GetGroups())
            {
                TextureMipmapLimitSettings settings = QualitySettings.GetTextureMipmapLimitSettings(group);
                summary.AppendLine("limit group " + group + ": mode=" + settings.limitBiasMode + "; bias=" + settings.limitBias);
            }
            summary.AppendLine("estimated_bytes = format size of mips from first_resident_mip (texture arrays x slices, cubemaps x6, render targets x MSAA plus depth). Estimates, not driver residency.");
            summary.AppendLine("key(type|streaming|ignore_limit|limit_group|active_limit|first_resident_mip),textures,estimated_MiB");
            foreach (var entry in totals)
                summary.AppendLine(entry.Key + "," + entry.Value[0] + "," + (entry.Value[1] / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture));
            File.WriteAllText(prefix + "-summary.txt", summary.ToString());
            return "Texture inventory written: " + prefix + "-*.txt/csv; textures=" + count + "; estimated=" + (all / 1048576.0).ToString("0", CultureInfo.InvariantCulture) +
                " MiB; global limit=" + QualitySettings.globalTextureMipmapLimit + "; streaming=" + QualitySettings.streamingMipmapsActive;
        }

        internal static string Describe(Texture texture, out long bytes, out string key)
        {
            int width = texture.width, height = texture.height, mips = Math.Max(1, texture.mipmapCount), depth = 1, faces = 1, samples = 1;
            GraphicsFormat format = texture.graphicsFormat;
            string streaming = "", requested = "", loaded = "", ignore = "", group = "", active = "";
            int first = 0;
            long extra = 0;
            switch (texture)
            {
                case Texture2D two:
                    ignore = B(two.ignoreMipmapLimit);
                    group = two.mipmapLimitGroup ?? "";
                    active = N(two.activeMipmapLimit);
                    first = Math.Max(0, two.activeMipmapLimit);
                    if (two.streamingMipmaps)
                    {
                        streaming = "true";
                        requested = N(two.requestedMipmapLevel);
                        loaded = N(two.loadedMipmapLevel);
                        if (QualitySettings.streamingMipmapsActive)
                            first = Math.Max(first, two.loadedMipmapLevel);
                    }
                    else
                        streaming = "false";
                    break;
                case Texture2DArray array:
                    depth = array.depth;
                    break;
                case Cubemap _:
                    faces = 6;
                    break;
                case CubemapArray cubes:
                    faces = 6 * cubes.cubemapCount;
                    break;
                case Texture3D volume:
                    depth = volume.depth;
                    break;
                case RenderTexture target:
                    depth = Math.Max(1, target.volumeDepth);
                    samples = Math.Max(1, target.antiAliasing);
                    if (target.dimension == UnityEngine.Rendering.TextureDimension.Cube)
                        faces = 6;
                    // Depth/stencil buffer: bits per pixel x samples.
                    extra = (long)width * height * target.depth / 8 * samples;
                    break;
            }
            bytes = extra;
            if (format != GraphicsFormat.None)
            {
                bool volume = texture is Texture3D;
                for (int mip = Math.Min(first, mips - 1); mip < mips; mip++)
                {
                    int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
                    long size = volume ? (long)GraphicsFormatUtility.ComputeMipmapSize(w, h, Math.Max(1, depth >> mip), format)
                        : (long)GraphicsFormatUtility.ComputeMipmapSize(w, h, format) * depth;
                    bytes += size * faces * samples;
                }
            }
            long runtime = 0;
            try
            { runtime = Profiler.GetRuntimeMemorySizeLong(texture); }
            catch { }
            string type = texture.GetType().Name;
            key = type + "|" + streaming + "|" + ignore + "|" + group + "|" + active + "|" + N(first);
            return string.Join(",", Q(type), Q(texture.name), N(texture.GetInstanceID()), N(width), N(height), N(depth * faces), N(mips), Q(format.ToString()),
                B(texture.isReadable), streaming, requested, loaded, ignore, Q(group), active, N(first), N(bytes), N(runtime));
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string B(bool value) => value ? "true" : "false";
        private static string Q(string value) => HideoutResearchCapture.Quote(value);
    }
}
