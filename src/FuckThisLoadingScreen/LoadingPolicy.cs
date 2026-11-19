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
        public bool Enabled, AudioBatching, NonCollectingMeasurements, ParallelScenes, ScopedSpawnZones, SkipBuiltInSceneProbes, LootBundlePriority, FrameCapOverride, BoundedScenes, DebugReports, ReduceBotProfilePreloadDelay;
        public int AudioBatchSize, LoadingFrameLimit = 240, SceneWindow = 2;
        public LoadingPriority Priority;
        public override string ToString() => $"Enabled={Enabled}; AudioBatching={AudioBatching}; AudioBatchSize={AudioBatchSize}; NonCollectingMeasurements={NonCollectingMeasurements}; Priority={Priority}; ParallelScenes={ParallelScenes}; ScopedSpawnZones={ScopedSpawnZones}; SkipBuiltInSceneProbes={SkipBuiltInSceneProbes}; LootBundlePriority={LootBundlePriority}; FrameCapOverride={FrameCapOverride}; LoadingFrameLimit={LoadingFrameLimit}; BoundedScenes={BoundedScenes}; SceneWindow={SceneWindow}; DebugReports={DebugReports}; ReduceBotProfilePreloadDelay={ReduceBotProfilePreloadDelay}";
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
        public int BotPreloadDelaysReduced, BotPreloadDelayNominalSavedMs;
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
        private readonly int _mainThread = Environment.CurrentManagedThreadId;
        private readonly Func<Options> _readOptions;
        private readonly Func<int> _readPriority;
        private readonly Action<int> _writePriority;
        private readonly Func<LoadingPriority, int> _priorityValue;
        private readonly Func<long> _clock;
        private readonly long _frequency;
        private readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();
        private readonly List<MapScope> _maps = new List<MapScope>();
        private readonly List<BundleScope> _bundles = new List<BundleScope>();
        private readonly Action<string, LoadingRun> _report;
        private readonly LoadingFrameCap _frameCap;
        private int _priorPriority, _appliedPriority;
        private bool _ownsPriority, _stopped;
        private volatile LoadingRun _current;

        public LoadingPolicy(Func<Options> readOptions, Func<int> readPriority, Action<int> writePriority,
            Func<LoadingPriority, int> priorityValue, Action<string, LoadingRun> report, Func<long> clock = null, long frequency = 0, LoadingFrameCap frameCap = null)
        {
            _readOptions = readOptions;
            _readPriority = readPriority;
            _writePriority = writePriority;
            _priorityValue = priorityValue;
            _report = report;
            _clock = clock ?? Stopwatch.GetTimestamp;
            _frameCap = frameCap;
            _frequency = frequency == 0 ? Stopwatch.Frequency : frequency;
        }

        public bool IsLoading => _current != null;
        internal int BotProfileDelay(int milliseconds)
        {
            LoadingRun run = _current;
            if (milliseconds != BotProfilePreloadDelay.NativeMilliseconds || run == null || run.Closed ||
                !run.Options.Enabled || !run.Options.ReduceBotProfilePreloadDelay ||
                Environment.CurrentManagedThreadId != _mainThread)
                return milliseconds;
            Interlocked.Increment(ref run.BotPreloadDelaysReduced);
            Interlocked.Add(ref run.BotPreloadDelayNominalSavedMs,
                BotProfilePreloadDelay.NativeMilliseconds - BotProfilePreloadDelay.ReducedMilliseconds);
            return BotProfilePreloadDelay.ReducedMilliseconds;
        }
        public LoadingRun BoundedSceneRun
        {
            get
            {
                LoadingRun run = _current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.BoundedScenes && run.Maps > 0 &&
                    Environment.CurrentManagedThreadId == _mainThread ? run : null;
            }
        }
        internal double Elapsed(LoadingRun run) => (_clock() - run.Started) * 1000.0 / _frequency;
        internal Func<bool> CaptureSceneActivity()
        {
            LoadingRun run = BoundedSceneRun;
            return () => run != null && ReferenceEquals(BoundedSceneRun, run);
        }
        internal Action<BoundedSceneStats> BeginSceneReport()
        {
            LoadingRun run = BoundedSceneRun;
            if (run == null)
                return null;
            if (run.Schedules.Count >= 128)
            { run.DroppedSchedules++; return null; }
            var row = new SceneScheduleRow { StartMs = Elapsed(run), Window = Math.Max(1, Math.Min(4, run.Options.SceneWindow)) };
            run.Schedules.Add(row);
            return stats =>
            {
                lock (run)
                {
                    if (run.Closed)
                        return;
                    row.EndMs = Elapsed(run);
                    row.Submitted = stats.Submitted;
                    row.Completed = stats.Completed;
                    row.Peak = stats.Peak;
                    row.Outcome = stats.Outcome;
                    row.Requested = stats.Requested;
                    row.Failed = stats.Failed;
                }
            };
        }
        public bool LootBundlePriority
        {
            get
            {
                LoadingRun run = _current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.LootBundlePriority &&
                    run.Options.Priority != LoadingPriority.Native && Environment.CurrentManagedThreadId == _mainThread;
            }
        }
        public LoadingRun SceneProbeRun
        {
            get
            {
                LoadingRun run = _current;
                return run != null && !run.Closed && run.Options.Enabled && run.Maps > 0 &&
                    Environment.CurrentManagedThreadId == _mainThread ? run : null;
            }
        }
        public LoadingRun SpawnZoneRun
        {
            get
            {
                LoadingRun run = _current;
                return run != null && !run.Closed && run.Options.Enabled && run.Options.ScopedSpawnZones &&
                    Environment.CurrentManagedThreadId == _mainThread ? run : null;
            }
        }

        public void CountSpawnZoneScope() { LoadingRun run = _current; if (run != null) Interlocked.Increment(ref run.SpawnZoneScopes); }
        public void CountSpawnZoneLookup(bool cached)
        {
            LoadingRun run = _current;
            if (run == null)
                return;
            if (cached)
                Interlocked.Increment(ref run.SpawnZoneHits);
            else
                Interlocked.Increment(ref run.SpawnZoneNativeCalls);
        }
        public void CountSpawnZoneInvalidation() { LoadingRun run = _current; if (run != null) Interlocked.Increment(ref run.SpawnZoneInvalidations); }
        public int AudioBatchSize
        {
            get
            {
                LoadingRun run = _current;
                return run != null && run.Options.Enabled && run.Options.AudioBatching && run.Maps > 0
                    ? Math.Max(300, Math.Min(4800, run.Options.AudioBatchSize)) : 300;
            }
        }
        public bool NonCollectingMeasurements
        {
            get { LoadingRun run = _current; return run != null && run.Options.Enabled && run.Options.NonCollectingMeasurements; }
        }
        public bool ParallelScenes
        {
            get { LoadingRun run = _current; return run != null && run.Options.Enabled && run.Options.ParallelScenes && run.Maps > 0; }
        }

        public LoadingRun Begin()
        {
            if (_stopped || Environment.CurrentManagedThreadId != _mainThread)
                return null;
            if (_current != null)
            {
                if (!_current.Returned)
                    return null; // nested matching/reconnect does not own the root
                End(_current, "superseded-before-start");
            }
            var run = new LoadingRun { Options = _readOptions(), Started = _clock() };
            _current = run;
            return run;
        }

        public void AttachMetrics(object metrics)
        {
            LoadingRun run = _current;
            if (run != null && run.Metrics == null)
                run.Metrics = metrics;
        }
        public void GameStarted(object metrics)
        {
            LoadingRun run = _current;
            if (run != null && ReferenceEquals(run.Metrics, metrics))
                Dispatch(() => End(run, "game-started"));
        }
        public void RootFinished(LoadingRun run, bool failed)
        {
            if (run == null)
                return;
            Dispatch(() => { if (run.Closed) return; run.Returned = true; if (failed) End(run, "failed-or-canceled"); });
        }

        public MapScope EnterMap()
        {
            LoadingRun run = _current;
            if (_stopped || run == null || Environment.CurrentManagedThreadId != _mainThread)
                return null;
            if (_maps.Count >= 8)
                return null;
            var scope = new MapScope { Run = run };
            scope.OwnsPriority = AcquirePriority(run);
            _maps.Add(scope);
            run.Maps++;
            if (run.Options.Enabled && run.Options.FrameCapOverride)
                scope.FrameCap = _frameCap?.Enter(run, run.Options.LoadingFrameLimit, Elapsed(run));
            return scope;
        }
        private bool AcquirePriority(LoadingRun run)
        {
            if (run.Options.Enabled && run.Options.Priority != LoadingPriority.Native)
            {
                if (!_ownsPriority)
                {
                    _priorPriority = _readPriority();
                    _appliedPriority = _priorityValue(run.Options.Priority);
                    _writePriority(_appliedPriority);
                    _ownsPriority = true;
                }
                return true;
            }
            return false;
        }
        public BundleScope EnterLootBundles()
        {
            if (_stopped || !LootBundlePriority || _bundles.Count >= 8)
                return null;
            LoadingRun run = _current;
            var scope = new BundleScope { Run = run, Started = _clock(), OwnsPriority = AcquirePriority(run) };
            _bundles.Add(scope);
            run.LootBundleScopes++;
            if (scope.OwnsPriority)
                run.LootBundlePriorityScopes++;
            return scope;
        }
        public void LeaveLootBundles(BundleScope scope)
        {
            if (scope != null)
                Dispatch(() => CloseBundles(scope));
        }
        private void CloseBundles(BundleScope scope)
        {
            if (scope.Closed)
                return;
            scope.Closed = true;
            scope.Run.LootBundleScopeTicks += _clock() - scope.Started;
            _bundles.Remove(scope);
            RestoreIfUnused();
        }
        public void LeaveMap(MapScope scope)
        {
            if (scope != null)
                Dispatch(() => CloseMap(scope));
        }
        private void CloseMap(MapScope scope)
        {
            if (scope.Closed)
                return;
            scope.Closed = true;
            scope.Run.Maps--;
            _maps.Remove(scope);
            _frameCap?.Leave(scope.FrameCap, Elapsed(scope.Run));
            RestoreIfUnused();
        }
        private void RestoreIfUnused()
        {
            if (_ownsPriority && !_maps.Exists(m => m.OwnsPriority) && !_bundles.Exists(b => b.OwnsPriority))
                RestorePriority();
        }
        private void RestorePriority()
        {
            // Do not overwrite another owner's newer global priority setting.
            if (_readPriority() == _appliedPriority)
                _writePriority(_priorPriority);
            _ownsPriority = false;
        }
        public void CountNonCollectingCall()
        {
            LoadingRun run = _current;
            if (run != null)
                Interlocked.Increment(ref run.NonCollectingCalls);
        }
        private void Dispatch(Action action)
        {
            if (Environment.CurrentManagedThreadId == _mainThread)
                action();
            else if (!_stopped)
                _pending.Enqueue(action);
        }
        public void Pump()
        {
            while (_pending.TryDequeue(out Action action))
                action();
            LoadingRun run = _current;
            if (run != null && (_clock() - run.Started) / (double)_frequency >= 600)
                End(run, "timeout");
        }
        private void End(LoadingRun run, string outcome)
        {
            if (run.Closed)
                return;
            run.Closed = true;
            for (int i = _maps.Count - 1; i >= 0; i--)
            if (_maps[i].Run == run)
                CloseMap(_maps[i]);
            for (int i = _bundles.Count - 1; i >= 0; i--)
            if (_bundles[i].Run == run)
                CloseBundles(_bundles[i]);
            if (ReferenceEquals(_current, run))
                _current = null;
            if (!run.Options.DebugReports)
                return;
            // The plugin buffers this small report until a later Update outside a loading run.
            _report($"{DateTime.UtcNow:O} | {outcome} | {run.Options} | NonCollectingCalls={run.NonCollectingCalls}; SpawnZoneScopes={run.SpawnZoneScopes}; SpawnZoneHits={run.SpawnZoneHits}; SpawnZoneNativeCalls={run.SpawnZoneNativeCalls}; SpawnZoneInvalidations={run.SpawnZoneInvalidations}; SceneProbeRequests={run.SceneProbeRequests}; SceneProbeSkipped={run.SceneProbeSkipped}; SceneProbeNative={run.SceneProbeNative}; SceneProbeGuardMs={(run.SceneProbeGuardTicks * 1000.0 / Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; SceneProbeFallbacks[Shape,BuiltIn,Location,Manifest,Registered,ExistingOperation,File,ObserverError]={string.Join(",", run.SceneProbeFallbacks)}; SceneProbeFirstError={run.SceneProbeFirstError ?? "none"}; LootBundleScopes={run.LootBundleScopes}; LootBundlePriorityScopes={run.LootBundlePriorityScopes}; LootBundleScopeMs={(run.LootBundleScopeTicks * 1000.0 / _frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}; BotPreloadDelaysReduced={run.BotPreloadDelaysReduced}; BotPreloadDelayNominalSavedMs={run.BotPreloadDelayNominalSavedMs}; FrameCapRecords={run.FrameCaps.Count}; DroppedFrameCapRecords={run.DroppedFrameCaps}; SceneScheduleRecords={run.Schedules.Count}; DroppedSceneScheduleRecords={run.DroppedSchedules}", run);
        }
        public void Stop()
        {
            _stopped = true;
            if (_current != null)
                End(_current, "plugin-stopped");
            while (_pending.TryDequeue(out _))
            { }
            if (_ownsPriority)
                RestorePriority();
        }
    }
}
