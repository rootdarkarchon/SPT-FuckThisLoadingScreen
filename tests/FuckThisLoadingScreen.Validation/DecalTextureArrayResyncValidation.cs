using System;
using System.Collections.Generic;
using System.Linq;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

internal static class DecalTextureArrayResyncValidation
{
    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        Type drawType = find("StaticDeferredDecalDrawInstance");
        Guid module = drawType.Module.ModuleVersionId;
        var harmony = new Harmony("ftls.decal-arrays.installed");
        bool oldStreaming = QualitySettings.streamingMipmapsActive;
        Texture2D streamed = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ftls-streaming-lifetime.png");
        var unstreamed = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "unstreamed decal" };
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/ftls-mip-probe.shader");
        var empty = new Material(shader) { name = "decal without texture" };
        var fixed_ = new Material(shader) { name = "unstreamed decal" };
        var puddle = new Material(shader) { name = "streamed puddle" };
        var arrays = new List<Texture2DArray>();
        var loaded = new Dictionary<Texture2D, int>();
        var copies = new List<(Texture2D Texture, Texture2DArray Array, int Slice)>();
        int id = Shader.PropertyToID("_Stream");
        try
        {
            check(streamed.streamingMipmaps && !unstreamed.streamingMipmaps, "Array fixture includes streamed and nonstreamed sources");
            fixed_.SetTexture(id, unstreamed); puddle.SetTexture(id, streamed);
            var fix = DecalTextureArrayResync.Install(harmony, find, module, null,
                texture => loaded.TryGetValue(texture, out int mip) ? mip : 0, (texture, array, slice) => copies.Add((texture, array, slice)));
            check(Harmony.GetPatchInfo(fix.Create).Postfixes.Any(p => p.owner == harmony.Id), "Resync binds the installed CreateTextureArray(ref Texture2DArray, int, int, TextureFormat, int)");
            object instance = Activator.CreateInstance(drawType);
            var materials = (SortedList<int, Material>)AccessTools.Field(drawType, "_materials").GetValue(instance);
            materials.Add(1, empty); materials.Add(2, fixed_); materials.Add(3, puddle);
            Texture2DArray NewArray() { var array = new Texture2DArray(64, 64, 3, TextureFormat.RGBA32, true); arrays.Add(array); return array; }
            QualitySettings.streamingMipmapsActive = true;

            Texture2DArray first = NewArray();
            streamed.requestedMipmapLevel = 3; loaded[streamed] = 2;
            fix.Created(instance, first, id);
            check(fix.PendingCount == 1 && streamed.requestedMipmapLevel == 0, "Streamed source copied before mip 0 is resident is deferred and requests mip 0");
            streamed.requestedMipmapLevel = 4; fix.Pump();
            check(copies.Count == 0 && fix.PendingCount == 1 && streamed.requestedMipmapLevel == 0, "Pending slice waits for mip 0 and reasserts the request over native tree writes");
            loaded[streamed] = 0; fix.Pump();
            check(copies.Count == 1 && copies[0].Texture == streamed && copies[0].Array == first && copies[0].Slice == 2 && fix.PendingCount == 0,
                "Loaded source is recopied into its native slice; materials without the texture still occupy a slice");
            fix.Pump();
            check(copies.Count == 1, "A resynced slice is copied exactly once");

            fix.Created(instance, NewArray(), id);
            check(fix.PendingCount == 0, "Source fully resident at array build needs no resync");
            loaded[streamed] = 2; QualitySettings.streamingMipmapsActive = false;
            fix.Created(instance, NewArray(), id);
            check(fix.PendingCount == 0, "Inactive mip streaming leaves native arrays alone");
            QualitySettings.streamingMipmapsActive = true;

            Texture2DArray replaced = NewArray();
            fix.Created(instance, replaced, id);
            UnityEngine.Object.DestroyImmediate(replaced); loaded[streamed] = 0; fix.Pump();
            check(fix.PendingCount == 0 && copies.Count == 1, "Destroyed or rebuilt arrays are dropped without copying");

            loaded[streamed] = 2; fix.Created(instance, NewArray(), id); fix.Stop();
            check(fix.PendingCount == 0, "Stop releases every pending slice");

            try { DecalTextureArrayResync.Install(harmony, find, Guid.Empty, null); check(false, "Unsupported module should fail"); }
            catch (NotSupportedException) { check(true, "Unsupported EFT identity refuses the resync"); }
        }
        finally
        {
            DecalTextureArrayResync.Active = null; harmony.UnpatchSelf();
            QualitySettings.streamingMipmapsActive = oldStreaming;
            if (streamed) streamed.ClearRequestedMipmapLevel();
            foreach (var array in arrays) if (array) UnityEngine.Object.DestroyImmediate(array);
            UnityEngine.Object.DestroyImmediate(empty); UnityEngine.Object.DestroyImmediate(fixed_); UnityEngine.Object.DestroyImmediate(puddle);
            UnityEngine.Object.DestroyImmediate(unstreamed);
        }
    }
}
