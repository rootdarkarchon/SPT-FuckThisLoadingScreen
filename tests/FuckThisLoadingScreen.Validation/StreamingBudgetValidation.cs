using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using FuckThisLoadingScreen;
using HarmonyLib;

/// <summary>Streaming budget estimate: pure math, the 60 s sampling session, and the installed EFT/Unity bindings.</summary>
internal static class StreamingBudgetValidation
{
    private const ulong Mib = 1048576;

    internal static void Run(Action<bool, string> check)
    {
        Arithmetic(check);
        Peaks(check);
        Session(check);
    }

    private static StreamingBudgetSample Sample(ulong desired, ulong nonStreaming, ulong current, ulong total, ulong budget, ulong usage) =>
        new StreamingBudgetSample(desired * Mib, nonStreaming * Mib, current * Mib, total * Mib, budget * Mib, usage * Mib);

    private static void Arithmetic(Action<bool, string> check)
    {
        check(StreamingBudgetMath.MarginMiB(4000) == 512 && StreamingBudgetMath.MarginMiB(5120) == 512 && System.Math.Abs(StreamingBudgetMath.MarginMiB(8192) - 819.2) < 1e-9,
            "Margin is 512 MiB until 10 % of the VRAM budget exceeds it");
        check(System.Math.Abs(StreamingBudgetMath.HeadroomMiB(8192, 2000, 1500) - 3872.8) < 1e-9 && StreamingBudgetMath.HeadroomMiB(1000, 800, 200) == 1000 - 800 - 200 - 512,
            "Headroom subtracts non-texture VRAM, nonstreaming textures and the margin");

        check(StreamingBudgetMath.RecommendMiB(3872.8) == 3840, "Recommendation rounds the headroom down to 64 MiB");
        check(StreamingBudgetMath.RecommendMiB(4096) == 4096 && StreamingBudgetMath.RecommendMiB(4095.99) == 4032, "An exact multiple of 64 stays; just below it rounds down");
        check(StreamingBudgetMath.RecommendMiB(255.9) == 256 && StreamingBudgetMath.RecommendMiB(0) == 256 && StreamingBudgetMath.RecommendMiB(-500) == 256 && StreamingBudgetMath.RecommendMiB(256) == 256,
            "Recommendation never drops below 256 MiB");
        check(StreamingBudgetMath.RecommendMiB(319.9) == 256 && StreamingBudgetMath.RecommendMiB(320) == 320, "Rounding happens after the lower clamp boundary");
        check(StreamingBudgetMath.RecommendMiB(65536) == 65536 && StreamingBudgetMath.RecommendMiB(70000) == 65536 && StreamingBudgetMath.RecommendMiB(double.PositiveInfinity) == 65536,
            "Recommendation never exceeds 65536 MiB");
        check(StreamingBudgetMath.RecommendMiB(double.NaN) == 256, "A NaN headroom falls back to the minimum");

        check(StreamingBudgetMath.Classify(2250, 2250, 3000) == StreamingBudgetVerdict.NeverLimits, "Demand equal to the map budget never limits textures");
        check(StreamingBudgetMath.Classify(2250.01, 2250, 3000) == StreamingBudgetVerdict.Raise, "Demand just above the map budget with room to raise is budget-limited");
        check(StreamingBudgetMath.Classify(5000, 3000, 3000) == StreamingBudgetVerdict.NeverLimits, "Without headroom above the map budget there is nothing to raise");
        check(StreamingBudgetMath.Classify(1000, 3000.01, 3000) == StreamingBudgetVerdict.Lower && StreamingBudgetMath.Classify(9000, 3000.01, 3000) == StreamingBudgetVerdict.Lower,
            "A map budget above the headroom is lowered whatever the demand");
        check(StreamingBudgetMath.Classify(1000, 2250, 3000) == StreamingBudgetVerdict.NeverLimits, "Low demand under a fitting budget never limits textures");
        check(StreamingBudgetMath.Describe(StreamingBudgetVerdict.Lower).StartsWith("EFT's budget exceeds what this GPU can hold", StringComparison.Ordinal) &&
            StreamingBudgetMath.Describe(StreamingBudgetVerdict.Raise).StartsWith("Textures were budget-limited", StringComparison.Ordinal) &&
            StreamingBudgetMath.Describe(StreamingBudgetVerdict.NeverLimits).StartsWith("Budget never limited textures here", StringComparison.Ordinal),
            "Each verdict has its own text");
    }

    private static void Peaks(Action<bool, string> check)
    {
        var peaks = new StreamingBudgetPeaks();
        check(peaks.Add(Sample(3000, 1000, 2500, 12288, 10240, 6000)), "Valid sample is recorded");
        peaks.Add(Sample(4500, 1200, 3800, 12288, 10000, 8000));
        peaks.Add(Sample(2000, 900, 2000, 12288, 10240, 5000));
        check(!peaks.Add(Sample(9000, 9000, 9000, 12288, 0, 9000)) && peaks.Samples == 3, "A sample without a VRAM budget is rejected");

        StreamingBudgetResult result = peaks.Evaluate(2250, "Streets");
        check(result.PeakDemandMiB == 3300 && result.PeakNonStreamingMiB == 1200 && result.PeakOtherMiB == 4200 && result.MinVramBudgetMiB == 10000 && result.PeakVramUsageMiB == 8000 && result.TotalVramMiB == 12288,
            "Peaks are the maxima (demand, nonstreaming, other VRAM, usage) and the minimum VRAM budget");
        check(result.MarginMiB == 1000 && result.HeadroomMiB == 3600 && result.RecommendedMiB == 3584, "Headroom and recommendation follow from the peaks");
        check(result.Verdict == StreamingBudgetVerdict.Raise, "Demand above the map budget with headroom to spare raises");
        check(result.Summary.Contains("Streets") && result.Summary.Contains("3584 MiB") && result.ApplyLabel.Contains("3584 MiB") && result.Report("h").StartsWith("h\n", StringComparison.Ordinal),
            "Summary, apply label and report carry the recommendation");
        check(peaks.Evaluate(3300, "x").Verdict == StreamingBudgetVerdict.NeverLimits && peaks.Evaluate(4000, "x").Verdict == StreamingBudgetVerdict.Lower,
            "The same peaks classify differently by map budget");

        var idle = new StreamingBudgetPeaks();
        idle.Add(Sample(500, 800, 1000, 8192, 8192, 900));
        check(idle.PeakDemandMiB == 0 && idle.PeakOtherMiB == 0, "Negative demand and non-texture VRAM clamp to zero");
    }

    private static void Session(Action<bool, string> check)
    {
        double now = 0;
        bool raid = true, streaming = true, vram = true;
        float budget = 2250;
        string map = "Streets";
        int reads = 0;
        StreamingBudgetSampleSource source = (out StreamingBudgetSample sample) =>
        {
            reads++;
            sample = vram ? Sample(3000, 1000, 2500, 12288, 10240, 6000 + (ulong)(reads % 7)) : default;
            return vram;
        };
        StreamingBudgetMeasurement New() => new StreamingBudgetMeasurement(() => now, () => raid, () => streaming, source, () => budget, () => map);

        StreamingBudgetMeasurement measurement = New();
        raid = false;
        check(!measurement.Start() && !measurement.Running && measurement.Message.Contains("raid"), "Start is refused outside a raid with a reason");
        raid = true; streaming = false;
        check(!measurement.Start() && measurement.Message.Contains("streaming"), "Start is refused with mip streaming off");
        streaming = true; vram = false;
        check(!measurement.Start() && measurement.Message.Contains("video-memory"), "Start is refused without the VRAM query");
        vram = true; budget = float.NaN;
        check(!measurement.Start() && measurement.Message.Contains("budget"), "Start is refused without a usable EFT budget");
        budget = 2250;

        reads = 0;
        check(measurement.Start() && measurement.Running && measurement.Message == null, "Start succeeds in a raid and clears the refusal");
        check(!measurement.Start(), "A running measurement cannot be started again");
        string first = measurement.Progress;
        check(first.Contains("60 s") && ReferenceEquals(first, measurement.Progress), "Progress shows 60 s and is not rebuilt within the same second");
        StreamingBudgetResult finished = null;
        int pumped = 0;
        for (now = 0; now <= 60000; now += 50)
        {
            StreamingBudgetResult step = measurement.Pump();
            pumped++;
            if (step != null)
                finished = step;
            if (now == 15000)
                check(measurement.Progress.Contains("45 s") && reads == 1 + 61, "Samples are taken every 250 ms, not per frame: " + reads);
        }

        check(finished != null && ReferenceEquals(finished, measurement.Result) && !measurement.Running && measurement.Message == null, "The sample finishes after 60 s with a result");
        check(finished.Samples == 241 && reads == 1 + 241, "One reading per 250 ms over the 60 s window, plus the start probe: " + finished.Samples);
        check(finished.MapName == "Streets" && finished.MapBudgetMiB == 2250 && finished.PeakDemandMiB == 2000, "Result carries the map, EFT's budget and the demand");
        check(measurement.Pump() == null, "A finished measurement does nothing on later pumps");

        // Raid ends mid-sample: no result, a reason, and the earlier result stays available for Apply.
        now = 0; reads = 0;
        measurement.Start();
        for (now = 0; now < 5000; now += 50)
            measurement.Pump();
        raid = false; now = 5250;
        check(measurement.Pump() == null && !measurement.Running && measurement.Message.Contains("raid ended") && ReferenceEquals(measurement.Result, finished),
            "A raid that ends mid-sample cancels without a result and keeps the previous one");
        raid = true;

        streaming = true; now = 0;
        measurement.Start();
        measurement.Pump();
        streaming = false; now = 250;
        check(measurement.Pump() == null && !measurement.Running && measurement.Message.Contains("streaming"), "Turning mip streaming off mid-sample cancels it");
        streaming = true;

        // Too few readings (long hitch): refused instead of producing a result from one sample.
        now = 0;
        measurement.Start();
        measurement.Pump();
        now = 60000;
        check(measurement.Pump() == null && measurement.Message.Contains("Too few"), "A hitch that leaves too few readings produces no result");

        // The VRAM query dropping out mid-sample skips those readings.
        now = 0;
        measurement.Start();
        vram = false;
        for (now = 0; now <= 60000; now += 250)
            measurement.Pump();
        check(!measurement.Running && measurement.Message.Contains("Too few"), "Readings without VRAM data are not counted");
    }

    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        Type graphics = find("EFT.Settings.Graphics.GraphicsSettingsController"), cameraType = find("EFT.CameraControl.CameraManager");
        Guid module = graphics.Module.ModuleVersionId;
        StreamingBudgetGame game = StreamingBudgetGame.Bind(find, module);
        FieldInfo nativeBudget = AccessTools.Field(graphics, "MipStreamingMemoryBudget"), instance = AccessTools.Field(cameraType, "instance");
        float oldBudget = (float)nativeBudget.GetValue(null);
        object oldInstance = instance.GetValue(null);
        try
        {
            check(!game.InRaid(), "No raid is detected in the batch editor (InGameStatus.InRaid and Singleton<GameWorld> are both unset)");
            check(game.MapName() != null, "Map name falls back to the active scene");

            nativeBudget.SetValue(null, 1234f);
            check(game.MapBudget() == 1234f, "Without accounting the map budget is EFT's static field");
            float selected = 2250, applied = 2250;
            var accounting = new MipBudgetAccounting(() => selected, () => applied, value => applied = value, () => 0, () => true, () => 0, () => 0);
            accounting.NativePolicyWritten();
            MipBudgetAccounting.Active = accounting;
            check(game.MapBudget() == 2250f, "With active accounting the map budget is its BaseBudget (EFT's selected value)");
            MipBudgetAccounting.Active = null;

            instance.SetValue(null, null);
            check(!game.TryRead(out _), "No CameraManager means no sample");
            // An uninitialised CameraManager has no _vramWrapper, so the real GetVRamUsage returns zeros.
            instance.SetValue(null, RuntimeHelpers.GetUninitializedObject(cameraType));
            check(!game.TryRead(out _) && !game.TryRead(out _), "The bound GetVRamUsage delegate reports zeros as an unavailable query");
        }
        finally
        {
            MipBudgetAccounting.Active = null;
            nativeBudget.SetValue(null, oldBudget);
            instance.SetValue(null, oldInstance);
        }

        try { StreamingBudgetGame.Bind(find, Guid.Empty); check(false, "Wrong module must fail"); }
        catch (NotSupportedException) { check(true, "Wrong EFT module refuses the streaming budget binding"); }
    }
}
