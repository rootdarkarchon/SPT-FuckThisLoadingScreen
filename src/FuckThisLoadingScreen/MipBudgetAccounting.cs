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
        private readonly Func<float> _selected, _read;
        private readonly Action<float> _write;
        private readonly Func<ulong> _nonstreaming;
        private readonly Func<bool> _streaming;
        private readonly Func<int> _icons;
        private readonly Func<double> _clock;
        private float _applied;
        private double _nextPoll;
        private bool _owns, _stopped;
        internal float BaseBudget { get; private set; }
        internal float NonstreamingAllowance { get; private set; }
        internal float IconAllowance { get; private set; }
        internal string State { get; private set; } = "unarmed";

        internal MipBudgetAccounting(Func<float> selected, Func<float> read, Action<float> write,
            Func<ulong> nonstreaming, Func<bool> streaming, Func<int> icons, Func<double> clock)
        {
            this._selected = selected; this._read = read; this._write = write;
            this._nonstreaming = nonstreaming; this._streaming = streaming; this._icons = icons; this._clock = clock;
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
            if (_stopped) return;
            _owns = true; _applied = _read(); Apply();
        }
        internal void Pump()
        {
            if (_stopped || _clock() < _nextPoll) return;
            _nextPoll = _clock() + 500; Apply();
        }
        internal void IconsFinished() { if (!_stopped) Apply(); }
        private void Apply()
        {
            if (!_owns) return;
            if (_read() != _applied) { _owns = false; State = "external-override"; return; }
            BaseBudget = _selected(); IconAllowance = _icons() > 0 ? 100f : 0f;
            if (float.IsNaN(BaseBudget) || float.IsInfinity(BaseBudget) || BaseBudget < 0)
            { _owns = false; State = "invalid-base"; return; }
            bool enabled = _streaming();
            NonstreamingAllowance = enabled ? (float)Math.Ceiling(_nonstreaming() / 1048576.0) : 0f;
            float total = BaseBudget + NonstreamingAllowance + IconAllowance;
            if (float.IsInfinity(total)) { _owns = false; State = "invalid-total"; return; }
            if (total != _applied) _write(total);
            _applied = total; State = enabled ? "active" : "streaming-off";
        }
        internal void Stop()
        {
            if (_stopped) return;
            if (_owns && _read() == _applied) _write(BaseBudget + (_icons() > 0 ? 100f : 0f));
            _owns = false; _stopped = true; State = "stopped";
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
