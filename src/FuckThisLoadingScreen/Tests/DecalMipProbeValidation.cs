using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

internal static class DecalMipProbeValidation
{
    internal static void Installed(Action<bool, string> check, Func<string, Type> find, string project)
    {
        var harmony = new Harmony("ftls.decal-probe.installed"); var sync = new Harmony("ftls.decal-probe.result-sync");
        string output = Path.Combine(project, "decal-mip-probe-validation", Guid.NewGuid().ToString("N"));
        var research = new HideoutResearch(output, "fixture", find, _ => { }, new List<HideoutResearchHook>());
        Type treeType = find("TextureQuadTree"), drawType = find("StaticDeferredDecalDrawInstance"), decalType = find("StaticDeferredDecal");
        FieldInfo raid = AccessTools.Field(find("EFT.InGameStatus"), "InRaid"), drop = AccessTools.Field(treeType, "DropResultsAfterFramesCount"), offset = AccessTools.Field(treeType, "MipOffset"), update = AccessTools.Field(treeType, "_updateParams");
        bool oldRaid = (bool)raid.GetValue(null), oldStreaming = QualitySettings.streamingMipmapsActive;
        int oldDrop = (int)drop.GetValue(null), oldOffset = (int)offset.GetValue(null); object oldUpdate = update.GetValue(null);
        var renderer = new GameObject("decal renderer fixture"); var cameraObject = new GameObject("camera, \"probe\"");
        var decalObject = new GameObject("floor, \"patch\""); decalObject.SetActive(false);
        var farObject = new GameObject("far patch"); farObject.SetActive(false); farObject.transform.position = new Vector3(200, 0, 0);
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ftls-streaming-lifetime.png");
        string oldName = texture.name; texture.name = "ground, \"atlas\"";
        var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/ftls-mip-probe.shader")) { name = "floor, material" };
        var rt = new RenderTexture(16, 16, 0) { name = "non-Texture2D binding" };
        DecalMipProbe.Context context = null;
        int initialRequested = 0;
        try
        {
            var camera = cameraObject.AddComponent<Camera>();
            var decal = decalObject.AddComponent(decalType); var farDecal = farObject.AddComponent(decalType);
            AccessTools.Field(decalType, "_material").SetValue(decal, material);
            AccessTools.Field(decalType, "_tint").SetValue(decal, new Color(.2f, .3f, .4f, .5f));
            AccessTools.Field(decalType, "LowerLeftPixel").SetValue(decal, new Vector2(4, 8));
            AccessTools.Field(decalType, "UpperRightPixel").SetValue(decal, new Vector2(32, 48));
            material.SetTexture("_Stream", texture); material.SetTexture("_Static", rt);
            object draw = RuntimeHelpers.GetUninitializedObject(drawType); AccessTools.Field(drawType, "_material").SetValue(draw, material);
            var decals = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(decalType)); decals.Add(decal); decals.Add(farDecal);
            Guid module = treeType.Module.ModuleVersionId;
            DecalResultSynchronization.Install(sync, find, module); DecalMipProbe.Install(harmony, find, module);
            HideoutResearch.Active = research;
            raid.SetValue(null, false);
            DecalMipProbe.CameraPrefix(renderer.transform, camera, out context);
            check(context == null, "Menu cameras do not start raid decal captures");
            DecalMipProbe.BuildPrefix(renderer.transform, out var build);
            DecalMipProbe.BuffersPostfix(draw, decals, new Bounds(Vector3.zero, Vector3.one * 5));
            DecalMipProbe.BuildFinalizer(build);
            raid.SetValue(null, true); QualitySettings.streamingMipmapsActive = true; drop.SetValue(null, 240); offset.SetValue(null, -1);
            DecalMipProbe.CameraPrefix(renderer.transform, camera, out context);
            check(context != null && context.Capture != null, "Raid decal camera starts a capture without icon or hideout activity");
            object tree = RuntimeHelpers.GetUninitializedObject(treeType);
            AccessTools.Field(treeType, "_texture").SetValue(tree, texture);
            AccessTools.Field(treeType, "_mipmapCount").SetValue(tree, texture.mipmapCount);
            AccessTools.Field(treeType, "updateParamsLock").SetValue(tree, new object());
            Type resultType = treeType.GetNestedType("ThreadResults"); object result = Activator.CreateInstance(resultType), stale = Activator.CreateInstance(resultType);
            AccessTools.Field(resultType, "updatedDuringFrame").SetValue(result, Time.frameCount);
            AccessTools.Field(resultType, "requestedMipMapLevel").SetValue(result, 1);
            AccessTools.Field(resultType, "pixelsHBuf").SetValue(result, 128f); AccessTools.Field(resultType, "pixelsVBuf").SetValue(result, 64f);
            AccessTools.Field(resultType, "closestDist").SetValue(result, 2f);
            AccessTools.Field(resultType, "updatedDuringFrame").SetValue(stale, Time.frameCount - 241);
            AccessTools.Field(resultType, "requestedMipMapLevel").SetValue(stale, 0);
            var dict = (IDictionary)Activator.CreateInstance(AccessTools.Field(treeType, "_results").FieldType);
            dict.Add(camera.GetInstanceID(), result); dict.Add(778899, stale); AccessTools.Field(treeType, "_results").SetValue(tree, dict);
            object parameters = Activator.CreateInstance(update.FieldType); AccessTools.Field(update.FieldType, "cameraInstanceID").SetValue(parameters, 778899);
            AccessTools.Field(update.FieldType, "frameIndex").SetValue(parameters, Time.frameCount - 3); update.SetValue(null, parameters);
            texture.requestedMipmapLevel = 3;
            initialRequested = texture.requestedMipmapLevel;
            var request = AccessTools.DeclaredMethod(treeType, "UpdateTextureRequestedMipMapLevel"); request.Invoke(tree, null);
            check(texture.requestedMipmapLevel == 0, "Observed native decal request still applies its original fresh result and mip offset");
            check(research.DecalMips.Select(tree) == null, "Repeated tree/camera callbacks are throttled to one sample per second");
            AccessTools.Field(resultType, "updatedDuringFrame").SetValue(result, Time.frameCount - 241);
            var sample = new DecalMipProbe.Sample { Context = context, Tree = tree, Texture = texture, Before = texture.requestedMipmapLevel };
            request.Invoke(tree, null); research.DecalMips.Observe(sample, null);
            int expiredRequested = Math.Min(texture.mipmapCount - 1, QualitySettings.streamingMipmapsMaxLevelReduction);
            check(texture.requestedMipmapLevel == expiredRequested, "Native expired visibility selects a coarse mip subject to Unity's max-reduction clamp");
            sync.UnpatchSelf(); research.DecalMips.Observe(sample, null);
            check(texture.requestedMipmapLevel == expiredRequested, "Unsynchronized result observation neither enumerates worker data nor changes mip requests");
            DecalMipProbe.CameraFinalizer(context); context = null;
            check(research.DecalMips.Select(tree) == null, "Camera finalizer clears request context even outside another render");
            check(Harmony.GetPatchInfo(request).Prefixes.Any(p => p.owner == harmony.Id) && Harmony.GetPatchInfo(request).Transpilers.All(p => p.owner != harmony.Id),
                "Decal mip probes use ordinary observers and leave native request IL unchanged");
            try { DecalMipProbe.Install(harmony, find, Guid.Empty); check(false, "Wrong probe module must fail"); }
            catch (NotSupportedException) { check(true, "Wrong EFT module refuses decal probe hooks"); }
            GC.KeepAlive(draw); GC.KeepAlive(decals);
        }
        finally
        {
            if (context != null) DecalMipProbe.CameraFinalizer(context);
            HideoutResearch.Active = null; research.Stop(); harmony.UnpatchSelf(); sync.UnpatchSelf();
            raid.SetValue(null, oldRaid); drop.SetValue(null, oldDrop); offset.SetValue(null, oldOffset); update.SetValue(null, oldUpdate);
            texture.name = oldName; texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel(); QualitySettings.streamingMipmapsActive = oldStreaming;
            UnityEngine.Object.DestroyImmediate(decalObject); UnityEngine.Object.DestroyImmediate(farObject); UnityEngine.Object.DestroyImmediate(renderer); UnityEngine.Object.DestroyImmediate(cameraObject);
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(rt);
        }
        string folder = Directory.GetDirectories(output).Single();
        var captureFile = Path.Combine(folder, "events.csv");
        for (int n = 0; n < 100 && !File.ReadAllText(captureFile).Contains("plugin-stopped"); n++) System.Threading.Thread.Sleep(20);
        var mipLines = File.ReadAllLines(Path.Combine(folder, "decal-mips.csv")); var header = Csv(mipLines[0]); var data = mipLines.Skip(1).Select(Csv).ToArray();
        string Get(string[] row, string field) => row[Array.IndexOf(header, field)];
        check(data.Length >= 5 && data.All(row => row.Length == header.Length), "Dedicated decal mip rows match their CSV schema");
        check(data.Any(row => Get(row, "requested_before") == initialRequested.ToString() && Get(row, "requested_mip") == "0" && Get(row, "result_fresh") == "true"),
            "Probe records before/after native mip selection with fresh visibility");
        check(data.Any(row => Get(row, "result_age_frames") == "241" && Get(row, "result_fresh") == "false" && Get(row, "result_camera_id") == "778899"),
            "Other-camera stale visibility is linked to its exact result frame and native expiry threshold");
        check(data.Any(row => Get(row, "result_state") == "unsynchronized-not-read") && data.All(row => Get(row, "probe_error") == ""),
            "Absent synchronization is explicit, with no unsafe dictionary read or observer errors");
        check(data.All(row => Get(row, "texture_name") == "ground, \"atlas\"" && Get(row, "camera_name") == "camera, \"probe\""), "Texture/camera names survive CSV quoting");
        var bindingLines = File.ReadAllLines(Path.Combine(folder, "decal-bindings.csv")); var bindingHeader = Csv(bindingLines[0]); var bindingData = bindingLines.Skip(1).Select(Csv).ToArray();
        string Binding(string[] row, string field) => row[Array.IndexOf(bindingHeader, field)];
        check(bindingData.Length == 2 && bindingData.All(row => row.Length == bindingHeader.Length), "Nearby decal binding rows are bounded, deduplicated and schema-aligned");
        check(bindingData.Any(row => Binding(row, "texture_type") == "RenderTexture" && Binding(row, "streamable") == "") &&
            bindingData.Any(row => Binding(row, "texture_type") == "Texture2D" && Binding(row, "streamable") == "true"), "Bindings distinguish streamable atlas textures from other texture types");
        check(bindingData.All(row => Binding(row, "texture_id") == Binding(row, "source_texture_id") && Binding(row, "texture_type") == Binding(row, "source_texture_type") &&
            Binding(row, "camera_name") == "camera, \"probe\""), "Bindings connect original and draw texture identities to the observing camera");
        check(bindingData.All(row => Binding(row, "path").Contains("floor, \"patch\"") && Binding(row, "lower_pixel_x") == "4" && Binding(row, "upper_pixel_y") == "48" && Binding(row, "tint_a").StartsWith("0.5")),
            "World path, atlas rectangle and decal tint identify the affected ground object");
        check(!File.ReadAllText(captureFile).Contains("decal-probe-error"), "Native decal observer fixture completes without research errors");
    }
    private static string[] Csv(string line)
    {
        var values = new List<string>(); var value = new System.Text.StringBuilder(); bool quoted = false;
        for (int n = 0; n < line.Length; n++)
        {
            char c = line[n]; if (c == '"') { if (quoted && n + 1 < line.Length && line[n + 1] == '"') { value.Append('"'); n++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { values.Add(value.ToString()); value.Clear(); } else value.Append(c);
        }
        values.Add(value.ToString()); return values.ToArray();
    }
}
