using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // An opt-in A/B candidate, not a measured performance fix. Unity still enforces
    // the selected streaming memory budget when unused mips are cached.
    internal sealed class MipResidencyPolicy
    {
        internal static MipResidencyPolicy Active;
        private bool _previous, _owns;
        internal static MipResidencyPolicy Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type graphics = find("EFT.Settings.Graphics.GraphicsSettingsController");
            if (graphics.Module.ModuleVersionId != module)
                throw new NotSupportedException("Mip residency policy requires the supported EFT module");
            var method = AccessTools.DeclaredMethod(graphics, "RefreshStreamingMemoryBudget");
            if (method?.ReturnType != typeof(void) || method.IsStatic)
                throw new MissingMethodException("Mip memory policy ABI");
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(MipResidencyPolicy), nameof(Guard)),
                postfix: new HarmonyMethod(typeof(MipResidencyPolicy), nameof(Refreshed)));
            var policy = new MipResidencyPolicy();
            policy.Apply();
            return policy;
        }
        internal void Apply()
        { _previous = Texture.streamingTextureDiscardUnusedMips; Texture.streamingTextureDiscardUnusedMips = false; _owns = true; }
        internal void Stop()
        { if (_owns && !Texture.streamingTextureDiscardUnusedMips) Texture.streamingTextureDiscardUnusedMips = _previous; _owns = false; }
        internal static void Refreshed() => Active?.Apply();
        internal static IEnumerable<CodeInstruction> Guard(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            if (code.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(Texture) && m.Name == "set_streamingTextureDiscardUnusedMips") != 1)
                throw new MissingMethodException("Mip discard policy setter changed");
            return code;
        }
    }
}
