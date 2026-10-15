using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FTLS.LevelStageTimer
{
    // Wall-clock intervals are inclusive and may overlap. Never sum them as exclusive CPU cost.
    internal sealed class StageRow
    {
        public int Id, Count, StartThread, EndThread;
        public string Stage, Kind, Detail, Outcome = "open";
        public long Start, End;
    }

    internal sealed class Capture
    {
        internal readonly List<RetentionRow> Retentions = new List<RetentionRow>();
        internal int DroppedRetentions, RetentionObserverErrors;
        public readonly List<StageRow> Rows = new List<StageRow>();
        public readonly List<string> Samples = new List<string>();
        public readonly Dictionary<string, int> StageCounts = new Dictionary<string, int>();
        public readonly Dictionary<string, int> Suppressed = new Dictionary<string, int>();
        public readonly Dictionary<string, AggregateRow> Aggregates = new Dictionary<string, AggregateRow>();
        public readonly List<ScenePhase> ScenePhases = new List<ScenePhase>();
        public readonly Dictionary<string, ScenePhase> SceneLookup = new Dictionary<string, ScenePhase>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<LifecycleKey, LifecycleRow> LifecycleRows = new Dictionary<LifecycleKey, LifecycleRow>();
        public readonly List<LifecycleDetail> PinewoodDetails = new List<LifecycleDetail>();
        public readonly HashSet<int> SceneHandlerMethods = new HashSet<int>();
        public long DroppedSceneHandlerCalls, OutOfScopeSceneHandlerCalls;
        public int DroppedScenes, SceneObserverErrors, SceneRequests, BundleErrorReadFailures;
        public long DroppedLifecycleDetails, UnmatchedLifecycleCalls, AmbiguousLifecycleCalls, LifecycleObserverErrors, DroppedLifecycleDepth;
        public long PinewoodCalls;
        public int PinewoodMinimumIndex;
        public long PinewoodMinimumTicks;
        public DateTime StartedUtc = DateTime.UtcNow;
        public long Start, Terminal;
        public string Outcome;
        public int DroppedRows, DroppedSamples, DroppedAggregateStages;
        public bool Sealed;
        public bool RootCompleted;
        public object Marker;
    }

    // Constant storage per stage, including calls beyond the individual-row budget.
    // Total is a sum of inclusive call durations, not merged wall coverage or CPU time.
    internal sealed class AggregateRow
    {
        public string Stage;
        public long Calls, Completed, Threw, TotalTicks, MaxTicks, FirstStart, LastEnd;
    }

    internal struct AggregateToken
    {
        internal Capture Capture;
        internal AggregateRow Row;
        internal Span Detail;
        internal long Start;
        internal bool Finished;
        internal Recorder Owner;
    }

    internal sealed class Span
    {
        internal Capture Capture;
        internal StageRow Row;
        internal bool IsRoot;
        internal Recorder Owner;
        public void Finish(string outcome) => Owner.Finish(this, outcome);
    }

    internal sealed class Recorder
    {
        private readonly object gate = new object();
        private readonly Func<long> clock;
        private readonly long frequency;
        private readonly int maxCaptures, maxRows, maxSamples;
        private readonly double timeoutSeconds;
        private int started;
        private Capture active;
        private readonly Queue<Capture> completed = new Queue<Capture>();

        public Recorder(int maxCaptures = 3, double timeoutSeconds = 600, int maxRows = 2048,
            int maxSamples = 1200, Func<long> clock = null, long frequency = 0)
        {
            this.maxCaptures = maxCaptures;
            this.timeoutSeconds = timeoutSeconds;
            this.maxRows = maxRows;
            this.maxSamples = maxSamples;
            this.clock = clock ?? Stopwatch.GetTimestamp;
            this.frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        }

        public bool IsActive { get { lock (gate) return active != null && active.Outcome == null; } }
        internal Capture ActiveCapture { get { lock (gate) return active != null && active.Outcome == null ? active : null; } }
        public bool Exhausted { get { lock (gate) return started >= maxCaptures && active == null && completed.Count == 0; } }
        public double Milliseconds(long ticks) => ticks * 1000.0 / frequency;
        internal double Elapsed(Capture capture) => Milliseconds(clock() - capture.Start);
        internal long Timestamp() => clock();

        public AggregateToken BeginAggregate(string stage, string kind, int count = -1, int detailLimit = 64)
        {
            lock (gate)
            {
                if (active == null || active.Outcome != null) return default;
                if (!active.Aggregates.TryGetValue(stage, out AggregateRow row))
                {
                    // Fixed catalog uses four slots; retain a hard independent bound.
                    if (active.Aggregates.Count >= 16) { active.DroppedAggregateStages++; return default; }
                    row = new AggregateRow { Stage = stage, FirstStart = clock() };
                    active.Aggregates.Add(stage, row);
                }
                Span detail = Begin(stage, kind, count: count, limit: detailLimit);
                long start = clock();
                if (row.Calls == 0) row.FirstStart = start;
                row.Calls++;
                return new AggregateToken { Owner = this, Capture = active, Row = row, Detail = detail, Start = start };
            }
        }

        public void FinishAggregate(ref AggregateToken token, bool threw)
        {
            lock (gate)
            {
                if (token.Finished || token.Capture == null) return;
                token.Finished = true;
                if (token.Capture.Sealed) return;
                long end = clock(), duration = end - token.Start;
                AggregateRow row = token.Row;
                row.TotalTicks += duration;
                row.MaxTicks = Math.Max(row.MaxTicks, duration);
                row.LastEnd = Math.Max(row.LastEnd, end);
                if (threw) row.Threw++; else row.Completed++;
                token.Detail?.Finish(threw ? "threw" : "completed");
            }
        }

        public Span Begin(string stage, string kind, bool root = false, int count = -1, int limit = 0, string detail = null)
        {
            lock (gate)
            {
                if (root)
                {
                    // A reconnect/matching entry inside an existing capture does not own its lifetime.
                    if (active != null)
                    {
                        if (!active.RootCompleted && active.Outcome == null) return null;
                        if (active.Outcome == null) { active.Outcome = "superseded-before-start"; active.Terminal = clock(); }
                        completed.Enqueue(Seal(active, clock()));
                        active = null;
                    }
                    if (started >= maxCaptures) return null;
                    active = new Capture { Start = clock() };
                    started++;
                }
                if (active == null || active.Outcome != null) return null;
                if (limit > 0)
                {
                    active.StageCounts.TryGetValue(stage, out int calls);
                    if (calls >= limit)
                    {
                        active.Suppressed.TryGetValue(stage, out int suppressed);
                        if (suppressed < int.MaxValue) active.Suppressed[stage] = suppressed + 1;
                        return null;
                    }
                    active.StageCounts[stage] = calls + 1;
                }
                if (active.Rows.Count >= maxRows) { active.DroppedRows++; return null; }
                var row = new StageRow { Id = active.Rows.Count + 1, Stage = stage, Kind = kind,
                    Start = clock(), Count = count, StartThread = Environment.CurrentManagedThreadId,
                    Detail = detail == null || detail.Length <= 320 ? detail : detail.Substring(0, 320) };
                active.Rows.Add(row);
                return new Span { Owner = this, Capture = active, Row = row, IsRoot = root };
            }
        }

        internal void Finish(Span span, string outcome)
        {
            lock (gate)
            {
                if (span.Capture.Sealed || span.Row.Outcome != "open") return;
                span.Row.End = clock();
                span.Row.EndThread = Environment.CurrentManagedThreadId;
                span.Row.Outcome = outcome;
                if (span.IsRoot) span.Capture.RootCompleted = true;
                if (span.IsRoot && outcome != "completed" && span.Capture.Outcome == null)
                {
                    span.Capture.Terminal = span.Row.End;
                    span.Capture.Outcome = outcome;
                }
            }
        }

        public void AttachMarker(object marker)
        {
            lock (gate) { if (active != null && active.Outcome == null && active.Marker == null) active.Marker = marker; }
        }

        public void GameStarted(object marker = null)
        {
            lock (gate)
            {
                if (active == null || active.Outcome != null || !ReferenceEquals(active.Marker, marker)) return;
                active.Terminal = clock();
                active.Outcome = "game-started";
            }
        }

        public void Sample(string values)
        {
            lock (gate)
            {
                if (active == null || active.Outcome != null) return;
                if (active.Samples.Count < maxSamples)
                    active.Samples.Add(Milliseconds(clock() - active.Start).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "," + values);
                else active.DroppedSamples++;
            }
        }

        // Called by the Unity main thread; late task completions cannot mutate a sealed export.
        public Capture TakeReady(bool shutdown = false)
        {
            lock (gate)
            {
                if (completed.Count > 0) return completed.Dequeue();
                if (active == null) return null;
                long now = clock();
                if (active.Outcome == null)
                {
                    if (!shutdown && (now - active.Start) / (double)frequency < timeoutSeconds) return null;
                    active.Terminal = now;
                    active.Outcome = shutdown ? "plugin-stopped" : "timeout";
                }
                // Allow task observers to catch up after the native game-start marker.
                if (!shutdown && active.Outcome == "game-started" && active.Rows.Exists(r => r.Outcome == "open") &&
                    (now - active.Terminal) / (double)frequency < 1) return null;
                Capture result = Seal(active, now);
                active = null;
                return result;
            }
        }

        private static Capture Seal(Capture capture, long now)
        {
            capture.Sealed = true;
            foreach (StageRow row in capture.Rows)
                if (row.Outcome == "open") { row.End = now; row.Outcome = "incomplete-at-export"; }
            return capture;
        }
    }
}
