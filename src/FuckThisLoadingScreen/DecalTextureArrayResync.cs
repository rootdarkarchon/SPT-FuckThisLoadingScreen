using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // EFT copies each decal material texture into a shared Texture2DArray only when the draw
    // instance's material set changes. With mip streaming, a source copied before mip 0 is resident
    // leaves its slice incomplete for the rest of the raid. Request mip 0 for such sources and recopy
    // the slice with the native copy branches once it is loaded. The native array build is unchanged.
    internal sealed class DecalTextureArrayResync
    {
        internal static DecalTextureArrayResync Active;
        private sealed class Pending { internal Texture2DArray Array; internal Texture2D Texture; internal int Slice, LoadedAtCopy, Frame; }
        private readonly List<Pending> pending = new List<Pending>();
        private readonly FieldInfo materials;
        private readonly Func<Texture2D, int> loadedMip;
        private readonly Action<Texture2D, Texture2DArray, int> copy;
        private readonly Action<string> log;
        internal readonly MethodInfo Create;
        internal int PendingCount => pending.Count;

        private DecalTextureArrayResync(FieldInfo materials, MethodInfo create, Func<Texture2D, int> loadedMip, Action<Texture2D, Texture2DArray, int> copy, Action<string> log)
        { this.materials = materials; Create = create; this.loadedMip = loadedMip; this.copy = copy; this.log = log; }

        internal static DecalTextureArrayResync Install(Harmony harmony, Func<string, Type> find, Guid module, Action<string> log,
            Func<Texture2D, int> loadedMip = null, Action<Texture2D, Texture2DArray, int> copy = null)
        {
            Type draw = find("StaticDeferredDecalDrawInstance");
            if (draw.Module.ModuleVersionId != module) throw new NotSupportedException("Decal texture-array resync requires the supported EFT module");
            FieldInfo materials = AccessTools.Field(draw, "_materials");
            if (materials == null || materials.IsStatic || materials.FieldType != typeof(SortedList<int, Material>))
                throw new MissingFieldException(draw.FullName, "_materials (SortedList<int, Material>)");
            MethodInfo create = AccessTools.DeclaredMethod(draw, "CreateTextureArray",
                new[] { typeof(Texture2DArray).MakeByRefType(), typeof(int), typeof(int), typeof(TextureFormat), typeof(int) });
            if (create == null || create.IsStatic || create.ReturnType != typeof(void))
                throw new MissingMethodException(draw.FullName, "void CreateTextureArray(ref Texture2DArray, int, int, TextureFormat, int)");
            var fix = new DecalTextureArrayResync(materials, create, loadedMip ?? (texture => texture.loadedMipmapLevel), copy ?? NativeCopy, log);
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(DecalTextureArrayResync), nameof(Postfix)));
            return fix;
        }
        internal static void Postfix(object __instance, ref Texture2DArray __0, int __4)
        {
            try { Active?.Created(__instance, __0, __4); }
            catch (Exception error) { Active?.Log("Decal texture-array resync skipped one array build: " + error.GetType().Name + ": " + error.Message); }
        }
        internal void Created(object instance, Texture2DArray array, int textureId)
        {
            if (!array || !QualitySettings.streamingMipmapsActive) return;
            var list = (SortedList<int, Material>)materials.GetValue(instance);
            if (list == null) return;
            // Native slice order: one slice per material in key order, including materials without this texture.
            int slice = 0;
            foreach (Material material in list.Values)
            {
                if (material && material.GetTexture(textureId) is Texture2D texture && texture && texture.streamingMipmaps)
                {
                    int loaded = loadedMip(texture);
                    if (loaded != 0)
                    {
                        pending.Add(new Pending { Array = array, Texture = texture, Slice = slice, LoadedAtCopy = loaded, Frame = Time.frameCount });
                        texture.requestedMipmapLevel = 0;
                        Report("decals.texture-array.deferred", "Decal texture-array slice copied before mip 0 was resident: texture=" + texture.name + "; slice=" + slice +
                            "; loaded_mip=" + loaded + "; array_mips=" + array.mipmapCount + "; requesting mip 0 and recopying when loaded.");
                    }
                }
                slice++;
            }
        }
        internal void Pump()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending entry = pending[i];
                if (!entry.Array || !entry.Texture)
                {
                    // Native rebuilt or released the array, or the source was unloaded.
                    pending.RemoveAt(i); Release(entry.Texture);
                    continue;
                }
                if (loadedMip(entry.Texture) != 0)
                {
                    // The native texture quad tree also writes this request; keep mip 0 until the copy is complete.
                    if (entry.Texture.requestedMipmapLevel != 0) entry.Texture.requestedMipmapLevel = 0;
                    continue;
                }
                copy(entry.Texture, entry.Array, entry.Slice);
                pending.RemoveAt(i); Release(entry.Texture);
                Report("decals.texture-array.resynced", "Decal texture-array slice recopied at mip 0: texture=" + entry.Texture.name + "; slice=" + entry.Slice +
                    "; loaded_mip_at_build=" + entry.LoadedAtCopy + "; frames_waited=" + (Time.frameCount - entry.Frame) + ".");
            }
        }
        internal void Stop()
        {
            for (int i = pending.Count - 1; i >= 0; i--) { Texture2D texture = pending[i].Texture; pending.RemoveAt(i); Release(texture); }
        }
        // Return the source to native/camera-driven streaming once no slice waits for it.
        private void Release(Texture2D texture)
        {
            if (!texture) return;
            for (int i = 0; i < pending.Count; i++) if (ReferenceEquals(pending[i].Texture, texture)) return;
            texture.ClearRequestedMipmapLevel();
        }
        // Same branches as StaticDeferredDecalDrawInstance.CreateTextureArray/CopyMipsManual.
        internal static void NativeCopy(Texture2D texture, Texture2DArray array, int slice)
        {
            if (QualitySettings.globalTextureMipmapLimit == 0 && texture.mipmapCount == array.mipmapCount) { Graphics.CopyTexture(texture, 0, array, slice); return; }
            int mips = Math.Min(texture.mipmapCount, array.mipmapCount);
            for (int mip = 0; mip < mips; mip++)
                Graphics.CopyTexture(texture, 0, mip, 0, 0, texture.width >> mip, texture.height >> mip, array, slice, mip, 0, 0);
        }
        private void Report(string stage, string text)
        {
            Log(text);
            try { HideoutResearch.Active?.NoteDetail(stage, text); } catch { }
        }
        private void Log(string text) { try { log?.Invoke(text); } catch { } }
    }
}
