using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine.LowLevel;

internal static class HideoutResearchValidation
{
    internal static void Run(Action<bool, string> check, string project)
    {
        string directory = Path.Combine(project, "hideout-research-validation", Guid.NewGuid().ToString("N"));
        long tick = 0;
        var capture = new HideoutResearchCapture(Path.Combine(directory, "lifecycle"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        var entry = capture.Begin("entry", true);
        var pending = capture.Begin("pending");
        var source = new TaskCompletionSource<bool>();
        pending.Observe(source.Task);
        capture.Pulse(); capture.Sample("1");
        Interlocked.Exchange(ref tick, 5000);
        string heartbeatPath = Path.Combine(capture.DirectoryPath, "heartbeat.csv");
        Wait(() => File.Exists(heartbeatPath) && ReadLive(heartbeatPath).Contains("5000,0,5000"));
        check(ReadLive(heartbeatPath).Contains("pending"), "Writer flushes heartbeat and open stage while main-thread pump is absent");
        source.SetResult(true); pending.Finish("duplicate"); entry.Finish("completed");
        capture.Close("fixture-stop"); check(capture.Join(5000), "Research writer closes within fixture deadline");
        string events = File.ReadAllText(Path.Combine(capture.DirectoryPath, "events.csv"));
        check(events.Split('\n').Count(line => line.Contains("\"pending\",end,")) == 1 && !events.Contains("duplicate"), "Task observer completion is idempotent");
        check(events.Contains("launch-return") && events.Contains("fixture-stop"), "Research keeps task kickoff separate from lifetime and writes terminal reason");
        check(File.ReadAllText(Path.Combine(capture.DirectoryPath, "samples.csv")).Contains("0,1"), "Main-thread snapshot is flushed independently of final export");
        capture.Note("late", "ignored"); check(capture.Begin("late") == null, "Closed capture rejects late spans and events");

        tick = 0;
        var timeout = new HideoutResearchCapture(Path.Combine(directory, "timeout"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        timeout.Begin("hung-entry", true);
        Interlocked.Exchange(ref tick, 600001); timeout.Note("wake", "timeout fixture");
        check(timeout.Join(5000), "Writer enforces capture timeout without Unity Update");
        check(File.ReadAllText(Path.Combine(timeout.DirectoryPath, "events.csv")).Contains("incomplete-at-timeout-600s"), "Timeout marks open spans incomplete instead of completed");

        tick = 0;
        var tail = new HideoutResearchCapture(Path.Combine(directory, "tail"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        var first = tail.Begin("entry", true); first.Finish("completed");
        Interlocked.Exchange(ref tick, 9000);
        var second = tail.Begin("entry", true); second.Finish("canceled");
        Interlocked.Exchange(ref tick, 10001); tail.Note("wake", "first entry tail expired");
        Thread.Sleep(300);
        check(!tail.Closed, "A repeated hideout entry extends the observation tail");
        Interlocked.Exchange(ref tick, 19001); tail.Note("wake", "second entry tail expired");
        check(tail.Join(5000), "Capture ends ten seconds after the last entry completes");
        tick = 0;
        var iconTail = new HideoutResearchCapture(Path.Combine(directory, "icon-tail"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        iconTail.Begin("entry", true).Finish("completed"); check(iconTail.EnterIcon(), "Active icon rendering extends capture lifetime");
        Interlocked.Exchange(ref tick, 10001); iconTail.Note("wake", "entry tail elapsed during icon rendering"); Thread.Sleep(300);
        check(!iconTail.Closed, "Entry tail does not truncate pending icon timeout/cleanup observations");
        iconTail.ExitIcon(); Interlocked.Exchange(ref tick, 20002); iconTail.Note("wake", "icon cleanup tail elapsed");
        check(iconTail.Join(5000), "Icon cleanup retains a bounded ten-second post-entry observation tail");

        tick = 0;
        var decalTail = new HideoutResearchCapture(Path.Combine(directory, "decal-tail"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        decalTail.Begin("entry", true).Finish("completed");
        Interlocked.Exchange(ref tick, 9000); decalTail.DecalActivity();
        Interlocked.Exchange(ref tick, 11000); decalTail.Note("wake", "raid camera still rendering"); Thread.Sleep(300);
        check(!decalTail.Closed, "Raid decal activity keeps entry capture open without icon activity");
        Interlocked.Exchange(ref tick, 19001); decalTail.Note("wake", "camera tail finished");
        check(decalTail.Join(5000), "Capture closes after decal activity ends and the bounded observation tail expires");

        var bindingBound = new HideoutResearchCapture(Path.Combine(directory, "decal-bound"), "fixture", "elapsed_ms,value");
        for (int n = 0; n <= HideoutResearchCapture.MaxDecalBindings; n++) bindingBound.DecalRow(true, "fixture");
        bindingBound.Close("bounds"); check(bindingBound.Join(5000), "Bounded decal binding writer drains");
        check(File.ReadAllLines(Path.Combine(bindingBound.DirectoryPath, "decal-bindings.csv")).Length == HideoutResearchCapture.MaxDecalBindings + 1 &&
            File.ReadAllText(Path.Combine(bindingBound.DirectoryPath, "events.csv")).Contains("dropped_rows=1"), "Excess decal binding rows are rejected and counted");

        tick = 0;
        var preload = new HideoutResearchCapture(Path.Combine(directory, "preload"), "fixture", "elapsed_ms,value", () => Interlocked.Read(ref tick));
        preload.Begin("preload").Finish("completed");
        Interlocked.Exchange(ref tick, 30001); preload.Note("wake", "preload idle");
        check(preload.Join(5000) && File.ReadAllText(Path.Combine(preload.DirectoryPath, "events.csv")).Contains("preload-idle"), "Background-only preloading has its own bounded capture");

        var bounded = new HideoutResearchCapture(Path.Combine(directory, "bounds"), "fixture", "elapsed_ms,value");
        for (int i = 0; i < HideoutResearchCapture.MaxSpans; i++) bounded.Begin("bounded").Finish("completed");
        check(bounded.Begin("over-cap") == null, "High-frequency probes have a fixed span cap");
        for (int i = 0; i < HideoutResearchCapture.MaxSamples + 10; i++) bounded.Sample("1");
        bounded.Close("bounds"); check(bounded.Join(5000), "Bounded capture writer drains successfully");
        check(File.ReadAllLines(Path.Combine(bounded.DirectoryPath, "samples.csv")).Length == HideoutResearchCapture.MaxSamples + 1, "Snapshot count remains bounded");
        check(File.ReadAllText(Path.Combine(bounded.DirectoryPath, "heartbeat.csv")).Contains(",11,"), "Dropped spans and samples remain visible");

        string blockedPath = Path.Combine(directory, "file-instead-of-directory"); File.WriteAllText(blockedPath, "fixture");
        var failed = new HideoutResearchCapture(blockedPath, "fixture", "elapsed_ms,value");
        check(failed.Join(5000) && failed.Closed && failed.Error != null, "Writer I/O failure closes diagnostics without throwing on the producer");

        var nativeLoop = new PlayerLoopSystem { type = typeof(UnityEngine.PlayerLoop.EarlyUpdate.UpdateTextureStreamingManager) };
        check(HideoutResearch.StreamingManager(new PlayerLoopSystem { subSystemList = new[] { nativeLoop } }) == "native", "Snapshot recognizes native streaming player-loop entry");
        check(HideoutResearch.StreamingManager(new PlayerLoopSystem { type = typeof(CustomUpdateTextureStreamingManager) }) == "replaced", "Snapshot recognizes EFT's temporary streaming replacement");
        check(HideoutResearch.StreamingManager(new PlayerLoopSystem()) == "absent", "Missing streaming manager is distinguished from native");
    }

    internal static void Installed(Action<bool, string> check, Func<string, Type> find, string project)
    {
        var specs = HideoutResearch.Resolve(find);
        check(specs.Count == 40 && specs.Select(s => s.Method).Distinct().Count() == specs.Count, "All hideout/icon research probes resolve uniquely against installed EFT");
        check(specs.Where(s => s.Stage == "icons.item-render" || s.Stage == "icons.player-render").All(s => s.Start), "Both item and player icon renders can start a capture in raid after earlier captures closed");
        var harmony = new Harmony("ftls.hideout.research.installed");
        try
        {
            HideoutResearch.Install(harmony, specs);
            foreach (var spec in specs)
                check(Harmony.GetPatchInfo(spec.Method).Owners.Contains(harmony.Id), "Installed research hook compiles: " + spec.Stage);
            MethodInfo worker = AccessTools.DeclaredMethod(find("TextureQuadTree"), "WorkThread", new[] { typeof(object) });
            check(Harmony.GetPatchInfo(worker).Owners.Contains(harmony.Id), "Actual installed texture-worker lock wrappers and lifecycle hooks compile");
            var nativeWorker = PatchProcessor.GetOriginalInstructions(worker);
            var tracedWorker = HideoutDecalProbes.WorkerTranspiler(nativeWorker).ToList();
            MethodInfo sleep = AccessTools.Method(typeof(Thread), nameof(Thread.Sleep), new[] { typeof(int) });
            check(tracedWorker.Count(i => i.Calls(sleep)) == 3, "Worker observer preserves all three native sleeps and their durations");
            MethodInfo exit = AccessTools.Method(typeof(Monitor), nameof(Monitor.Exit), new[] { typeof(object) });
            check(tracedWorker.Count(i => i.Calls(exit)) == 1, "Worker observer leaves the separate update-parameter lock exit untouched");
            check(tracedWorker.Count(i => i.Calls(AccessTools.Method(typeof(HideoutDecalProbes), nameof(HideoutDecalProbes.WorkerEnter)))) == 1 &&
                tracedWorker.Count(i => i.Calls(AccessTools.Method(typeof(HideoutDecalProbes), nameof(HideoutDecalProbes.WorkerExit)))) == 1, "Exactly one worker global-lock pair is observed");
            var tracedDispose = HideoutDecalProbes.DisposeTranspiler(PatchProcessor.GetOriginalInstructions(AccessTools.DeclaredMethod(find("TextureQuadTree"), "Dispose", Type.EmptyTypes))).ToList();
            check(tracedDispose.Count(i => i.Calls(AccessTools.Method(typeof(HideoutDecalProbes), nameof(HideoutDecalProbes.DisposeEnter)))) == 1 &&
                tracedDispose.Count(i => i.Calls(AccessTools.Method(typeof(HideoutDecalProbes), nameof(HideoutDecalProbes.DisposeJoin)))) == 1, "Exactly one disposal lock acquisition and worker join are observed");
            foreach (bool disposal in new[] { true, false })
            {
                try
                {
                    var invalid = new[] { new CodeInstruction(OpCodes.Ret) };
                    (disposal ? HideoutDecalProbes.DisposeTranspiler(invalid) : HideoutDecalProbes.WorkerTranspiler(invalid)).ToList();
                    check(false, "Invalid call-site shape must be rejected");
                }
                catch (InvalidOperationException) { check(true, "Changed lock/join ABI refuses incomplete observer installation"); }
            }
        }
        finally { harmony.UnpatchSelf(); }

        var fixtureSpecs = new System.Collections.Generic.List<HideoutResearchHook> {
            new HideoutResearchHook { Method = typeof(HideoutResearchValidation).GetMethod(nameof(TaskFixture), BindingFlags.NonPublic | BindingFlags.Static), Stage = "fixture.entry", Start = true, Entry = true },
            new HideoutResearchHook { Method = typeof(HideoutResearchValidation).GetMethod(nameof(ThrowFixture), BindingFlags.NonPublic | BindingFlags.Static), Stage = "fixture.throw", Start = true, Entry = true }
        };
        string output = Path.Combine(project, "hideout-research-validation", "hook-" + Guid.NewGuid().ToString("N"));
        var research = new HideoutResearch(output, "fixture", find, _ => { }, fixtureSpecs);
        try
        {
            HideoutResearch.Install(harmony, fixtureSpecs);
            var pending = new TaskCompletionSource<bool>();
            check(ReferenceEquals(TaskFixture(pending.Task), pending.Task), "Inactive research preserves original generic Task identity");
            HideoutResearch.Active = research;
            check(ReferenceEquals(TaskFixture(pending.Task), pending.Task), "Active research preserves original generic Task identity");
            pending.SetResult(true);
            var canceled = Task.FromCanceled<bool>(new CancellationToken(true));
            check(ReferenceEquals(TaskFixture(canceled), canceled) && canceled.IsCanceled, "Active observer preserves canceled Task identity and status");
            var faulted = Task.FromException<bool>(new InvalidOperationException("native fault"));
            check(ReferenceEquals(TaskFixture(faulted), faulted), "Active observer preserves faulted Task identity");
            try { faulted.GetAwaiter().GetResult(); check(false, "Native fault must reach awaiter"); }
            catch (InvalidOperationException error) { check(error.Message == "native fault", "Original Task failure reaches native awaiter"); }
            try { ThrowFixture(); check(false, "Native synchronous exception must reach caller"); }
            catch (InvalidOperationException error) { check(error.Message == "native throw", "Research finalizer preserves synchronous exceptions"); }
            TestWorker(check, research, output);
        }
        finally { HideoutResearch.Active = null; research.Stop(); harmony.UnpatchSelf(); }
        string generated = Directory.GetDirectories(output).Single();
        string eventPath = Path.Combine(generated, "events.csv");
        Wait(() => ReadLive(eventPath).Contains("plugin-stopped"));
        check(!ReadLive(eventPath).Contains("sample-error"), "Actual Unity snapshot and cached EFT field reads succeed in the installed-hook fixture");
        string[] sampleLines = ReadLive(Path.Combine(generated, "samples.csv")).Trim().Split('\n');
        check(sampleLines.Length >= 2 && sampleLines[0].Split(',').Length == sampleLines[1].Split(',').Length, "Live snapshot values match the declared CSV schema");
        string tracedEvents = ReadLive(eventPath);
        check(tracedEvents.Contains("decals.texture-tree.dispose.lock-wait") && tracedEvents.Contains("\"acquired\"") &&
            tracedEvents.Contains("decals.texture-tree.dispose.join") && tracedEvents.Contains("\"joined\""), "Capture contains exact native lock-wait and join outcomes");

        string rearmOutput = Path.Combine(project, "hideout-research-validation", "icon-rearm-" + Guid.NewGuid().ToString("N"));
        var rearmed = new HideoutResearch(rearmOutput, "fixture", find, _ => { }, fixtureSpecs);
        try
        {
            var first = rearmed.IconCapture();
            check(first != null, "Icon work starts research without hideout entry");
            first.Close("fixture-idle");
            check(first.Join(5000), "Earlier icon capture finishes writing");
            var second = rearmed.IconCapture();
            check(second != null && !ReferenceEquals(first, second), "Later raid icon work rearms a fresh capture after the earlier capture closes");
        }
        finally { rearmed.Stop(); }
    }

    private static void TestWorker(Action<bool, string> check, HideoutResearch research, string output)
    {
        object sync = new object();
        using (var held = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var attempted = new ManualResetEventSlim())
        using (var acquired = new ManualResetEventSlim())
        {
            var worker = new Thread(() =>
            {
                HideoutDecalProbes.WorkerPrefix();
                HideoutDecalProbes.WorkerEnter(sync);
                try { held.Set(); release.Wait(10000); }
                finally { HideoutDecalProbes.WorkerExit(sync); HideoutDecalProbes.WorkerFinalizer(null); }
            }) { IsBackground = true };
            var disposer = new Thread(() =>
            {
                attempted.Set(); HideoutDecalProbes.DisposeEnter(sync);
                try { acquired.Set(); } finally { Monitor.Exit(sync); }
            }) { IsBackground = true };
            worker.Start(); check(held.Wait(5000), "Worker observer forwards native monitor acquisition");
            disposer.Start(); check(attempted.Wait(5000), "Disposal fixture attempts the held lock");
            try
            {
                Thread.Sleep(100); check(!acquired.IsSet, "Observed disposal still blocks on the original lock");
                string workerFile = Path.Combine(Directory.GetDirectories(output).Single(), "worker.csv");
                Wait(() => File.Exists(workerFile) && ReadLive(workerFile).Contains("holding-tree-lock") && ReadLive(workerFile).Contains("WaitSleepJoin"));
                check(ReadLive(workerFile).Contains(",true,"), "Writer records sleeping worker and held tree lock without taking the game lock or pumping Unity");
            }
            finally { release.Set(); }
            check(disposer.Join(5000) && worker.Join(5000) && acquired.IsSet, "Original monitor ownership is retained after lock observation");
            check(research.Decals.Snapshot().Contains("\"exited\"") && research.Decals.Snapshot().Contains(",false,"), "Worker exit clears held-lock state and retains hold-duration statistics");
        }
        var joined = new Thread(() => Thread.Sleep(100)) { IsBackground = true };
        joined.Start(); HideoutDecalProbes.DisposeJoin(joined);
        check(!joined.IsAlive, "Observed native Thread.Join waits for the original worker to exit");
        try { HideoutDecalProbes.DisposeEnter(null); check(false, "Null native monitor must throw"); }
        catch (ArgumentNullException) { check(true, "Lock observer preserves native acquisition exceptions"); }
        try { HideoutDecalProbes.DisposeJoin(null); check(false, "Null native thread must throw"); }
        catch (NullReferenceException) { check(true, "Join observer preserves native invocation exceptions"); }
        HideoutResearch.Active = null;
        try
        {
            HideoutDecalProbes.DisposeEnter(sync);
            check(Monitor.IsEntered(sync), "Inactive observer still forwards native monitor ownership");
            Monitor.Exit(sync);
        }
        finally { HideoutResearch.Active = research; }
    }

    private static void Wait(Func<bool> predicate)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate()) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Research writer fixture"); Thread.Sleep(10); }
    }
    private static string ReadLive(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
    }
    private sealed class CustomUpdateTextureStreamingManager { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task<bool> TaskFixture(Task<bool> task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowFixture() => throw new InvalidOperationException("native throw");
}
