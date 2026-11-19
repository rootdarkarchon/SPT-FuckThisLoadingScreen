using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine;

internal static class TextureQualityWithStreamingValidation
{
    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        Type controller = find("EFT.Settings.Graphics.GraphicsSettingsController");
        Type group = find("EFT.Settings.Graphics.GraphicsSettingsGroup");
        var bias = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), AccessTools.DeclaredMethod(group, "TextureQualityToMipBias", new[] { typeof(int) }));
        FieldInfo baseBudget = AccessTools.Field(controller, "MipStreamingMemoryBudget"), baseReduction = AccessTools.Field(controller, "StreamingMipmapsMaxLevelReduction");
        object oldBase = baseBudget.GetValue(null), oldBaseReduction = baseReduction.GetValue(null);
        int oldLimit = QualitySettings.globalTextureMipmapLimit, oldReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
        float oldBudget = QualitySettings.streamingMipmapsMemoryBudget;
        bool oldCameras = QualitySettings.streamingMipmapsAddAllCameras, oldDiscard = Texture.streamingTextureDiscardUnusedMips;
        var harmony = new Harmony("ftls.texture-quality-streaming.installed");
        bool enabled = true;
        try
        {
            TextureQualityWithStreaming.Install(harmony, find, controller.Module.ModuleVersionId, () => enabled);
            object instance = RuntimeHelpers.GetUninitializedObject(controller);
            void Apply(bool streaming, int quality) => TextureQualityWithStreaming.Change.Invoke(instance, new object[] { streaming, quality });

            Apply(true, 1);
            check(bias(1) > 0 && QualitySettings.globalTextureMipmapLimit == bias(1), "Streaming keeps the Medium texture-quality mipmap limit");
            Apply(true, 2);
            check(QualitySettings.globalTextureMipmapLimit == bias(2), "Highest texture quality keeps its native limit with streaming");
            enabled = false; Apply(true, 1);
            check(QualitySettings.globalTextureMipmapLimit == 0, "Disabled option retains EFT's forced streaming limit of 0");
            enabled = true; Apply(false, 0);
            check(QualitySettings.globalTextureMipmapLimit == bias(0), "Streaming off is untouched: EFT's own limit applies");
            try { TextureQualityWithStreaming.Install(harmony, find, Guid.Empty, () => true); check(false, "Unsupported module should fail"); }
            catch (NotSupportedException) { check(true, "Unsupported EFT identity refuses the texture-quality patch"); }
        }
        finally
        {
            harmony.UnpatchSelf(); TextureQualityWithStreaming.Enabled = null;
            QualitySettings.globalTextureMipmapLimit = oldLimit; QualitySettings.streamingMipmapsMaxLevelReduction = oldReduction;
            QualitySettings.streamingMipmapsMemoryBudget = oldBudget; QualitySettings.streamingMipmapsAddAllCameras = oldCameras;
            Texture.streamingTextureDiscardUnusedMips = oldDiscard;
            baseBudget.SetValue(null, oldBase); baseReduction.SetValue(null, oldBaseReduction);
        }
    }
}
