using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine;

internal static class MipBudgetAccountingValidation
{
    internal static void Run(Action<bool, string> check)
    {
        float selected = 2250, actual = 2250;
        ulong nonstream = 5307904441; // Captured 1.0.7 Customs nonstreaming residency.
        bool streaming = true; int owners = 0, writes = 0; double tick = 0;
        var policy = new MipBudgetAccounting(() => selected, () => actual, value => { actual = value; writes++; },
            () => nonstream, () => streaming, () => owners, () => tick);
        policy.NativePolicyWritten();
        check(actual == 7313 && selected == 2250 && policy.NonstreamingAllowance == 5063,
            "Captured Customs budget preserves 2250 MiB streaming allowance beyond rounded nonstreaming residency");
        policy.NativePolicyWritten(); policy.Pump();
        check(actual == 7313 && writes == 1, "Repeated refresh/poll neither compounds residency nor rewrites an unchanged budget");
        nonstream += 1048576; tick = 100; policy.Pump();
        check(actual == 7313, "Resident-memory polling is bounded to 500ms");
        tick = 500; policy.Pump();
        check(actual == 7314, "New resident nonstreaming memory updates the active world budget");
        nonstream = 400UL * 1048576; tick = 1000; policy.Pump();
        check(actual == 2650, "Unloaded nonstreaming memory reduces the accounted total again");
        owners = 2; actual = selected + 100; policy.NativePolicyWritten();
        check(actual == 2750 && policy.IconAllowance == 100, "Overlapping icon owners add one 100 MiB allowance");
        owners = 1; policy.IconsFinished(); check(actual == 2750, "First icon cleanup retains the last owner's allowance");
        selected = 1600; actual = 1700; policy.NativePolicyWritten();
        check(actual == 2100 && policy.BaseBudget == 1600, "Scene refresh during icons replaces the selected allowance without feedback");
        owners = 0; policy.IconsFinished(); check(actual == 2000 && policy.IconAllowance == 0, "Last icon cleanup retains world accounting and removes only the temporary allowance");
        streaming = false; tick = 1500; policy.Pump();
        check(actual == 1600 && policy.NonstreamingAllowance == 0 && policy.State == "streaming-off", "Disabled mip streaming restores the unadjusted native budget");
        streaming = true; tick = 2000; policy.Pump();
        check(actual == 2000, "Reenabled streaming resumes measured residency accounting");
        actual = 9000; tick = 2500; policy.Pump();
        check(actual == 9000 && policy.State == "external-override", "A newer unrelated budget write yields ownership");
        owners = 0; policy.IconsFinished(); nonstream += 1048576; tick = 3000; policy.Pump(); policy.Stop(); policy.Stop();
        check(actual == 9000, "Icon completion, polling and repeated shutdown preserve an external override");
        actual = selected; policy.NativePolicyWritten();
        check(actual == selected, "Stopped accounting cannot rearm through a late postfix");

        var restore = new MipBudgetAccounting(() => selected, () => actual, value => actual = value,
            () => nonstream, () => true, () => 0, () => tick);
        restore.NativePolicyWritten(); restore.Stop();
        check(actual == selected, "Owned shutdown restores the latest unadjusted selected budget");
        selected = float.NaN; var invalid = new MipBudgetAccounting(() => selected, () => actual, value => actual = value,
            () => nonstream, () => true, () => 0, () => tick);
        invalid.NativePolicyWritten();
        check(actual == 1600 && invalid.State == "invalid-base", "Invalid selected budgets cannot propagate a nonfinite corrected total");
        Override(check);
    }

    private static void Override(Action<bool, string> check)
    {
        float selected = 2250, actual = 2250;
        ulong nonstream = 400UL * 1048576;
        bool streaming = true;
        double tick = 0;
        int budgetOverride = 0;
        var policy = new MipBudgetAccounting(() => selected, () => actual, value => actual = value,
            () => nonstream, () => streaming, () => 0, () => tick, () => budgetOverride);
        policy.NativePolicyWritten();
        check(actual == 2650 && policy.EffectiveBudget == 2250 && policy.BaseBudget == 2250, "Override 0 leaves EFT's selected budget unchanged");
        budgetOverride = 1000; tick = 500; policy.Pump();
        check(actual == 1400 && policy.EffectiveBudget == 1000 && policy.BaseBudget == 2250, "An override below EFT's budget lowers the total while BaseBudget keeps reporting EFT's value");
        budgetOverride = 4000; tick = 1000; policy.Pump();
        check(actual == 4400 && policy.EffectiveBudget == 4000, "An override above EFT's budget raises the total");
        budgetOverride = 3000; tick = 1200; policy.Pump();
        check(actual == 4400, "A changed override waits for the next 500 ms poll");
        tick = 1500; policy.Pump();
        check(actual == 3400 && policy.EffectiveBudget == 3000, "A changed override is picked up on the next poll without a restart");
        budgetOverride = -5; tick = 2000; policy.Pump();
        check(actual == 2650 && policy.EffectiveBudget == 2250, "A negative override is treated as unset");
        budgetOverride = 3000; tick = 2500; policy.Pump();
        selected = 1600; actual = 1600; policy.NativePolicyWritten();
        check(actual == 3400 && policy.BaseBudget == 1600 && policy.EffectiveBudget == 3000, "A native map refresh keeps the override instead of adopting EFT's new value");
        policy.Stop();
        check(actual == 1600 && policy.State == "stopped", "Shutdown restores EFT's selected budget, not the override");

        selected = 2250; actual = 2250; streaming = false; budgetOverride = 3000;
        var off = new MipBudgetAccounting(() => selected, () => actual, value => actual = value,
            () => nonstream, () => streaming, () => 0, () => tick, () => budgetOverride);
        off.NativePolicyWritten();
        check(actual == 2250 && off.EffectiveBudget == 2250 && off.State == "streaming-off", "The override does nothing while mip streaming is off");

        selected = float.NaN; actual = 1700; streaming = true;
        var invalid = new MipBudgetAccounting(() => selected, () => actual, value => actual = value,
            () => nonstream, () => streaming, () => 0, () => tick, () => budgetOverride);
        invalid.NativePolicyWritten();
        check(actual == 1700 && invalid.State == "invalid-base", "An override cannot mask an invalid EFT base budget");
        check(MipBudgetAccounting.Header.Split(',').Length == 5 && MipBudgetAccounting.Header.EndsWith("mip_budget_effective_mb", StringComparison.Ordinal),
            "Snapshot header ends with the effective budget column");
    }

    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        var harmony = new Harmony("ftls.budget-accounting.installed");
        Type graphics = find("EFT.Settings.Graphics.GraphicsSettingsController");
        FieldInfo nativeBase = AccessTools.Field(graphics, "MipStreamingMemoryBudget"), nativeReduction = AccessTools.Field(graphics, "StreamingMipmapsMaxLevelReduction");
        float oldBase = (float)nativeBase.GetValue(null), oldBudget = QualitySettings.streamingMipmapsMemoryBudget;
        int oldBaseReduction = (int)nativeReduction.GetValue(null), oldReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
        bool oldStreaming = QualitySettings.streamingMipmapsActive, oldDiscard = Texture.streamingTextureDiscardUnusedMips, oldCameras = QualitySettings.streamingMipmapsAddAllCameras;
        IconMipLifetime.Scope first = null, second = null;
        try
        {
            QualitySettings.streamingMipmapsActive = true;
            Guid module = graphics.Module.ModuleVersionId;
            IconMipLifetime.Install(harmony, find, module);
            MipBudgetAccounting.Active = MipBudgetAccounting.Install(harmony, find, module);
            MethodInfo refresh = AccessTools.DeclaredMethod(graphics, "RefreshStreamingMemoryBudget");
            object instance = RuntimeHelpers.GetUninitializedObject(graphics);
            refresh.Invoke(instance, new object[] { new Vector4(500, 750, 1000, 1500), (int?)2 });
            var policy = MipBudgetAccounting.Active;
            check(policy.State == "active" && policy.BaseBudget == 1000 && (float)nativeBase.GetValue(null) == 1000 &&
                QualitySettings.streamingMipmapsMemoryBudget == 1000 + policy.NonstreamingAllowance,
                "Exact native graphics refresh applies accounting after its settings write without modifying the EFT base field");
            IconMipLifetime.Begin(out first); IconMipLifetime.Increase();
            IconMipLifetime.Begin(out second); IconMipLifetime.Increase(); IconMipLifetime.Current.Value = null;
            check(IconMipLifetime.ActiveBudgets == 2 && policy.IconAllowance == 100 &&
                QualitySettings.streamingMipmapsMemoryBudget == 1100 + policy.NonstreamingAllowance && QualitySettings.streamingMipmapsMaxLevelReduction == 4,
                "Installed icon adapter and accounting compose with one shared boost and the original mip-reduction policy");
            policy.NativePolicyWritten();
            check(QualitySettings.streamingMipmapsMemoryBudget == 1100 + policy.NonstreamingAllowance, "Known policy reapplication never adds the corrected budget to itself");
            IconMipLifetime.Release(first, false);
            check(policy.IconAllowance == 100, "First installed icon release preserves overlapping accounting");
            refresh.Invoke(instance, new object[] { new Vector4(700, 900, 1600, 1900), (int?)2 });
            check(policy.BaseBudget == 1600 && (float)nativeBase.GetValue(null) == 1600 &&
                QualitySettings.streamingMipmapsMemoryBudget == 1700 + policy.NonstreamingAllowance,
                "Installed native refresh during an icon batch adopts the new map budget exactly once");
            IconMipLifetime.Release(second, true);
            check(IconMipLifetime.ActiveBudgets == 0 && policy.IconAllowance == 0 &&
                QualitySettings.streamingMipmapsMemoryBudget == 1600 + policy.NonstreamingAllowance && QualitySettings.streamingMipmapsMaxLevelReduction == 2,
                "Failed final installed render removes icon boost, restores reduction and preserves corrected world budget");
            check(MipBudgetAccounting.Snapshot().Split(',').Length == 5 && MipBudgetAccounting.Snapshot().Contains("\"active\""),
                "Research exports base, residency, icon allowance and active accounting state");
            QualitySettings.streamingMipmapsMemoryBudget = 8500;
            policy.IconsFinished(); policy.Stop();
            check(QualitySettings.streamingMipmapsMemoryBudget == 8500 && policy.State == "stopped", "Actual Unity external budget write survives accounting cleanup");
            try { MipBudgetAccounting.Install(harmony, find, Guid.Empty); check(false, "Wrong accounting module must fail"); }
            catch (NotSupportedException) { check(true, "Wrong EFT module refuses budget accounting patches"); }
        }
        finally
        {
            IconMipLifetime.Current.Value = null;
            if (first != null) IconMipLifetime.Release(first, true);
            if (second != null) IconMipLifetime.Release(second, true);
            MipBudgetAccounting.Active?.Stop(); MipBudgetAccounting.Active = null; harmony.UnpatchSelf();
            nativeBase.SetValue(null, oldBase); nativeReduction.SetValue(null, oldBaseReduction);
            QualitySettings.streamingMipmapsMemoryBudget = oldBudget; QualitySettings.streamingMipmapsMaxLevelReduction = oldReduction;
            QualitySettings.streamingMipmapsActive = oldStreaming; Texture.streamingTextureDiscardUnusedMips = oldDiscard;
            QualitySettings.streamingMipmapsAddAllCameras = oldCameras;
        }
    }
}
