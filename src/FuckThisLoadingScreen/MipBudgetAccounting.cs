using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // EFT selects a fixed map/quality allowance. Unity's budget also charges
    // nonstreaming textures, which cannot be evicted by mip streaming. Keep that
    // measured residency outside the selected streaming allowance. Never put the
    // corrected total back into EFT's base field (that would compound on refresh).
    internal sealed class MipBudgetAccounting
    {
        internal static MipBudgetAccounting Active;
        internal const string Header = "mip_budget_base_mb,mip_budget_nonstream_mb,mip_budget_icon_mb,mip_budget_accounting";
        private readonly Func<float> selected, read;
        private readonly Action<float> write;
        private readonly Func<ulong> nonstreaming;
        private readonly Func<bool> streaming;
        private readonly Func<int> icons;
        private readonly Func<double> clock;
        private float applied;
        private double nextPoll;
        private bool owns, stopped;
        internal float BaseBudget { get; private set; }
        internal float NonstreamingAllowance { get; private set; }
        internal float IconAllowance { get; private set; }
        internal string State { get; private set; } = "unarmed";

        internal MipBudgetAccounting(Func<float> selected, Func<float> read, Action<float> write,
            Func<ulong> nonstreaming, Func<bool> streaming, Func<int> icons, Func<double> clock)
        {
            this.selected = selected; this.read = read; this.write = write;
            this.nonstreaming = nonstreaming; this.streaming = streaming; this.icons = icons; this.clock = clock;
        }
        internal static MipBudgetAccounting Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type graphics = find("EFT.Settings.Graphics.GraphicsSettingsController");
            if (graphics.Module.ModuleVersionId != module) throw new NotSupportedException("Mip budget accounting requires the supported EFT module");
            FieldInfo field = AccessTools.Field(graphics, "MipStreamingMemoryBudget");
            MethodInfo refresh = AccessTools.DeclaredMethod(graphics, "RefreshStreamingMemoryBudget");
            if (field?.FieldType != typeof(float) || !field.IsStatic || refresh?.ReturnType != typeof(void) || refresh.IsStatic ||
                refresh.GetParameters().Length != 2 || refresh.GetParameters()[0].ParameterType != typeof(Vector4) || refresh.GetParameters()[1].ParameterType != typeof(int?))
                throw new MissingMethodException("Mip budget accounting ABI");
            harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(MipBudgetAccounting), nameof(Refreshed)) { priority = Priority.Last });
            var policy = new MipBudgetAccounting(() => (float)field.GetValue(null), () => QualitySettings.streamingMipmapsMemoryBudget,
                value => QualitySettings.streamingMipmapsMemoryBudget = value, () => Texture.nonStreamingTextureMemory,
                () => QualitySettings.streamingMipmapsActive, () => IconMipLifetime.ActiveBudgets, () => HideoutResearchCapture.Now);
            policy.NativePolicyWritten(); return policy;
        }
        internal static void Refreshed() => Active?.NativePolicyWritten();

        // Only call immediately after a known native/adapted policy write. A fresh
        // scene/settings selection may intentionally replace a previous override.
        internal void NativePolicyWritten()
        {
            if (stopped) return;
            owns = true; applied = read(); Apply();
        }
        internal void Pump()
        {
            if (stopped || clock() < nextPoll) return;
            nextPoll = clock() + 500; Apply();
        }
        internal void IconsFinished() { if (!stopped) Apply(); }
        private void Apply()
        {
            if (!owns) return;
            if (read() != applied) { owns = false; State = "external-override"; return; }
            BaseBudget = selected(); IconAllowance = icons() > 0 ? 100f : 0f;
            if (float.IsNaN(BaseBudget) || float.IsInfinity(BaseBudget) || BaseBudget < 0)
            { owns = false; State = "invalid-base"; return; }
            bool enabled = streaming();
            NonstreamingAllowance = enabled ? (float)Math.Ceiling(nonstreaming() / 1048576.0) : 0f;
            float total = BaseBudget + NonstreamingAllowance + IconAllowance;
            if (float.IsInfinity(total)) { owns = false; State = "invalid-total"; return; }
            if (total != applied) write(total);
            applied = total; State = enabled ? "active" : "streaming-off";
        }
        internal void Stop()
        {
            if (stopped) return;
            if (owns && read() == applied) write(BaseBudget + (icons() > 0 ? 100f : 0f));
            owns = false; stopped = true; State = "stopped";
        }
        internal static string Snapshot()
        {
            var policy = Active;
            return policy == null ? ",,,\"disabled\"" : HideoutResearchCapture.Number(policy.BaseBudget) + "," +
                HideoutResearchCapture.Number(policy.NonstreamingAllowance) + "," + HideoutResearchCapture.Number(policy.IconAllowance) + "," +
                HideoutResearchCapture.Quote(policy.State);
        }
    }
}
