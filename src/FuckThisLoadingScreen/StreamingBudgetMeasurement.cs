using System;
using System.Globalization;

namespace FuckThisLoadingScreen
{
    /// <summary>Reads one sample; false when the video-memory query is unavailable.</summary>
    internal delegate bool StreamingBudgetSampleSource(out StreamingBudgetSample sample);

    /// <summary>
    /// A 60 s sampling session driven from the main thread (<see cref="Pump"/>). Owns the state shown by the menu drawers:
    /// progress, the last result and the reason of the last refusal or cancellation.
    /// </summary>
    internal sealed class StreamingBudgetMeasurement
    {
        internal static StreamingBudgetMeasurement Active;

        internal const double DurationMs = 60000, IntervalMs = 250;
        internal const int MinSamples = 40;

        private readonly Func<double> _clock;
        private readonly Func<bool> _inRaid, _streamingActive;
        private readonly StreamingBudgetSampleSource _source;
        private readonly Func<float> _mapBudget;
        private readonly Func<string> _mapName;
        private StreamingBudgetPeaks _peaks;
        private double _end, _next;
        private string _map, _progress;
        private int _shownRemaining = -1;

        internal StreamingBudgetMeasurement(Func<double> clock, Func<bool> inRaid, Func<bool> streamingActive,
            StreamingBudgetSampleSource source, Func<float> mapBudget, Func<string> mapName)
        {
            _clock = clock;
            _inRaid = inRaid;
            _streamingActive = streamingActive;
            _source = source;
            _mapBudget = mapBudget;
            _mapName = mapName;
        }

        internal bool Running { get; private set; }

        /// <summary>The last finished measurement; kept when a later attempt is refused or cancelled.</summary>
        internal StreamingBudgetResult Result { get; private set; }

        /// <summary>Why the last attempt did not produce a result; null after a start or a result.</summary>
        internal string Message { get; private set; }

        /// <summary>"Measuring..." text for the drawer; rebuilt only when the remaining whole seconds change.</summary>
        internal string Progress
        {
            get
            {
                int remaining = Math.Max(0, (int)Math.Ceiling((_end - _clock()) / 1000.0));
                if (remaining != _shownRemaining)
                {
                    _shownRemaining = remaining;
                    _progress = "Measuring: " + remaining.ToString(CultureInfo.InvariantCulture) + " s left. Keep looking and moving around, including aiming down scopes.";
                }

                return _progress;
            }
        }

        /// <summary>Begins a sample unless a precondition fails; the reason is left in <see cref="Message"/>.</summary>
        internal bool Start()
        {
            if (Running)
                return false;
            if (!_inRaid())
                return Refuse("Not in a raid. Load into a raid first.");
            if (!_streamingActive())
                return Refuse("Mip streaming is off. Enable it in EFT graphics settings and load a raid again.");
            if (!_source(out _))
                return Refuse("The GPU video-memory query is unavailable on this system.");
            if (!IsUsable(_mapBudget()))
                return Refuse("EFT's streaming budget for this map is unavailable.");
            _peaks = new StreamingBudgetPeaks();
            _map = _mapName();
            double now = _clock();
            _end = now + DurationMs;
            _next = now;
            _shownRemaining = -1;
            Message = null;
            Running = true;
            return true;
        }

        /// <summary>Takes due samples; returns the result in the call that finishes a measurement, otherwise null.</summary>
        internal StreamingBudgetResult Pump()
        {
            if (!Running)
                return null;
            double now = _clock();
            if (now < _next)
                return null;
            if (!_inRaid())
                return Cancel("The raid ended during the measurement; nothing was recorded.");
            if (!_streamingActive())
                return Cancel("Mip streaming was turned off during the measurement; nothing was recorded.");
            if (_source(out StreamingBudgetSample sample))
                _peaks.Add(sample);
            _next = now + IntervalMs;
            return now >= _end ? Finish() : null;
        }

        private StreamingBudgetResult Finish()
        {
            Running = false;
            if (_peaks.Samples < MinSamples)
                return Cancel("Too few video-memory readings (" + _peaks.Samples.ToString(CultureInfo.InvariantCulture) + "); nothing was recorded.");
            float mapBudget = _mapBudget();
            if (!IsUsable(mapBudget))
                return Cancel("EFT's streaming budget for this map became unavailable; nothing was recorded.");
            Result = _peaks.Evaluate(mapBudget, _map);
            Message = null;
            return Result;
        }

        private StreamingBudgetResult Cancel(string reason)
        {
            Running = false;
            Message = reason;
            return null;
        }

        private bool Refuse(string reason)
        {
            Message = reason;
            return false;
        }

        private static bool IsUsable(float budget) => !float.IsNaN(budget) && !float.IsInfinity(budget) && budget > 0;
    }
}
