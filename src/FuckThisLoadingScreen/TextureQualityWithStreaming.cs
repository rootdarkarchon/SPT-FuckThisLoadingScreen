using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // EFT's ChangeTexturesQuality forces QualitySettings.globalTextureMipmapLimit to 0 whenever mip
    // streaming is on, so texture quality no longer reduces textures that cannot stream. With this
    // option, the limit EFT uses for streaming-off (TextureQualityToMipBias) also applies with streaming
    // on; streaming then manages the streamable textures below that limit.
    internal static class TextureQualityWithStreaming
    {
        private static Func<int, int> mipBias;
        internal static Func<bool> Enabled;
        internal static MethodInfo Change;
        internal static Action<string> Log;

        internal static void Install(Harmony harmony, Func<string, Type> find, Guid module, Func<bool> enabled)
        {
            Type controller = find("EFT.Settings.Graphics.GraphicsSettingsController"), group = find("EFT.Settings.Graphics.GraphicsSettingsGroup");
            if (controller.Module.ModuleVersionId != module) throw new NotSupportedException("Texture quality with streaming requires the supported EFT module");
            Change = AccessTools.DeclaredMethod(controller, "ChangeTexturesQuality", new[] { typeof(bool), typeof(int) });
            if (Change == null || Change.IsStatic || Change.ReturnType != typeof(void))
                throw new MissingMethodException(controller.FullName, "void ChangeTexturesQuality(bool, int)");
            MethodInfo bias = AccessTools.DeclaredMethod(group, "TextureQualityToMipBias", new[] { typeof(int) });
            if (bias == null || !bias.IsStatic || bias.ReturnType != typeof(int))
                throw new MissingMethodException(group.FullName, "static int TextureQualityToMipBias(int)");
            mipBias = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), bias);
            Enabled = enabled;
            harmony.Patch(Change, postfix: new HarmonyMethod(typeof(TextureQualityWithStreaming), nameof(Postfix)));
        }

        internal static void Postfix(bool __0, int __1)
        {
            try
            {
                // Streaming off: EFT already applied the same limit.
                if (!__0 || Enabled == null || !Enabled()) return;
                int limit = mipBias(__1);
                QualitySettings.globalTextureMipmapLimit = limit;
                Log?.Invoke("Texture quality " + __1 + " keeps mipmap limit " + limit + " with mip streaming.");
            }
            catch (Exception error) { Debug.LogWarning("[FTLS] Texture quality limit with streaming skipped: " + error.Message); }
        }
    }
}
