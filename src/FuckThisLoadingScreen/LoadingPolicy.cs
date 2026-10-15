using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FuckThisLoadingScreen
{
    public enum LoadingPriority { Native, Normal, High }

    internal sealed class Options
    {
        public bool Enabled, AudioBatching, NonCollectingMeasurements, ParallelScenes, ScopedSpawnZones, SkipBuiltInSceneProbes, LootBundlePriority, FrameCapOverride, BoundedScenes;
        public int AudioBatchSize, LoadingFrameLimit = 240, SceneWindow = 2;
        public LoadingPriority Priority;
        public override string ToString() => $"Enabled={Enabled}; AudioBatching={AudioBatching}; AudioBatchSize={AudioBatchSize}; NonCollectingMeasurements={NonCollectingMeasurements}; Priority={Priority}; ParallelScenes={ParallelScenes}; ScopedSpawnZones={ScopedSpawnZones}; SkipBuiltInSceneProbes={SkipBuiltInSceneProbes}; LootBundlePriority={LootBundlePriority}; FrameCapOverride={FrameCapOverride}; LoadingFrameLimit={LoadingFrameLimit}; BoundedScenes={BoundedScenes}; SceneWindow={SceneWindow}";
    }

    internal sealed class LoadingRun
    {
        public Options Options;
        public long Started;
        public object Metrics;
        public bool Returned, Closed;
        public int Maps, NonCollectingCalls;
        public int SpawnZoneScopes, SpawnZoneHits, SpawnZoneNativeCalls, SpawnZoneInvalidations;
        public int SceneProbeRequests, SceneProbeSkipped, SceneProbeNative;
        public long SceneProbeGuardTicks;
        public string SceneProbeFirstError;
        public int LootBundleScopes, LootBundlePriorityScopes;
        public long LootBundleScopeTicks;
        public readonly int[] SceneProbeFallbacks = new int[8];
        public readonly List<FrameCapRow> FrameCaps = new List<FrameCapRow>();
        public readonly List<SceneScheduleRow> Schedules = new List<SceneScheduleRow>();
        public int DroppedFrameCaps, DroppedSchedules;
    }

    internal sealed class MapScope
    {
        public LoadingRun Run;
        public bool Closed, OwnsPriority;
        public FrameCapLease FrameCap;
    }

    internal sealed class BundleScope
    {
        public LoadingRun Run;
        public bool Closed, OwnsPriority;
        public long Started;
    }

    // All Unity state changes happen on the constructing (Unity main) thread.
    internal sealed class LoadingPolicy
    {
        private readonly int mainThread = Environment.CurrentManagedThreadId;
        private readonly Func<Options> readOptions;
        private readonly Func<int> readPriority;
        private readonly Action<int> writePriority;
        private readonly Func<LoadingPriority, int> priorityValue;
        private readonly Func<long> clock;
        private readonly long frequency;
        private readonly ConcurrentQueue<Action> pending = new ConcurrentQueue<Action>();
        private readonly List<MapScope> maps = new List<MapScope>();
        private readonly List<BundleScope> bundles = new List<BundleScope>();
        private readonly Action<string, LoadingRun> report;
        private readonly LoadingFrameCap frameCap;
        private int priorPriority, appliedPriority;
        private bool ownsPriority, stopped;
        private volatile LoadingRun current;

        public LoadingPolicy(Func<Options> readOptions, Func<int> readPriority, Action<int> writePriority,
            Func<LoadingPriority, int> priorityValue, Action<string, LoadingRun> report, Func<long> clock = null, long frequency = 0, LoadingFrameCap frameCap = null)
        {
            this.readOptions = readOptions; this.readPriority = readPriority; this.writePriority = writePriority;
            this.priorityValue = priorityValue; this.report = report; this.clock = clock ?? Stopwatch.GetTimestamp; this.frameCap = frameCap;
            this.frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        }

        public bool IsLoading => current != null;
        public LoadingRun BoundedSceneRun
        {
            get
            {
                LoadingRun run = current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.BoundedScenes && run.Maps > 0 &&
                    Environment.CurrentManagedThreadId == mainThread ? run : null;
            }
        }
        internal double Elapsed(LoadingRun run) => (clock() - run.Started) * 1000.0 / frequency;
        internal Func<bool> CaptureSceneActivity()
        {
            LoadingRun run = BoundedSceneRun;
            return () => run != null && ReferenceEquals(BoundedSceneRun, run);
        }
        internal Action<BoundedSceneStats> BeginSceneReport()
        {
            LoadingRun run = BoundedSceneRun;
            if (run == null) return null;
            if (run.Schedules.Count >= 128) { run.DroppedSchedules++; return null; }
            var row = new SceneScheduleRow { StartMs = Elapsed(run), Window = Math.Max(1, Math.Min(4, run.Options.SceneWindow)) };
            run.Schedules.Add(row);
            return stats =>
            {
                lock (run)
                {
                    if (run.Closed) return;
                    row.EndMs = Elapsed(run); row.Submitted = stats.Submitted; row.Completed = stats.Completed;
                    row.Peak = stats.Peak; row.Outcome = stats.Outcome; row.Requested = stats.Requested; row.Failed = stats.Failed;
                }
            };
        }
        public bool LootBundlePriority
        {
            get
            {
                LoadingRun run = current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.LootBundlePriority &&
                    run.Options.Priority != LoadingPriority.Native && Environment.CurrentManagedThreadId == mainThread;
            }
        }
        public LoadingRun SceneProbeRun
        {
            get
            {
                LoadingRun run = current;
                return run != null && !run.Closed && run.Options.Enabled && run.Maps > 0 &&
                    Environment.CurrentManagedThreadId == mainThread ? run : null;
            }
        }
        public LoadingRun SpawnZoneRun
        {
            get
            {
                LoadingRun run = current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.ScopedSpawnZones &&
                    Environment.CurrentManagedThreadId == mainThread ? run : null;
            }
        }

        public void CountSpawnZoneScope() { LoadingRun run = current; if (run != null) Interlocked.Increment(ref run.SpawnZoneScopes); }
        public void CountSpawnZoneLookup(bool cached)
        {
            LoadingRun run = current;
            if (run == null) return;
            if (cached) Interlocked.Increment(ref run.SpawnZoneHits); else Interlocked.Increment(ref run.SpawnZoneNativeCalls);
        }
        public void CountSpawnZoneInvalidation() { LoadingRun run = current; if (run != null) Interlocked.Increment(ref run.SpawnZoneInvalidations); }
        public int AudioBatchSize
        {
            get
            {
                LoadingRun run = current;
                return run != null && run.Options.Enabled && run.Options.AudioBatching && run.Maps > 0
                    ? Math.Max(300, Math.Min(4800, run.Options.AudioBatchSize)) : 300;
            }
        }
        public bool NonCollectingMeasurements
        {
            get { LoadingRun run = current; return run != null && run.Options.Enabled && run.Options.NonCollectingMeasurements; }
        }
        public bool ParallelScenes
        {
            get { LoadingRun run = current; return run != null && run.Options.Enabled && run.Options.ParallelScenes && run.Maps > 0; }
        }

        public LoadingRun Begin()
        {
            if (stopped || Environment.CurrentManagedThreadId != mainThread) return null;
            if (current != null)
            {
                if (!current.Returned) return null; // nested matching/reconnect does not own the root
                End(current, "superseded-before-start");
            }
            var run = new LoadingRun { Options = readOptions(), Started = clock() };
            current = run;
            return run;
        }

        public void AttachMetrics(object metrics)
        {
            LoadingRun run = current;
            if (run != null && run.Metrics == null) run.Metrics = metrics;
        }
        public void GameStarted(object metrics)
        {
            LoadingRun run = current;
            if (run != null && ReferenceEquals(run.Metrics, metrics)) Dispatch(() => End(run, "game-started"));
        }
        public void RootFinished(LoadingRun run, bool failed)
        {
            if (run == null) return;
            Dispatch(() => { if (run.Closed) return; run.Returned = true; if (failed) End(run, "failed-or-canceled"); });
        }

        public MapScope EnterMap()
        {
            LoadingRun run = current;
            if (stopped || run == null || Environment.CurrentManagedThreadId != mainThread) return null;
            if (maps.Count >= 8) return null;
            var scope = new MapScope { Run = run };
            scope.OwnsPriority = AcquirePriority(run);
            maps.Add(scope); run.Maps++;
            if (run.Options.Enabled && run.Options.FrameCapOverride) scope.FrameCap = frameCap?.Enter(run, run.Options.LoadingFrameLimit, Elapsed(run));
            return scope;
        }
        private bool AcquirePriority(LoadingRun run)
        {
            if (run.Options.Enabled && run.Options.Priority != LoadingPriority.Native)
            {
                if (!ownsPriority)
                {
                    priorPriority = readPriority();
                    appliedPriority = priorityValue(run.Options.Priority);
                    writePriority(appliedPriority);
                    ownsPriority = true;
                }
                return true;
            }
            return false;
        }
        public BundleScope EnterLootBundles()
        {
            if (stopped || !LootBundlePriority || bundles.Count >= 8) return null;
            LoadingRun run = current;
            var scope = new BundleScope { Run = run, Started = clock(), OwnsPriority = AcquirePriority(run) };
            bundles.Add(scope); run.LootBundleScopes++;
            if (scope.OwnsPriority) run.LootBundlePriorityScopes++;
            return scope;
        }
        public void LeaveLootBundles(BundleScope scope)
        {
            if (scope != null) Dispatch(() => CloseBundles(scope));
        }
        private void CloseBundles(BundleScope scope)
        {
            if (scope.Closed) return;
            scope.Closed = true;
            scope.Run.LootBundleScopeTicks += clock() - scope.Started;
            bundles.Remove(scope);
            RestoreIfUnused();
        }
        public void LeaveMap(MapScope scope)
        {
            if (scope != null) Dispatch(() => CloseMap(scope));
        }
        private void CloseMap(MapScope scope)
        {
            if (scope.Closed) return;
            scope.Closed = true;
            scope.Run.Maps--;
            maps.Remove(scope);
            frameCap?.Leave(scope.FrameCap, Elapsed(scope.Run));
            RestoreIfUnused();
        }
        private void RestoreIfUnused()
        {
            if (ownsPriority && !maps.Exists(m => m.OwnsPriority) && !bundles.Exists(b => b.OwnsPriority)) RestorePriority();
        }
        private void RestorePriority()
        {
            // Do not overwrite another owner's newer global priority setting.
            if (readPriority() == appliedPriority) writePriority(priorPriority);
            ownsPriority = false;
        }
        public void CountNonCollectingCall()
        {
            LoadingRun run = current;
            if (run != null) Interlocked.Increment(ref run.NonCollectingCalls);
        }
        private void Dispatch(Action action)
        {
            if (Environment.CurrentManagedThreadId == mainThread) action();
            else if (!stopped) pending.Enqueue(action);
        }
        public void Pump()
        {
            while (pending.TryDequeue(out Action action)) action();
            LoadingRun run = current;
            if (run != null && (clock() - run.Started) / (double)frequency >= 600) End(run, "timeout");
        }
        private void End(LoadingRun run, string outcome)
        {
            if (run.Closed) return;
            run.Closed = true;
            for (int i = maps.Count - 1; i >= 0; i--) if (maps[i].Run == run) CloseMap(maps[i]);
            for (int i = bundles.Count - 1; i >= 0; i--) if (bundles[i].Run == run) CloseBundles(bundles[i]);
            if (ReferenceEquals(current, run)) current = null;
            // The plugin buffers this small report until a later Update outside a loading run.
            report($"{DateTime.UtcNow:O} | {outcome} | {run.Options} | NonCollectingCalls={run.NonCollectingCalls}; SpawnZoneScopes={run.SpawnZoneScopes}; SpawnZoneHits={run.SpawnZoneHits}; SpawnZoneNativeCalls={run.SpawnZoneNativeCalls}; SpawnZoneInvalidations={run.SpawnZoneInvalidations}; SceneProbeRequests={run.SceneProbeRequests}; SceneProbeSkipped={run.SceneProbeSkipped}; SceneProbeNative={run.SceneProbeNative}; SceneProbeGuardMs={(run.SceneProbeGuardTicks * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; SceneProbeFallbacks[Shape,BuiltIn,Location,Manifest,Registered,ExistingOperation,File,ObserverError]={string.Join(",", run.SceneProbeFallbacks)}; SceneProbeFirstError={run.SceneProbeFirstError ?? "none"}; LootBundleScopes={run.LootBundleScopes}; LootBundlePriorityScopes={run.LootBundlePriorityScopes}; LootBundleScopeMs={(run.LootBundleScopeTicks * 1000.0 / frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; FrameCapRecords={run.FrameCaps.Count}; DroppedFrameCapRecords={run.DroppedFrameCaps}; SceneScheduleRecords={run.Schedules.Count}; DroppedSceneScheduleRecords={run.DroppedSchedules}", run);
        }
        public void Stop()
        {
            stopped = true;
            if (current != null) End(current, "plugin-stopped");
            while (pending.TryDequeue(out _)) { }
            if (ownsPriority) RestorePriority();
        }
    }
}
