using System;
using System.Globalization;

namespace FuckThisLoadingScreen
{
    /// <summary>What a measurement says about EFT's selected map budget on this GPU.</summary>
    internal enum StreamingBudgetVerdict
    {
        /// <summary>The map budget is above what this GPU can hold.</summary>
        Lower,

        /// <summary>Streaming demand exceeded the map budget and the GPU has room to raise it.</summary>
        Raise,

        /// <summary>Demand stayed under the map budget, which fits the GPU.</summary>
        NeverLimits
    }

    /// <summary>One reading of the texture counters and the DXGI video-memory query, in bytes.</summary>
    internal readonly struct StreamingBudgetSample
    {
        internal readonly ulong Desired, NonStreaming, Current, VramTotal, VramBudget, VramUsage;

        internal StreamingBudgetSample(ulong desired, ulong nonStreaming, ulong current, ulong vramTotal, ulong vramBudget, ulong vramUsage)
        {
            Desired = desired;
            NonStreaming = nonStreaming;
            Current = current;
            VramTotal = vramTotal;
            VramBudget = vramBudget;
            VramUsage = vramUsage;
        }
    }

    /// <summary>Pure arithmetic of the streaming budget estimate. All values are MiB.</summary>
    internal static class StreamingBudgetMath
    {
        internal const double MinMarginMiB = 512, MarginFraction = 0.10;
        internal const int MinRecommendedMiB = 256, MaxRecommendedMiB = 65536, RoundMiB = 64;

        /// <summary>Safety reserve below the GPU's video-memory budget: the larger of 512 MiB and 10 %.</summary>
        internal static double MarginMiB(double minVramBudgetMiB) => Math.Max(MinMarginMiB, MarginFraction * minVramBudgetMiB);

        /// <summary>Video memory left for streamed textures once everything else, the nonstreaming textures and the margin are accounted for.</summary>
        internal static double HeadroomMiB(double minVramBudgetMiB, double peakOtherMiB, double peakNonStreamingMiB) =>
            minVramBudgetMiB - peakOtherMiB - peakNonStreamingMiB - MarginMiB(minVramBudgetMiB);

        /// <summary>
        /// Unity's budget is an upper bound and EFT discards unused mips, so a larger budget costs nothing:
        /// the recommendation is the headroom itself, rounded down to 64 MiB and clamped to 256..65536.
        /// </summary>
        internal static int RecommendMiB(double headroomMiB)
        {
            if (double.IsNaN(headroomMiB))
                return MinRecommendedMiB;
            double clamped = Math.Max(MinRecommendedMiB, Math.Min(MaxRecommendedMiB, headroomMiB));
            return (int)(Math.Floor(clamped / RoundMiB) * RoundMiB);
        }

        internal static StreamingBudgetVerdict Classify(double peakDemandMiB, double mapBudgetMiB, double headroomMiB)
        {
            if (mapBudgetMiB > headroomMiB)
                return StreamingBudgetVerdict.Lower;
            return peakDemandMiB > mapBudgetMiB && headroomMiB > mapBudgetMiB ? StreamingBudgetVerdict.Raise : StreamingBudgetVerdict.NeverLimits;
        }

        internal static string Describe(StreamingBudgetVerdict verdict)
        {
            switch (verdict)
            {
                case StreamingBudgetVerdict.Lower:
                    return "EFT's budget exceeds what this GPU can hold: applying lowers it (avoids VRAM paging stutter)";
                case StreamingBudgetVerdict.Raise:
                    return "Textures were budget-limited: applying raises the budget (sharper textures)";
                default:
                    return "Budget never limited textures here: applying raises the ceiling for heavier scenes; no visible change expected now";
            }
        }
    }

    /// <summary>The numbers and verdict of one finished measurement.</summary>
    internal sealed class StreamingBudgetResult
    {
        internal readonly int Samples, RecommendedMiB;
        internal readonly double PeakDemandMiB, PeakNonStreamingMiB, PeakOtherMiB, MinVramBudgetMiB, PeakVramUsageMiB, TotalVramMiB, MarginMiB, HeadroomMiB, MapBudgetMiB;
        internal readonly StreamingBudgetVerdict Verdict;
        internal readonly string MapName;

        /// <summary>Compact multi-line text for the menu drawer; built once.</summary>
        internal readonly string Summary;

        /// <summary>Label of the Apply button for this result; built once.</summary>
        internal readonly string ApplyLabel;

        internal StreamingBudgetResult(StreamingBudgetPeaks peaks, double mapBudgetMiB, string mapName)
        {
            Samples = peaks.Samples;
            PeakDemandMiB = peaks.PeakDemandMiB;
            PeakNonStreamingMiB = peaks.PeakNonStreamingMiB;
            PeakOtherMiB = peaks.PeakOtherMiB;
            MinVramBudgetMiB = peaks.MinVramBudgetMiB;
            PeakVramUsageMiB = peaks.PeakVramUsageMiB;
            TotalVramMiB = peaks.TotalVramMiB;
            MapBudgetMiB = mapBudgetMiB;
            MapName = mapName ?? "";
            MarginMiB = StreamingBudgetMath.MarginMiB(MinVramBudgetMiB);
            HeadroomMiB = StreamingBudgetMath.HeadroomMiB(MinVramBudgetMiB, PeakOtherMiB, PeakNonStreamingMiB);
            RecommendedMiB = StreamingBudgetMath.RecommendMiB(HeadroomMiB);
            Verdict = StreamingBudgetMath.Classify(PeakDemandMiB, mapBudgetMiB, HeadroomMiB);
            Summary = "Verdict: " + StreamingBudgetMath.Describe(Verdict) + "\n" +
                "Recommended: " + Mib(RecommendedMiB) + " (EFT map budget " + Mib(MapBudgetMiB) + ")\n" +
                "Peak streamed-texture demand: " + Mib(PeakDemandMiB) + "; headroom: " + Mib(HeadroomMiB) + "\n" +
                "VRAM budget: " + Mib(MinVramBudgetMiB) + "; peak usage: " + Mib(PeakVramUsageMiB) + "\n" +
                "Map: " + (MapName.Length == 0 ? "unknown" : MapName) + "; " + Samples.ToString(CultureInfo.InvariantCulture) + " readings";
            ApplyLabel = "Apply measured budget (" + Mib(RecommendedMiB) + ")";
        }

        /// <summary>Full text for the per-measurement file: the summary plus every input of the estimate.</summary>
        internal string Report(string header) => header + "\n" + Summary + "\n\n" +
            "peakDemand=" + Mib(PeakDemandMiB) + "\n" +
            "peakNonStreaming=" + Mib(PeakNonStreamingMiB) + "\n" +
            "peakOther (VRAM usage minus texture memory)=" + Mib(PeakOtherMiB) + "\n" +
            "minVramBudget=" + Mib(MinVramBudgetMiB) + "\n" +
            "peakVramUsage=" + Mib(PeakVramUsageMiB) + "\n" +
            "totalVram=" + Mib(TotalVramMiB) + "\n" +
            "margin=" + Mib(MarginMiB) + "\n" +
            "headroom=" + Mib(HeadroomMiB) + "\n" +
            "mapBudget=" + Mib(MapBudgetMiB) + "\n" +
            "recommended=" + Mib(RecommendedMiB) + "\n";

        internal static string Mib(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + " MiB";
    }

    /// <summary>Running peaks over a measurement's samples; produces the result.</summary>
    internal sealed class StreamingBudgetPeaks
    {
        private const double Mebibyte = 1048576.0;
        private long _demand = long.MinValue, _nonStreaming, _other = long.MinValue, _minBudget = long.MaxValue, _usage, _total;

        internal int Samples { get; private set; }

        internal double PeakDemandMiB => Math.Max(0L, _demand) / Mebibyte;

        internal double PeakNonStreamingMiB => _nonStreaming / Mebibyte;

        internal double PeakOtherMiB => Math.Max(0L, _other) / Mebibyte;

        internal double MinVramBudgetMiB => Samples == 0 ? 0 : _minBudget / Mebibyte;

        internal double PeakVramUsageMiB => _usage / Mebibyte;

        internal double TotalVramMiB => _total / Mebibyte;

        /// <summary>Records a sample; false (and nothing recorded) when the VRAM query reported no budget.</summary>
        internal bool Add(in StreamingBudgetSample sample)
        {
            if (sample.VramBudget == 0)
                return false;
            _demand = Math.Max(_demand, (long)sample.Desired - (long)sample.NonStreaming);
            _nonStreaming = Math.Max(_nonStreaming, (long)sample.NonStreaming);
            _other = Math.Max(_other, (long)sample.VramUsage - (long)sample.Current);
            _minBudget = Math.Min(_minBudget, (long)sample.VramBudget);
            _usage = Math.Max(_usage, (long)sample.VramUsage);
            _total = Math.Max(_total, (long)sample.VramTotal);
            Samples++;
            return true;
        }

        internal StreamingBudgetResult Evaluate(double mapBudgetMiB, string mapName) => new StreamingBudgetResult(this, mapBudgetMiB, mapName);
    }
}
