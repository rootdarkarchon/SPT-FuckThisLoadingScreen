using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Installed-ABI checks for icon render recovery: destroyed textures during the mip wait, empty cache entries
/// left by renders without a sprite, and the native capture flag after a throwing capture.
/// </summary>
internal static class IconRenderRecoveryValidation
{
    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        var harmony = new Harmony("ftls.icon-recovery.native-fixture");
        var oldContext = SynchronizationContext.Current; var pump = new IconMipProbeValidation.Pump(); SynchronizationContext.SetSynchronizationContext(pump);
        var warnings = new List<string>(); Action<string> oldWarning = IconMipLifetime.Warning; IconMipLifetime.Warning = warnings.Add;
        var model = new GameObject("FTLS recovery, model"); Material material = null;
        try
        {
            IconMipLifetime.Install(harmony, find, find("ItemIconCreator").Module.ModuleVersionId);
            Shader shader = Shader.Find("Unlit/Texture");
            check(shader != null, "Recovery fixture has a textured built-in shader");
            material = new Material(shader);
            model.AddComponent<MeshRenderer>().sharedMaterial = material;
            DestroyedTextures(check, find, pump, material, model);
            EmptyIcons(check, find);
            CaptureFlag(check, find, model);
            TimingSummary(check);
            MipmapLimit(check, find, pump, material, model);
            check(warnings.Count == 0, "Recovery paths log no cleanup warnings: " + string.Join(" | ", warnings));
        }
        finally
        {
            IconMipLifetime.Current.Value = null; IconMipLifetime.Warning = oldWarning; harmony.UnpatchSelf();
            SynchronizationContext.SetSynchronizationContext(oldContext);
            if (material) UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(model);
        }
    }

    private static void DestroyedTextures(Action<bool, string> check, Func<string, Type> find, IconMipProbeValidation.Pump pump, Material material, GameObject model)
    {
        Type graphics = find("IconCreatorGraphicsSettings");
        foreach (string name in new[] { "RequestMipZeroFast", "RequestMipZero" })
        {
            var texture = new Texture2D(4, 4);
            material.mainTexture = texture;
            IconMipLifetime.Begin(out var scope);
            var request = (Task<bool>)AccessTools.DeclaredMethod(graphics, name).Invoke(RuntimeHelpers.GetUninitializedObject(graphics), new object[] { model });
            IconMipLifetime.Current.Value = null;
            // Player requests yield before their first check, so the texture dies while the request waits.
            UnityEngine.Object.DestroyImmediate(texture);
            pump.Until(request);
            check(request.Status == TaskStatus.RanToCompletion && request.Result, "A texture destroyed during the mip wait completes the request instead of faulting the render: " + name);
            IconMipLifetime.Release(scope, false);
        }
        material.mainTexture = null;
    }

    private static void EmptyIcons(Action<bool, string> check, Func<string, Type> find)
    {
        Type creatorType = find("ItemIconCreator"), iconType = find("ItemIcon");
        object creator = RuntimeHelpers.GetUninitializedObject(creatorType);
        var cache = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(int), iconType));
        AccessTools.Field(creatorType, "_memoryCacheIndex").SetValue(creator, cache);
        PropertyInfo sprite = AccessTools.Property(iconType, "Sprite");
        var spriteTexture = new Texture2D(4, 4);
        Sprite live = Sprite.Create(spriteTexture, new Rect(0, 0, 4, 4), Vector2.zero);
        try
        {
            object Icon(int hash) => Activator.CreateInstance(iconType, hash);
            bool Ends(object icon, bool failed, bool noSprite, object cached)
            {
                cache[7] = cached;
                IconMipLifetime.Begin(out var scope);
                IconMipLifetime.Current.Value = null;
                scope.ItemRender = true; scope.Creator = creator; scope.FillIcon = icon; scope.IconHash = 7; scope.NoSprite = noSprite;
                IconMipLifetime.Release(scope, failed);
                return cache.Contains(7);
            }
            object failedIcon = Icon(7);
            check(!Ends(failedIcon, true, false, failedIcon), "A failed render drops its empty cache entry so the icon renders again");
            object emptyIcon = Icon(7);
            check(!Ends(emptyIcon, false, true, emptyIcon), "A render that returns no sprite drops its empty cache entry");
            object filledIcon = Icon(7); sprite.SetValue(filledIcon, live);
            check(Ends(filledIcon, false, false, filledIcon), "A successful render keeps its cache entry");
            object replacedIcon = Icon(7), newer = Icon(7);
            check(Ends(replacedIcon, true, false, newer) && ReferenceEquals(cache[7], newer), "A failed render never drops a newer icon cached under the same hash");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(live);
            UnityEngine.Object.DestroyImmediate(spriteTexture);
        }
    }

    private static void CaptureFlag(Action<bool, string> check, Func<string, Type> find, GameObject model)
    {
        Type creatorType = find("ItemIconCreator");
        object creator = RuntimeHelpers.GetUninitializedObject(creatorType);
        FieldInfo busy = AccessTools.Field(creatorType, "_isIconCreating");
        MethodInfo capture = AccessTools.Method(creatorType, "CaptureSpriteOfModel");
        object size = Activator.CreateInstance(capture.GetParameters()[1].ParameterType.GetElementType());
        var ambient = (RenderSettings.ambientMode, RenderSettings.ambientLight, RenderSettings.ambientIntensity, RenderSettings.ambientSkyColor,
            RenderSettings.ambientEquatorColor, RenderSettings.ambientGroundColor, RenderSettings.fog);
        try
        {
            bool Throws(object fillIcon)
            {
                busy.SetValue(creator, true);
                IconMipLifetime.Begin(out var scope);
                scope.ItemRender = true; scope.Creator = creator; scope.FillIcon = fillIcon;
                try
                {
                    // The uninitialized creator has no shader replacer, so the native capture throws after its model check.
                    capture.Invoke(creator, new[] { model, size, null });
                    return false;
                }
                catch (TargetInvocationException error) when (error.InnerException is NullReferenceException) { return true; }
                finally
                {
                    IconMipLifetime.Current.Value = null;
                    // This creator has no icon cache; cache eviction is covered by EmptyIcons.
                    scope.FillIcon = null;
                    IconMipLifetime.Release(scope, true);
                }
            }
            check(Throws(new object()) && !(bool)busy.GetValue(creator), "A throwing FillIconWithNewSpriteAsync capture releases the native capture flag and still throws");
            check(Throws(null) && (bool)busy.GetValue(creator), "A throwing capture outside a cache fill leaves the capture flag to its owner");
        }
        finally
        {
            (RenderSettings.ambientMode, RenderSettings.ambientLight, RenderSettings.ambientIntensity, RenderSettings.ambientSkyColor,
                RenderSettings.ambientEquatorColor, RenderSettings.ambientGroundColor, RenderSettings.fog) = ambient;
        }
    }

    private static void MipmapLimit(Action<bool, string> check, Func<string, Type> find, IconMipProbeValidation.Pump pump, Material material, GameObject model)
    {
        int oldLimit = QualitySettings.globalTextureMipmapLimit; bool oldStreaming = QualitySettings.streamingMipmapsActive;
        Texture2D texture = MipStreamingValidation.CreateStreamingTexture("Assets/ftls-icon-limit.png", 256);
        try
        {
            QualitySettings.streamingMipmapsActive = true;
            texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel();
            foreach (int limit in new[] { 0, 1 })
            {
                QualitySettings.globalTextureMipmapLimit = limit;
                check(IconMipLifetime.TopMip(texture) == limit, "Icon requests target the highest mip allowed by mipmap limit " + limit + ": active=" + texture.activeMipmapLimit);
                texture.requestedMipmapLevel = 0;
                bool zeroLoaded = texture.IsRequestedMipmapLevelLoaded();
                texture.requestedMipmapLevel = limit;
                Debug.Log("FTLS-ICON-LIMIT limit=" + limit + " active=" + texture.activeMipmapLimit + " loaded=" + texture.loadedMipmapLevel + " request0Loaded=" + zeroLoaded + " requestTopLoaded=" + texture.IsRequestedMipmapLevelLoaded());
                texture.ClearRequestedMipmapLevel();

                material.mainTexture = texture;
                Type item = find("ItemIconCreator");
                IconMipLifetime.Begin(out var scope);
                var request = (Task<bool>)AccessTools.DeclaredMethod(item, "RequestMipZero").Invoke(RuntimeHelpers.GetUninitializedObject(item), new object[] { model });
                int requested = texture.requestedMipmapLevel, minimum = texture.minimumMipmapLevel;
                IconMipLifetime.Current.Value = null;
                pump.Until(request);
                IconMipLifetime.Release(scope, false);
                check(requested == limit && minimum == limit, "Item icon request writes the top allowed mip (" + limit + "), not an unloadable mip 0: requested=" + requested + " minimum=" + minimum);
                check(request.Status == TaskStatus.RanToCompletion && texture.requestedMipmapLevel < 0 && texture.minimumMipmapLevel == IconMipLifetime.ClearedMinimumMip,
                    "Top-mip icon request completes and restores automatic mip state at limit " + limit);
            }
        }
        finally
        {
            material.mainTexture = null;
            QualitySettings.globalTextureMipmapLimit = oldLimit; QualitySettings.streamingMipmapsActive = oldStreaming;
            texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel();
        }
    }

    private static void TimingSummary(Action<bool, string> check)
    {
        var lines = new List<string>();
        Action<string> oldTiming = IconMipLifetime.TimingLog; Func<bool> oldEnabled = IconMipLifetime.DebugEnabled;
        IconMipLifetime.TimingLog = lines.Add;
        try
        {
            IconMipLifetime.DebugEnabled = () => false;
            IconMipLifetime.Begin(out var ignored); IconMipLifetime.Current.Value = null; ignored.ItemRender = true;
            IconMipLifetime.Release(ignored, false);
            AccessTools.Field(typeof(IconMipLifetime), "_nextWatch").SetValue(null, 0L); IconMipLifetime.Pump();
            check(lines.Count == 0, "Render timings are not collected while debug logging is off");

            IconMipLifetime.DebugEnabled = () => true;
            for (int n = 0; n < 2; n++)
            {
                IconMipLifetime.Begin(out var scope); IconMipLifetime.Current.Value = null;
                scope.ItemRender = true; scope.MipRequested = true; scope.MipFrames = 3 + n; scope.MipTextures = 4; scope.MipsLoaded = n == 0;
                IconMipLifetime.Release(scope, n == 1);
            }
            AccessTools.Field(typeof(IconMipLifetime), "_nextWatch").SetValue(null, 0L); IconMipLifetime.Pump();
            check(lines.Count == 1 && lines[0].StartsWith("Icon render timing: 2 item renders (1 failed, 0 without sprite)") &&
                lines[0].Contains("mip wait frames mean 3.5, max 4, textures mean 4.0, mips not loaded 1") && lines[0].Contains("upload slice ") && lines[0].Contains("slowest: "),
                "Drained item renders produce one timing summary with stage, mip-wait and upload figures: " + string.Join(" | ", lines));
            AccessTools.Field(typeof(IconMipLifetime), "_nextWatch").SetValue(null, 0L); IconMipLifetime.Pump();
            check(lines.Count == 1, "A written timing summary is not repeated");
        }
        finally { IconMipLifetime.TimingLog = oldTiming; IconMipLifetime.DebugEnabled = oldEnabled; }
    }
}
