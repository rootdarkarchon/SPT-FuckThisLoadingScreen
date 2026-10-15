using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;
using Mono.Cecil;
using UnityEditor;

public static class FTLSValidation
{
    private static int checks, priority, nativeCalls, explicitCollections, iteration;
    private static long tick;
    private static Options options;
    private static readonly List<string> reports = new List<string>();
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }
    private static Options Enabled() => new Options { Enabled = true, AudioBatching = true, AudioBatchSize = 1200,
        NonCollectingMeasurements = true, Priority = LoadingPriority.High, ParallelScenes = true, ScopedSpawnZones = true, SkipBuiltInSceneProbes = true, LootBundlePriority = true };
    private static LoadingPolicy NewPolicy()
    {
        options = Enabled(); priority = 4; tick = 0;
        int main = Environment.CurrentManagedThreadId;
        return new LoadingPolicy(() => options, () => priority, value => {
            Check(Environment.CurrentManagedThreadId == main, "Unity priority write must stay on main thread"); priority = value;
        }, mode => mode == LoadingPriority.High ? 50 : 10, (text, run) => reports.Add(text), () => tick, 1000);
    }

    public static void Run()
    {
        string project = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        try
        {
            TestPolicy(); TestHooks(); TestLootPriority(); TestFrameCap(); BoundedSceneValidation.Run(Check); TestSpawnZones(); TestSceneProbes(project); TestInstalled(Path.Combine(project, "install.txt"));
            File.WriteAllText(Path.Combine(project, "result.txt"), $"PASS: {checks} loading scope, task/scene scheduling, transpiler and installed-ABI checks. No live loading-speed acceptance claimed.\n");
            UnityEngine.Debug.Log($"PASS: {checks} FTLS checks");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(project, "result.txt"), "FAIL: " + error);
            UnityEngine.Debug.LogException(error); EditorApplication.Exit(1);
        }
    }

    private static void TestFrameCap()
    {
        int target = 60, sync = 1;
        var cap = new LoadingFrameCap(() => target, value => target = value, () => sync, value => sync = value);
        var run = new LoadingRun();
        FrameCapLease first = cap.Enter(run, 240, 5), second = cap.Enter(run, 240, 6);
        Check(target == 240 && sync == 0 && run.FrameCaps.Count == 1 && run.FrameCaps[0].Leases == 2, "Nested frame-cap leases share one snapshot");
        cap.Leave(first, 10); Check(target == 240 && sync == 0, "Nested completion retains frame override");
        cap.Leave(second, 15); Check(target == 60 && sync == 1 && run.FrameCaps[0].Outcome == "restored", "Last lease restores target and VSync");
        cap.Leave(second, 20); Check(run.FrameCaps[0].EndMs == 15, "Late frame-cap completion is idempotent");
        first = cap.Enter(run, 240, 25); target = 144; sync = 2; cap.Leave(first, 30);
        Check(target == 144 && sync == 2 && run.FrameCaps[1].Outcome == "external-change-preserved", "Frame override respects newer external settings");
        bool failTarget = true; target = 60; sync = 1;
        var failure = new LoadingFrameCap(() => target, value => { target = value; if (failTarget && value == 240) throw new InvalidOperationException("setter failed"); }, () => sync, value => sync = value);
        Check(failure.Enter(run, 240, 35) == null && target == 60 && sync == 1 && run.FrameCaps[2].Outcome == "apply-failed", "Partially applied frame override rolls back without breaking loading");
        failTarget = false;
        LoadingPolicy Make(bool enabled = true)
        {
            options = Enabled(); options.FrameCapOverride = enabled; priority = 4; tick = 0;
            return new LoadingPolicy(() => options, () => priority, value => priority = value, mode => 50,
                (text, finished) => reports.Add(text), () => tick, 1000, cap);
        }
        foreach (string end in new[] { "normal", "failure", "timeout", "shutdown", "game-started" })
        {
            target = 60; sync = 1; var policy = Make(); var owner = policy.Begin(); var map = policy.EnterMap();
            Check(target == 240 && sync == 0, "Map scope acquires opt-in frame override: " + end);
            if (end == "normal") policy.LeaveMap(map);
            else if (end == "failure") policy.RootFinished(owner, true);
            else if (end == "timeout") { tick = 600001; policy.Pump(); }
            else if (end == "shutdown") policy.Stop();
            else { object marker = new object(); policy.AttachMetrics(marker); policy.GameStarted(marker); }
            Check(target == 60 && sync == 1, "Map terminal path restores frame settings: " + end);
            policy.LeaveMap(map); policy.Stop();
        }
        target = 60; sync = 1; var disabled = Make(false); disabled.Begin(); var native = disabled.EnterMap();
        Check(target == 60 && sync == 1, "Frame toggle off preserves native settings"); disabled.LeaveMap(native); disabled.Stop();
        var worker = Make(); worker.Begin(); var workerScope = worker.EnterMap();
        Task.Run(() => worker.LeaveMap(workerScope)).GetAwaiter().GetResult();
        Check(target == 240, "Worker frame completion waits for main-thread dispatch"); worker.Pump();
        Check(target == 60 && sync == 1, "Main-thread pump restores worker-completed frame scope"); worker.Stop();
        var bounded = Make(false); var boundedRun = bounded.Begin(); boundedRun.Options.BoundedScenes = true; var boundedMap = bounded.EnterMap();
        bool parallelFlag = true; PolicyHooks.Policy = bounded; PolicyHooks.PresetPrefix(ref parallelFlag);
        Check(!parallelFlag && bounded.BoundedSceneRun == boundedRun, "Bounded mode takes precedence over older unbounded parallel flag");
        boundedRun.Options.ParallelScenes = false;
        parallelFlag = true; PolicyHooks.PresetPrefix(ref parallelFlag, false);
        Check(parallelFlag, "Preload-only calls preserve original parallel selection when bounded mode declines");
        parallelFlag = false; PolicyHooks.PresetPrefix(ref parallelFlag, false);
        Check(!parallelFlag, "Preload-only calls preserve original sequential selection");
        for (int i = 0; i < 129; i++) { var done = bounded.BeginSceneReport(); done?.Invoke(new BoundedSceneStats { Requested = 5, Submitted = 5, Completed = 5, Peak = 2, Window = 2, Outcome = "completed" }); }
        Check(boundedRun.Schedules.Count == 128 && boundedRun.DroppedSchedules == 1, "Scene scheduling telemetry has an independent bounded record cap");
        Check(ExperimentReports.Schedules(boundedRun).Contains("peak_in_flight") && ExperimentReports.FrameCaps(run).Contains("external-change-preserved"), "Experiment CSVs retain scheduling and restoration outcomes");
        Func<bool> ownedActivity = bounded.CaptureSceneActivity();
        bounded.RootFinished(boundedRun, false); LoadingRun retry = bounded.Begin(); retry.Options.BoundedScenes = true; var retryMap = bounded.EnterMap();
        Check(!ownedActivity() && bounded.BoundedSceneRun == retry, "Superseded attempt cannot borrow the retry's active scheduling scope");
        bounded.LeaveMap(boundedMap); bounded.LeaveMap(retryMap); bounded.Stop(); PolicyHooks.Policy = null;
    }

    private static void TestLootPriority()
    {
        var harmony = new Harmony("ftls.loading.lootpriority");
        try
        {
            Patch(harmony, nameof(LootBundlesTask), nameof(PolicyHooks.LootBundlesPrefix), nameof(PolicyHooks.LootBundlesPostfix), nameof(PolicyHooks.LootBundlesFinalizer));
            Patch(harmony, nameof(ThrowingLootBundles), nameof(PolicyHooks.LootBundlesPrefix), nameof(PolicyHooks.LootBundlesPostfix), nameof(PolicyHooks.LootBundlesFinalizer));
            Patch(harmony, nameof(LootLaunch), nameof(PolicyHooks.LootLaunchPrefix), nameof(PolicyHooks.LootLaunchPostfix), nameof(PolicyHooks.LootLaunchFinalizer));
            Patch(harmony, nameof(ThrowingLootLaunch), nameof(PolicyHooks.LootLaunchPrefix), nameof(PolicyHooks.LootLaunchPostfix), nameof(PolicyHooks.LootLaunchFinalizer));
            PolicyHooks.Policy = NewPolicy();
            var source = new TaskCompletionSource<int>();
            Check(ReferenceEquals(LootLaunch(source.Task), source.Task) && priority == 4, "Standalone loot launch retains native priority and Task");
            LoadingRun run = PolicyHooks.Policy.Begin();
            Check(ReferenceEquals(LootBundlesTask(source.Task), source.Task) && priority == 4 && run.LootBundleScopes == 0, "Unrelated resource loads outside SpawnLoot retain native priority");
            Check(ReferenceEquals(LootLaunch(source.Task), source.Task) && priority == 50 && PolicyHooks.LootLaunchDepth == 0, "Loot kickoff extends High through original awaited Task without retaining thread-local launch state");
            Check(run.LootBundleScopes == 1 && run.Maps == 0 && PolicyHooks.Policy.AudioBatchSize == 300 && !PolicyHooks.Policy.ParallelScenes && PolicyHooks.Policy.SceneProbeRun == null,
                "Loot priority does not expand map-only audio, parallel or scene-probe scopes");
            tick = 2000; Task.Run(() => source.SetResult(1)).GetAwaiter().GetResult();
            Check(priority == 50, "Worker completion queues Unity priority restoration"); PolicyHooks.Policy.Pump();
            Check(priority == 4 && run.LootBundlePriorityScopes == 1 && run.LootBundleScopeTicks == 2000, "Completed loot Task restores priority and reports duration");
            Task failed = Task.FromException(new InvalidOperationException("loot failed"));
            Check(ReferenceEquals(LootLaunch(failed), failed) && priority == 4, "Already-faulted loot Task retains identity and restores priority");
            try { failed.GetAwaiter().GetResult(); throw new Exception("Loot error suppressed"); } catch (InvalidOperationException error) { Check(error.Message == "loot failed", "Original fault remains visible"); }
            Task canceled = Task.FromCanceled(new CancellationToken(true));
            Check(ReferenceEquals(LootLaunch(canceled), canceled) && priority == 4, "Already-canceled loot Task restores priority");
            try { ThrowingLootLaunch(); throw new Exception("Launch error suppressed"); } catch (InvalidOperationException) { checks++; }
            Check(priority == 4 && PolicyHooks.LootLaunchDepth == 0, "Synchronous bundle/launch errors close both scopes");
            MapScope map = PolicyHooks.Policy.EnterMap(); BundleScope bundle = PolicyHooks.Policy.EnterLootBundles();
            PolicyHooks.Policy.LeaveMap(map); Check(priority == 50, "Overlapping loot scope retains priority after map closes");
            PolicyHooks.Policy.LeaveLootBundles(bundle); Check(priority == 4, "Last priority owner restores original value");
            bundle = PolicyHooks.Policy.EnterLootBundles(); map = PolicyHooks.Policy.EnterMap();
            PolicyHooks.Policy.LeaveLootBundles(bundle); Check(priority == 50, "Overlapping map scope retains priority after loot closes");
            PolicyHooks.Policy.LeaveMap(map); Check(priority == 4, "Overlap restores only after both owners close");
            bundle = PolicyHooks.Policy.EnterLootBundles(); priority = 123; PolicyHooks.Policy.LeaveLootBundles(bundle);
            Check(priority == 123, "Another owner's newer global priority remains intact");
            PolicyHooks.Policy.Stop();
            PolicyHooks.Policy = NewPolicy(); options.LootBundlePriority = false; run = PolicyHooks.Policy.Begin();
            LootLaunch(Task.CompletedTask); Check(priority == 4 && run.LootBundleScopes == 0, "Independent loot-priority comparison toggle");
            PolicyHooks.Policy.Stop();
            PolicyHooks.Policy = NewPolicy(); options.Priority = LoadingPriority.Native; PolicyHooks.Policy.Begin();
            LootLaunch(Task.CompletedTask); Check(priority == 4, "Native priority mode applies to loot too"); PolicyHooks.Policy.Stop();
            foreach (string end in new[] { "failure", "timeout", "shutdown", "game-started" })
            {
                PolicyHooks.Policy = NewPolicy(); run = PolicyHooks.Policy.Begin(); var pending = new TaskCompletionSource<int>(); LootLaunch(pending.Task);
                if (end == "failure") PolicyHooks.Policy.RootFinished(run, true);
                else if (end == "timeout") { tick = 600001; PolicyHooks.Policy.Pump(); }
                else if (end == "shutdown") PolicyHooks.Policy.Stop();
                else { object metrics = new object(); PolicyHooks.Policy.AttachMetrics(metrics); PolicyHooks.Policy.GameStarted(metrics); }
                Check(priority == 4 && run.Closed, "Terminal cleanup releases pending loot priority: " + end);
                pending.SetResult(1); PolicyHooks.Policy.Pump(); Check(priority == 4, "Late completion is idempotent: " + end); PolicyHooks.Policy.Stop();
            }
        }
        finally { harmony.UnpatchSelf(); PolicyHooks.Policy?.Stop(); PolicyHooks.Policy = null; }
    }

    private sealed class ProbeManager
    {
        internal int Calls;
        internal bool LogErrors;
        internal readonly Hashtable Manifest = new Hashtable(), Mods = new Hashtable(), Loaded = new Hashtable(), Pending = new Hashtable();
        internal string Url;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Comfort.Common.IOperation<object> LoadBundleAsync(string bundle, bool logErrors)
        { Calls++; LogErrors = logErrors; return new Comfort.Common.Operation<object>(this); }
    }
    private static void TestSceneProbes(string project)
    {
        string path = "Assets/ProbeScene.unity";
        string directory = Path.GetFullPath(Path.Combine(project, "probe-bundles"));
        Directory.CreateDirectory(directory);
        var manager = new ProbeManager { Url = new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri };
        SceneBundleHooks.ExpectedDirectory = directory;
        SceneBundleHooks.IsBuiltIn = value => value == path;
        SceneBundleHooks.Url = value => ((ProbeManager)value).Url;
        SceneBundleHooks.Manifest = value => ((ProbeManager)value).Manifest;
        SceneBundleHooks.Registered = () => manager.Mods;
        SceneBundleHooks.Loaded = value => ((ProbeManager)value).Loaded;
        SceneBundleHooks.Pending = value => ((ProbeManager)value).Pending;
        SceneBundleHooks.Bind<ProbeManager, object>(typeof(ProbeManager).GetMethod("LoadBundleAsync"));
        PolicyHooks.Policy = NewPolicy(); var run = PolicyHooks.Policy.Begin();
        try
        {
            SceneBundleHooks.Load<ProbeManager, object>(manager, path, false, path);
            Check(manager.Calls == 1 && run.SceneProbeRequests == 0, "Outside map scope keeps native request");
            var map = PolicyHooks.Policy.EnterMap();
            var skipped = SceneBundleHooks.Load<ProbeManager, object>(manager, path, false, path);
            Check(skipped.Completed && skipped.Failed && !skipped.Succeed && manager.Calls == 1 && run.SceneProbeSkipped == 1, "Built-in probe produces explicit completed no-bundle operation");
            Check(manager.Loaded.Count == 0 && manager.Pending.Count == 0, "Skipped probe never changes manager caches");
            ProbeFallback reason;
            Check(!SceneBundleHooks.CanSkip(manager, path, "ProbeScene", out reason) && reason == ProbeFallback.Shape, "Short scene aliases stay native");
            Check(!SceneBundleHooks.CanSkip(manager, "external.bundle", "external.bundle", out reason), "Real bundle names stay native");
            SceneBundleHooks.IsBuiltIn = _ => false;
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason) && reason == ProbeFallback.BuiltIn, "External scene stays native");
            SceneBundleHooks.IsBuiltIn = value => value == path;
            string url = manager.Url; manager.Url = "https://example.invalid/bundles/";
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason) && reason == ProbeFallback.Location, "Remote bundle endpoint stays native");
            manager.Url = new Uri(Path.GetTempPath()).AbsoluteUri;
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason), "Different local directory stays native");
            manager.Url = url.TrimEnd('/');
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason), "Non-directory URL concatenation stays native");
            manager.Url = url + "?test=";
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason), "Query-bearing file URL stays native");
            manager.Url = url;
            foreach (var dictionary in new[] { manager.Manifest, manager.Mods, manager.Loaded, manager.Pending })
            {
                dictionary[path] = new object();
                Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason), "Manifest/mod/loaded/pending entries retain native ownership");
                dictionary.Remove(path);
            }
            string file = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, "existing");
            Check(!SceneBundleHooks.CanSkip(manager, path, path, out reason) && reason == ProbeFallback.File, "Existing file stays native"); File.Delete(file);
            manager.Manifest[path] = new object();
            SceneBundleHooks.Load<ProbeManager, object>(manager, path, true, path);
            Check(manager.Calls == 2 && manager.LogErrors && run.SceneProbeFallbacks[(int)ProbeFallback.Manifest] == 1, "Native fallback forwards original logging argument and records reason");
            manager.Manifest.Clear();
            SceneBundleHooks.Manifest = _ => throw new InvalidOperationException("guard observer");
            SceneBundleHooks.Load<ProbeManager, object>(manager, path, false, path);
            Check(manager.Calls == 3 && run.SceneProbeFallbacks[(int)ProbeFallback.ObserverError] == 1, "Guard errors fail closed to native request");
            SceneBundleHooks.Manifest = value => ((ProbeManager)value).Manifest;
            run.Options.SkipBuiltInSceneProbes = false;
            SceneBundleHooks.Load<ProbeManager, object>(manager, path, false, path);
            Check(manager.Calls == 4 && run.SceneProbeNative == 3, "Comparison toggle preserves native call");
            Check(run.SceneProbeRequests == 4 && run.SceneProbeSkipped == 1, "Probe counters account for every in-scope call");
            PolicyHooks.Policy.LeaveMap(map);
        }
        finally { PolicyHooks.Policy.Stop(); PolicyHooks.Policy = null; SceneBundleHooks.IsBuiltIn = SceneBundleHooks.ExactBuiltIn; }
    }

    private static void TestSpawnZones()
    {
        var harmony = new Harmony("ftls.loading.spawnzones");
        var objects = new List<UnityEngine.GameObject>();
        FTLSValidationZone MakeZone(string name)
        {
            var go = new UnityEngine.GameObject(name); objects.Add(go);
            return go.AddComponent<FTLSValidationZone>();
        }
        var first = MakeZone("duplicate"); var second = MakeZone("duplicate"); var other = MakeZone("other");
        nativeZones = new[] { first, second, other };
        MethodInfo discover = Method(nameof(DiscoverZones)).MakeGenericMethod(typeof(FTLSValidationZone));
        SpawnZoneHooks.NativeFindMethod = discover;
        SpawnZoneHooks.BindNative<FTLSValidationZone>(discover);
        try
        {
            harmony.Patch(Method(nameof(CollectionFixture)), SH(nameof(SpawnZoneHooks.CollectionPrefix), Priority.Last),
                SH(nameof(SpawnZoneHooks.CollectionPostfix), Priority.First),
                finalizer: SH(nameof(SpawnZoneHooks.CollectionFinalizer), Priority.First));
            harmony.Patch(Method(nameof(FixFixture)), SH(nameof(SpawnZoneHooks.FixPrefix), Priority.Last),
                SH(nameof(SpawnZoneHooks.FixPostfix), Priority.First),
                finalizer: SH(nameof(SpawnZoneHooks.FixFinalizer), Priority.First));
            harmony.Patch(Method(nameof(MarkerFixture)), transpiler: new HarmonyMethod(typeof(SpawnZoneHooks), nameof(SpawnZoneHooks.MarkerTranspiler)));
            PolicyHooks.Policy = NewPolicy(); LoadingRun run = PolicyHooks.Policy.Begin(); discoveryCalls = 0;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => {
                for (int i = 0; i < 222; i++) Check(ReferenceEquals(MarkerFixture("duplicate"), first), "Ordered duplicate-name selection preserved");
                first.name = "renamed";
                Check(ReferenceEquals(MarkerFixture("renamed"), first), "Renamed zone matched using its live native object");
                Check(ReferenceEquals(MarkerFixture("duplicate"), second), "Remaining duplicate matched after rename");
                Check(MarkerFixture("missing") == null, "Missing zone keeps native no-match result");
            }));
            Check(discoveryCalls == 0 && run.SpawnZoneHits == 225 && run.SpawnZoneScopes == 1, "One reconciliation avoids all repeated global discoveries");
            Check(SpawnZoneHooks.Lookup == null && SpawnZoneHooks.Reconciliation == null, "Normal completion releases all zone references");
            Check(ReferenceEquals(MarkerFixture("renamed"), first) && discoveryCalls == 1, "Standalone marker creation retains native discovery");
            FixFixture(null, null, nativeZones, () => MarkerFixture("other"));
            Check(discoveryCalls == 2, "FixSpawns outside native collection call retains standalone behavior");

            var outer = nativeZones; var inner = new[] { other, second, first };
            CollectionFixture(() => FixFixture(null, null, outer, () => {
                CollectionFixture(() => FixFixture(null, null, inner, () => Check(ReferenceEquals(SpawnZoneHooks.FindZones<FTLSValidationZone>(), inner), "Nested reconciliation uses its own snapshot")));
                Check(ReferenceEquals(SpawnZoneHooks.FindZones<FTLSValidationZone>(), outer), "Nested completion restores outer snapshot");
                Check(Task.Run(() => SpawnZoneHooks.Lookup == null && PolicyHooks.Policy.SpawnZoneRun == null).GetAwaiter().GetResult(), "Scope is thread-local and reuse is main-thread only");
            }));
            var expected = new InvalidOperationException("spawn fixture fault");
            try { CollectionFixture(() => FixFixture(null, null, nativeZones, () => throw expected)); throw new Exception("Spawn exception suppressed"); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, expected), "Native reconciliation exception identity preserved"); }
            Check(SpawnZoneHooks.Lookup == null && SpawnZoneHooks.Reconciliation == null, "Failure releases nested scopes");

            CollectionFixture(() => FixFixture(null, null, nativeZones, () => {
                first.gameObject.SetActive(false);
                Check(ReferenceEquals(MarkerFixture("duplicate"), second), "Inactive zone invalidates snapshot and uses native result");
                int before = discoveryCalls; MarkerFixture("other");
                Check(discoveryCalls == before + 1, "Invalidated scope stays on native discovery");
            }));
            first.gameObject.SetActive(true);
            Check(run.SpawnZoneInvalidations == 1, "Snapshot invalidation reported once per scope");
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => {
                UnityEngine.Object.DestroyImmediate(first.gameObject);
                Check(ReferenceEquals(MarkerFixture("duplicate"), second), "Destroyed Unity object triggers native discovery");
            }));
            Check(run.SpawnZoneInvalidations == 2, "Destroyed-zone fallback recorded");

            nativeZones = Array.Empty<FTLSValidationZone>();
            int calls = discoveryCalls;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => Check(MarkerFixture("missing") == null, "Empty snapshot retains empty result")));
            Check(discoveryCalls == calls, "Empty native scene zone snapshot is reusable");
            nativeZones = new[] { second, other };
            CollectionFixture(() => FixFixture(null, null, null, () => Check(ReferenceEquals(MarkerFixture("other"), other), "Null snapshot falls back to native lookup")));

            options = Enabled(); options.ScopedSpawnZones = false;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => Check(SpawnZoneHooks.Lookup != null, "Option edits do not change an in-flight run")));
            PolicyHooks.Policy.RootFinished(run, true); run = PolicyHooks.Policy.Begin(); calls = discoveryCalls;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => MarkerFixture("other")));
            Check(discoveryCalls == calls + 1 && run.SpawnZoneScopes == 0 && run.SpawnZoneHits == 0, "Native comparison toggle disables scope on next attempt");
            PolicyHooks.Policy.Stop();
            PolicyHooks.Policy = NewPolicy(); run = PolicyHooks.Policy.Begin();
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => {
                SpawnZoneHooks.Reset(); calls = discoveryCalls; MarkerFixture("other");
                Check(discoveryCalls == calls + 1, "Shutdown reset prevents reuse from an abandoned scope");
            }));
            Check(SpawnZoneHooks.Lookup == null && SpawnZoneHooks.Reconciliation == null, "Late closes do not restore cleared scopes");
            harmony.Patch(Method(nameof(FixFixture)), prefix: new HarmonyMethod(Method(nameof(FixBefore))), postfix: new HarmonyMethod(Method(nameof(FixAfter))));
            calls = discoveryCalls;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => MarkerFixture("other")));
            Check(beforeClean && afterClean && discoveryCalls == calls + 2, "Ordinary external prefix/postfix callbacks stay outside the native lookup scope");
            PolicyHooks.Policy.Stop(); calls = discoveryCalls;
            CollectionFixture(() => FixFixture(null, null, nativeZones, () => MarkerFixture("other")));
            Check(discoveryCalls == calls + 3, "Native discovery outside raid loading, including external callbacks");

            var instruction = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, discover);
            instruction.labels.Add(default(System.Reflection.Emit.Label));
            instruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            var code = SpawnZoneHooks.MarkerTranspiler(new[] { instruction, new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }).ToArray();
            Check(code[0].labels.Count == 1 && code[0].blocks.Count == 1 && code[1].opcode == System.Reflection.Emit.OpCodes.Ret,
                "Lookup call replacement preserves labels, regions and surrounding instructions");
            bool refused = false;
            try { SpawnZoneHooks.MarkerTranspiler(Array.Empty<CodeInstruction>()).ToArray(); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Changed lookup call site fails closed");
        }
        finally
        {
            PolicyHooks.Policy?.Stop(); PolicyHooks.Policy = null; SpawnZoneHooks.Reset(); harmony.UnpatchSelf();
            foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static FTLSValidationZone[] nativeZones;
    private static int discoveryCalls;
    private static bool beforeClean, afterClean;
    private static HarmonyMethod SH(string name, int priority) => new HarmonyMethod(typeof(SpawnZoneHooks), name) { priority = priority };
    private static void FixBefore() { beforeClean = SpawnZoneHooks.Lookup == null; MarkerFixture("other"); }
    private static void FixAfter() { afterClean = SpawnZoneHooks.Lookup == null; MarkerFixture("other"); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static T[] DiscoverZones<T>() where T : UnityEngine.Object
    { discoveryCalls++; return nativeZones.Where(z => z != null && z.gameObject.activeInHierarchy).Cast<T>().ToArray(); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static FTLSValidationZone MarkerFixture(string name) => DiscoverZones<FTLSValidationZone>().FirstOrDefault(zone => zone.name == name);
    [MethodImpl(MethodImplOptions.NoInlining)] private static void CollectionFixture(Action body) => body();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void FixFixture(object logger, object markers, FTLSValidationZone[] zones, Action body) => body();

    private static void TestPolicy()
    {
        LoadingPolicy p = NewPolicy();
        Check(p.AudioBatchSize == 300 && !p.NonCollectingMeasurements && !p.ParallelScenes, "Native outside loading");
        LoadingRun run = p.Begin();
        Check(p.Begin() == null, "Nested root cannot replace owner");
        Check(p.AudioBatchSize == 300 && p.NonCollectingMeasurements, "Audio map scope differs from cleanup root scope");
        MapScope first = p.EnterMap(), second = p.EnterMap();
        Check(priority == 50 && p.AudioBatchSize == 1200 && p.ParallelScenes, "Enabled loading controls");
        options = new Options { Enabled = false };
        Check(p.AudioBatchSize == 1200 && p.NonCollectingMeasurements, "Options frozen for current attempt");
        p.LeaveMap(first); Check(priority == 50, "Nested map retains priority ownership");
        Task.Run(() => p.LeaveMap(second)).GetAwaiter().GetResult();
        Check(priority == 50, "Worker completion waits for main-thread pump");
        p.Pump(); Check(priority == 4 && p.AudioBatchSize == 300, "Last map restores previous priority");
        object marker = new object(); p.AttachMetrics(marker); p.GameStarted(new object());
        Check(p.IsLoading, "Stale metrics ignored");
        p.GameStarted(marker); Check(!p.IsLoading && !p.NonCollectingMeasurements, "Game start ends loading policy");
        run = p.Begin(); first = p.EnterMap();
        Check(priority == 4 && p.AudioBatchSize == 300 && !p.NonCollectingMeasurements, "Master native comparison mode");
        p.RootFinished(run, true); Check(!p.IsLoading && priority == 4, "Failed attempt cleanup");
        p = NewPolicy(); run = p.Begin(); first = p.EnterMap(); priority = 123;
        p.LeaveMap(first); Check(priority == 123, "Another owner's newer setting is preserved"); p.Stop();
        p = NewPolicy(); run = p.Begin(); first = p.EnterMap(); tick = 600001; p.Pump();
        Check(!p.IsLoading && priority == 4 && p.AudioBatchSize == 300, "Timeout restores all controls");
        p = NewPolicy(); run = p.Begin(); first = p.EnterMap(); p.Stop(); p.LeaveMap(first);
        Check(priority == 4 && p.Begin() == null, "Shutdown and late callbacks");
        p = NewPolicy(); run = p.Begin(); p.RootFinished(run, false);
        LoadingRun replacement = p.Begin();
        Check(replacement != null && run.Closed, "Completed pre-start attempt may be superseded"); p.Stop();
        p = NewPolicy(); options.Priority = LoadingPriority.Native; run = p.Begin(); first = p.EnterMap();
        Check(priority == 4, "Native integration priority"); p.Stop();
        p = NewPolicy(); options.Priority = LoadingPriority.Normal; run = p.Begin(); first = p.EnterMap();
        Check(priority == 10, "Normal integration priority"); p.Stop();
    }

    private static void TestHooks()
    {
        var harmony = new Harmony("ftls.loading.validation");
        PolicyHooks.Policy = NewPolicy();
        PolicyHooks.NativeMeasureMethod = typeof(FTLSValidation).GetMethod(nameof(NativeMeasure), BindingFlags.Static | BindingFlags.NonPublic);
        PolicyHooks.NativeMeasure = NativeMeasure;
        try
        {
            Patch(harmony, nameof(RootTask), nameof(PolicyHooks.RootPrefix), nameof(PolicyHooks.RootPostfix), nameof(PolicyHooks.RootFinalizer));
            Patch(harmony, nameof(MapTask), nameof(PolicyHooks.MapPrefix), nameof(PolicyHooks.MapPostfix), nameof(PolicyHooks.MapFinalizer));
            Patch(harmony, nameof(ThrowingMap), nameof(PolicyHooks.MapPrefix), nameof(PolicyHooks.MapPostfix), nameof(PolicyHooks.MapFinalizer));
            Patch(harmony, nameof(ScheduleScenes), nameof(PolicyHooks.SchedulingPrefix), nameof(PolicyHooks.SchedulingPostfix), nameof(PolicyHooks.SchedulingFinalizer));
            Patch(harmony, nameof(ThrowingSchedule), nameof(PolicyHooks.SchedulingPrefix), nameof(PolicyHooks.SchedulingPostfix), nameof(PolicyHooks.SchedulingFinalizer));
            harmony.Patch(Method(nameof(QueueScene)), prefix: H(nameof(PolicyHooks.ScenePrefix)));
            harmony.Patch(Method(nameof(Preset)), prefix: H(nameof(PolicyHooks.PresetPrefix)));
            harmony.Patch(Method(nameof(ReachedBatch)), transpiler: H(nameof(PolicyHooks.AudioTranspiler)));
            harmony.Patch(Method(nameof(CleanupFixture)), transpiler: H(nameof(PolicyHooks.MemoryTranspiler)));
            var root = new TaskCompletionSource<int>(); var map = new TaskCompletionSource<int>();
            Check(ReferenceEquals(root.Task, RootTask(root.Task)), "Root Task identity");
            Check(ReferenceEquals(map.Task, MapTask(map.Task)) && priority == 50, "Map Task identity and entered priority");
            Check(Preset(false), "Parallel preset flag enabled only in map scope");
            Check(!QueueScene(false), "Unrelated held scene keeps native activation");
            var source = new TaskCompletionSource<int>();
            Task<bool> queued = ScheduleScenes(source.Task);
            Check(PolicyHooks.ParallelSchedulingDepth == 0 && !QueueScene(false), "Scheduling guard closes before async wait");
            source.SetResult(1);
            Check(queued.GetAwaiter().GetResult(), "Parallel scenes activate as ready; later/unrelated calls keep native flags");
            try { _ = ThrowingSchedule(); throw new Exception("Schedule exception suppressed"); } catch (InvalidOperationException) { checks++; }
            Check(PolicyHooks.ParallelSchedulingDepth == 0, "Scheduling exception restores guard");
            iteration = 300; Check(!ReachedBatch(), "Audio batch no longer yields at300");
            iteration = 1200; Check(ReachedBatch(), "Audio batch yields at1200");
            nativeCalls = 0; explicitCollections = 0; _ = CleanupFixture();
            Check(nativeCalls == 0 && explicitCollections == 1, "Only diagnostics replaced; explicit cleanup untouched");
            try { _ = ThrowingMap(); throw new Exception("Map exception suppressed"); } catch (InvalidOperationException) { checks++; }
            Check(priority == 50, "Failed nested map does not release outer priority");
            Task.Run(() => map.SetResult(1)).GetAwaiter().GetResult();
            PolicyHooks.Policy.Pump(); Check(priority == 4, "Asynchronous map completion restores priority");
            iteration = 300; Check(ReachedBatch() && !Preset(false), "Audio and parallel controls return native after map");
            root.SetCanceled(); PolicyHooks.Policy.Pump();
            Check(!PolicyHooks.Policy.IsLoading && priority == 4, "Canceled root closes policy");
            nativeCalls = 0; _ = CleanupFixture(); Check(nativeCalls == 2, "Native diagnostic calls restored outside loading");
            PolicyHooks.Policy = NewPolicy(); options.AudioBatching = false; options.ParallelScenes = false; options.NonCollectingMeasurements = false;
            LoadingRun run = PolicyHooks.Policy.Begin(); MapScope scope = PolicyHooks.Policy.EnterMap();
            iteration = 300; Check(ReachedBatch() && !Preset(false), "Independent native audio/parallel controls");
            nativeCalls = 0; _ = CleanupFixture(); Check(nativeCalls == 2, "Independent native memory control");
            PolicyHooks.Policy.Stop();
            PolicyHooks.Policy = NewPolicy(); run = PolicyHooks.Policy.Begin();
            Task canceled = Task.FromCanceled(new CancellationToken(true));
            Check(ReferenceEquals(MapTask(canceled), canceled) && priority == 4, "Canceled map preserves Task and restores priority");
            Task failed = Task.FromException(new InvalidOperationException("expected async failure"));
            Check(ReferenceEquals(MapTask(failed), failed) && priority == 4, "Faulted map preserves Task and restores priority");
            try { failed.GetAwaiter().GetResult(); throw new Exception("Async error suppressed"); } catch (InvalidOperationException) { checks++; }
            PolicyHooks.Policy.Stop();
            // An ABI mismatch must fail closed instead of modifying a different instruction.
            bool refused = false;
            try { PolicyHooks.AudioTranspiler(new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }).ToArray(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "Audio transpiler rejects changed IL");
            refused = false;
            try { PolicyHooks.MemoryTranspiler(new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Ret) }).ToArray(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "Memory transpiler rejects changed IL");
        }
        finally { PolicyHooks.Policy?.Stop(); PolicyHooks.Policy = null; harmony.UnpatchSelf(); }
    }

    private static void TestInstalled(string installFile)
    {
        string gamePath = Path.Combine(File.ReadAllText(installFile).Trim(), "EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll");
        using var module = ModuleDefinition.ReadModule(gamePath);
        Check(module.Mvid == new Guid("cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7"), "Installed EFT MVID");
        TypeDefinition reader = module.GetType("Audio.SpatialSystem.AudioBakeDataReader");
        MethodDefinition getter = reader.Methods.Single(m => m.Name == "get_HasReachedMaxIterationsPerFrame");
        Check(getter.Body.Instructions.Count(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4 && Equals(i.Operand, 300)) == 1,
            "Installed audio divisor guard");
        MethodDefinition collect = module.GetType("InGameMemoryManagement").Methods.Single(m => m.Name == "Collect" && m.Parameters.Count == 5);
        Check(collect.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "GetTotalAllocatedMemoryGB") == 2,
            "Installed diagnostic call count");
        Check(collect.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.GC" && m.Name == "Collect") == 2,
            "Native explicit GC calls remain present");
        TypeDefinition presets = module.GetType("EFT.LoadScenesFromPresetOperation");
        Check(presets.Methods.Count(m => m.Name == "Execute" && m.Parameters.Count == 7 && m.Parameters[3].Name == "loadInParallelMode") == 2, "Both native preset overloads");
        Check(presets.Methods.Any(m => m.Name == "ParallelLoadCoroutine" && m.ReturnType.FullName == "System.Threading.Tasks.Task"), "Native parallel scheduler signature");
        Check(module.GetType("EFT.AssetsManager.AssetsManager").Methods.Any(m => m.Name == "LoadScene" && m.Parameters.Count == 5 && m.Parameters[3].Name == "allowSceneActivation"), "Native scene activation argument");
        TypeDefinition localGame = module.GetType("EFT.BaseLocalGame`1");
        MethodDefinition spawnLoot = localGame.Methods.Single(m => m.Name == "SpawnLoot" && m.Parameters.Count == 1);
        TypeReference spawnState = (TypeReference)spawnLoot.CustomAttributes.Single(a => a.AttributeType.Name == "AsyncStateMachineAttribute").ConstructorArguments[0].Value;
        MethodDefinition spawnStep = spawnState.Resolve().Methods.Single(m => m.Name == "MoveNext");
        Check(spawnStep.Body.Instructions.Count(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "EFT.ObjectsFactory" && m.Name == "LoadBundlesAndCreatePools") == 1,
            "Installed SpawnLoot launches exactly one bundle/pool task");
        MethodDefinition nativeBudget = module.GetType("Diz.DependencyManager.DependencyGraph`1").Methods.Single(m => m.Name == ".cctor");
        Check(nativeBudget.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_S && Convert.ToInt32(i.Operand) == 55), "Native retention budget remains 55");
        TypeDefinition spawnCollection = module.GetType("EFT.Game.Spawning.SpawnPointsCollection");
        MethodDefinition fixSpawns = spawnCollection.Methods.Single(m => m.Name == "FixSpawns" && m.Parameters.Count == 4);
        Check(fixSpawns.Parameters[2].Name == "zones" && fixSpawns.Parameters[2].ParameterType.FullName == "BotZone[]", "Native reconciliation zone snapshot ABI");
        Check(spawnCollection.Methods.Count(m => m.Name == "CreateFromScene" && m.Parameters.Count == 2) == 1, "Native collection scope ABI");
        MethodDefinition markerCreate = module.GetType("EFT.Game.Spawning.SpawnPointMarker").Methods.Single(m => m.Name == "Create" && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.FullName == "UnityEngine.GameObject");
        Check(markerCreate.Body.Instructions.Select(i => i.Operand).OfType<GenericInstanceMethod>().Count(m => m.DeclaringType.FullName == "FindObjectsProxy" && m.Name == "FindUnityObjectsOfType" && m.GenericArguments.Single().FullName == "BotZone") == 1, "Exactly one native marker zone discovery call site");

        // Execute the exact installed getter in Unity/Mono; this type's constructor/getter
        // only use its counter and do not initialize game scenes or audio resources.
        Assembly game = Assembly.LoadFrom(gamePath);
        Assembly.LoadFrom(Path.Combine(File.ReadAllText(installFile).Trim(), "EscapeFromTarkov_Data/Managed/Unity.ScriptableBuildPipeline.dll"));
        Assembly.LoadFrom(Path.Combine(File.ReadAllText(installFile).Trim(), "BepInEx/plugins/spt/spt-custom.dll"));
        string timerPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(installFile), "../../FTLS.LevelStageTimer/plugin/netstandard2.1/FTLS.LevelStageTimer.dll"));
        Assembly timer = Assembly.LoadFrom(timerPath);
        var probeHarmony = new Harmony("ftls.loading.nativeprobe");
        var timerHarmony = new Harmony("ftls.loading.timercomposition");
        try
        {
            Func<string, Type> find = name => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).FirstOrDefault(t => t != null) ?? game.GetType(name, true);
            Type probes = timer.GetType("FTLS.LevelStageTimer.SceneProbes", true);
            probes.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { timerHarmony, find, new List<string>() });
            SceneBundleHooks.Install(probeHarmony, find);
            Check(true, "Exact installed scene coroutine accepts probe transpiler and live dictionary ABI");

            Type recorderType = timer.GetType("FTLS.LevelStageTimer.Recorder", true);
            object recorder = Activator.CreateInstance(recorderType, new object[] { 3, 600.0, 2048, 1200, null, 0L });
            recorderType.GetMethod("Begin").Invoke(recorder, new object[] { "raid", "work", true, -1, 0, null });
            Type accessType = timer.GetType("FTLS.LevelStageTimer.OperationAccess", true);
            object access = Activator.CreateInstance(accessType, new object[] { typeof(Comfort.Common.Operation<object>) });
            Type trackerType = timer.GetType("FTLS.LevelStageTimer.SceneTracker", true);
            object tracker = Activator.CreateInstance(trackerType, new[] { recorder, access });
            var owner = new Comfort.Common.Operation<object>();
            trackerType.GetMethod("Start").Invoke(tracker, new object[] { owner, "Assets/Test.unity", "Assets/Test.unity", "Additive", true });
            trackerType.GetMethod("BeginBundle").Invoke(tracker, new object[] { owner });
            Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(installFile), "../plugin/netstandard2.1/FuckThisLoadingScreen.dll")));
            Assembly.LoadFrom(Path.Combine(File.ReadAllText(installFile).Trim(), "BepInEx/core/BepInEx.dll"));
            plugin.GetType("FuckThisLoadingScreen.Plugin", true).GetMethod("InstallLootBundlePriority", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { probeHarmony, find });
            Check(true, "Actual built loot-priority hooks compile against installed closed game and bundle methods");
            plugin.GetType("FuckThisLoadingScreen.BoundedScenes", true).GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { probeHarmony, find });
            Check(true, "Actual bounded scene adapter binds installed ResourceKey extension, fields and direct scheduler");
            object skippedOperation = Activator.CreateInstance(plugin.GetType("FuckThisLoadingScreen.SceneBundleHooks+SkippedProbe`1", true).MakeGenericType(typeof(object)), true);
            trackerType.GetMethod("ObserveBundle").Invoke(tracker, new[] { skippedOperation, owner });
            object capture = recorderType.GetProperty("ActiveCapture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(recorder);
            string csv = (string)trackerType.GetMethod("Export").Invoke(null, new[] { capture });
            Check(csv.Contains("skipped-built-in-scene"), "Timer recognizes skipped probe separately from failed or loaded bundles");
            trackerType.GetMethod("Stop").Invoke(tracker, null);
        }
        finally { probeHarmony.UnpatchSelf(); timerHarmony.UnpatchSelf(); }
        Type actualReader = game.GetType("Audio.SpatialSystem.AudioBakeDataReader", true);
        object instance = Activator.CreateInstance(actualReader);
        FieldInfo counter = actualReader.GetField("_iterationCount");
        MethodInfo actualGetter = actualReader.GetProperty("HasReachedMaxIterationsPerFrame").GetGetMethod();
        var harmony = new Harmony("ftls.loading.nativegetter");
        try
        {
            PolicyHooks.Policy = NewPolicy(); LoadingRun run = PolicyHooks.Policy.Begin(); MapScope map = PolicyHooks.Policy.EnterMap();
            harmony.Patch(actualGetter, transpiler: H(nameof(PolicyHooks.AudioTranspiler)));
            counter.SetValue(instance, 300); Check(!(bool)actualGetter.Invoke(instance, null), "Installed getter skips native300 boundary");
            counter.SetValue(instance, 1200); Check((bool)actualGetter.Invoke(instance, null), "Installed getter yields at configured boundary");
            PolicyHooks.Policy.LeaveMap(map); counter.SetValue(instance, 300);
            Check((bool)actualGetter.Invoke(instance, null), "Installed getter native outside map scope");
        }
        finally { harmony.UnpatchSelf(); PolicyHooks.Policy?.Stop(); PolicyHooks.Policy = null; }
    }

    private static MethodInfo Method(string name) => typeof(FTLSValidation).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
    private static HarmonyMethod H(string name) => new HarmonyMethod(typeof(PolicyHooks), name);
    private static void Patch(Harmony harmony, string name, string prefix, string postfix, string finalizer) =>
        harmony.Patch(Method(name), H(prefix), H(postfix), finalizer: H(finalizer));
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task RootTask(Task task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task LootLaunch(Task task) => LootBundlesTask(task);
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task LootBundlesTask(Task task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowingLootBundles() => throw new InvalidOperationException("expected synchronous bundle error");
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowingLootLaunch() => ThrowingLootBundles();
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task MapTask(Task task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowingMap() => throw new InvalidOperationException("expected");
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool ReachedBatch() => iteration % 300 == 0;
    [MethodImpl(MethodImplOptions.NoInlining)] private static float NativeMeasure() { nativeCalls++; return 12f; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static float CleanupFixture()
    { float before = NativeMeasure(); explicitCollections++; return before - NativeMeasure(); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool Preset(bool loadInParallelMode, bool allowSceneActivation = true) => loadInParallelMode;
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool QueueScene(bool allowSceneActivation) => allowSceneActivation;
    [MethodImpl(MethodImplOptions.NoInlining)] private static async Task<bool> ScheduleScenes(Task source)
    { bool initial = QueueScene(false); await source.ConfigureAwait(false); return initial && !QueueScene(false); }
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowingSchedule() => throw new InvalidOperationException("expected");
}

public sealed class FTLSValidationZone : UnityEngine.MonoBehaviour { }
