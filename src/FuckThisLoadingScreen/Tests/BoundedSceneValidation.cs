using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEngine.SceneManagement;

internal static class BoundedSceneValidation
{
    private sealed class Progress : IProgress<float>
    {
        internal readonly List<float> Values = new List<float>();
        public void Report(float value) { Values.Add(value); }
    }
    private sealed class ThrowingProgress : IProgress<float>
    { public void Report(float value) { throw new InvalidOperationException("progress fault"); } }

    internal sealed class FakeKey { internal int Index; }
    internal interface IFakeManager { }
    internal sealed class FakeOperation : Operation
    {
        internal void Finish(bool fail = false) { if (fail) Error = "bundle failed"; else Succeed = true; }
    }
    internal sealed class FakeManager : IFakeManager
    {
        internal readonly List<FakeOperation> Operations = new List<FakeOperation>();
        internal readonly List<int> Order = new List<int>();
        internal bool Immediate;
    }
    internal static class FakeExtension
    {
        public static FakeOperation LoadScene(IFakeManager manager, FakeKey key, LoadSceneMode mode,
            bool allowSceneActivation, Action<float> progressCallback)
        {
            if (mode != LoadSceneMode.Additive || !allowSceneActivation || progressCallback != null)
                throw new Exception("As-ready native parameters differ");
            var fake = (FakeManager)manager;
            var operation = new FakeOperation();
            fake.Operations.Add(operation); fake.Order.Add(key.Index);
            if (fake.Immediate) operation.Finish();
            return operation;
        }
    }
    internal sealed class FakePreset
    {
        public IFakeManager _assetsManager;
        public bool _allowSceneActivation;
        public CancellationToken _cancellationToken;
        public List<FakeOperation> _loadSceneOperations = new List<FakeOperation>();
        internal int NativeCalls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task DirectLoadCoroutine(IList<FakeKey> scenes, IProgress<float> progress = null)
        { NativeCalls++; return Task.CompletedTask; }
    }

    public static void Run(Action<bool, string> check)
    {
        SynchronizationContext prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            TestWindow(check);
            TestCancellation(check);
            TestFaults(check);
            TestAdapter(check);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
            BoundedScenes.Enabled = null; BoundedScenes.Window = null; BoundedScenes.BeginReport = null; BoundedScenes.CaptureActive = null;
        }
    }

    private static void Completed(Task task, Action<bool, string> check, string message)
    {
        check(task.IsCompleted, message + " completes without hanging");
        task.GetAwaiter().GetResult();
    }

    private static void TestWindow(Action<bool, string> check)
    {
        foreach (int window in new[] { 1, 2, 4 })
        {
            var sources = new Dictionary<int, TaskCompletionSource<bool>>();
            var order = new List<int>();
            var progress = new Progress();
            var stats = new BoundedSceneStats();
            int reports = 0;
            Task run = BoundedScenes.Run(8, window, CancellationToken.None, () => true,
                i => { order.Add(i); var source = new TaskCompletionSource<bool>(); sources.Add(i, source); return source.Task; },
                progress, stats, result => { check(ReferenceEquals(result, stats), "Same bounded stats reported"); reports++; });
            check(order.Count == window && order[0] == 1, "First scene excluded; bounded initial submission");
            if (window > 1)
            {
                sources[2].SetResult(true);
                check(sources.ContainsKey(window + 1), "Window replenishes when any scene finishes, without waiting for first");
            }
            while (!run.IsCompleted)
            {
                int before = stats.Completed;
                foreach (int index in new List<int>(sources.Keys))
                    sources[index].TrySetResult(true);
                check(stats.Completed > before || run.IsCompleted, "Controlled bounded schedule makes progress");
            }
            Completed(run, check, "Bounded window " + window);
            check(stats.Peak <= window && stats.Peak == window, "No more than configured window admitted");
            check(stats.Submitted == 7 && stats.Completed == 7 && stats.Requested == 7, "Exactly all additional scenes finish");
            check(order.Count == 7 && string.Join(",", order) == "1,2,3,4,5,6,7", "Original submission order preserved");
            check(progress.Values.Count == 7 && progress.Values.TrueForAll(x => x == 0.5f), "Native loading progress weight preserved");
            check(stats.Outcome == "completed" && reports == 1 && stats.ElapsedTicks >= 0, "Successful bounded telemetry closes once");
        }
        foreach (int count in new[] { 0, 1, 50 })
        {
            var stats = new BoundedSceneStats();
            int next = 1;
            Task run = BoundedScenes.Run(count, 99, CancellationToken.None, () => true,
                i => { check(i == next++, "Immediate completion retains order"); return Task.CompletedTask; }, null, stats);
            Completed(run, check, "Empty/first-only/immediate schedule");
            check(stats.Window == 4 && stats.Completed == Math.Max(0, count - 1) && stats.Peak == 0, "Clamp window and handle synchronous completion without inventing in-flight operations");
        }
    }

    private static void TestCancellation(Action<bool, string> check)
    {
        var cts = new CancellationTokenSource();
        var source = new TaskCompletionSource<bool>();
        var stats = new BoundedSceneStats();
        var progress = new Progress();
        Task run = BoundedScenes.Run(10, 2, cts.Token, () => true, _ => source.Task, progress, stats);
        cts.Cancel();
        check(!run.IsCompleted && stats.Submitted == 2, "Cancellation waits for already submitted Unity operations");
        source.SetResult(true);
        Completed(run, check, "Canceled native-compatible schedule");
        check(stats.Submitted == 2 && stats.Completed == 2 && progress.Values.Count == 0 && stats.Outcome == "canceled",
            "Cancellation stops admission and progress; native preset handles cancellation afterward");
        stats = new BoundedSceneStats();
        run = BoundedScenes.Run(10, 2, cts.Token, () => true, _ => { throw new Exception("Must not launch"); }, null, stats);
        Completed(run, check, "Already canceled schedule");
        check(stats.Submitted == 0 && stats.Outcome == "canceled", "Pre-cancellation submits nothing");
        cts.Dispose();

        bool active = true;
        source = new TaskCompletionSource<bool>(); stats = new BoundedSceneStats();
        run = BoundedScenes.Run(10, 2, CancellationToken.None, () => active, _ => source.Task, null, stats);
        active = false; source.SetResult(true);
        check(run.IsCompleted && run.IsCanceled && stats.Submitted == 2 && stats.Completed == 2 && stats.Outcome == "scope-ended",
            "Master/timeout shutdown drains operations and cancels incomplete preset");
        try { run.GetAwaiter().GetResult(); check(false, "Scope shutdown must stay canceled"); }
        catch (OperationCanceledException) { check(true, "Scope shutdown cancellation remains visible"); }
    }

    private static void TestFaults(Action<bool, string> check)
    {
        var source = new TaskCompletionSource<bool>();
        var stats = new BoundedSceneStats();
        int reports = 0;
        Task run = BoundedScenes.Run(10, 2, CancellationToken.None, () => true,
            i => i == 1 ? source.Task : throw new InvalidOperationException("launch fault"), null, stats, _ => reports++);
        check(!run.IsCompleted && stats.Submitted == 1, "Launch fault drains previous operation");
        source.SetResult(true);
        check(run.IsFaulted && reports == 1 && stats.Outcome == "faulted", "Launch fault finishes report once");
        try { run.GetAwaiter().GetResult(); check(false, "Launch fault expected"); }
        catch (InvalidOperationException error) { check(error.Message == "launch fault", "Original launch fault preserved"); }

        stats = new BoundedSceneStats();
        run = BoundedScenes.Run(5, 2, CancellationToken.None, () => true, _ => Task.CompletedTask,
            new ThrowingProgress(), stats, _ => { throw new Exception("observer failure"); });
        check(run.IsFaulted && stats.Submitted == 2 && stats.Completed == 2, "Progress fault stops admission and drains window");
        try { run.GetAwaiter().GetResult(); check(false, "Progress fault expected"); }
        catch (InvalidOperationException error) { check(error.Message == "progress fault", "Observer cannot replace original progress fault"); }

        stats = new BoundedSceneStats();
        run = BoundedScenes.Run(5, 2, CancellationToken.None, () => true,
            _ => Task.FromException(new InvalidOperationException("completion fault")), null, stats);
        check(run.IsFaulted && stats.Submitted == 2, "Completion fault stops further submission");
        try { run.GetAwaiter().GetResult(); check(false, "Completion fault expected"); }
        catch (InvalidOperationException error) { check(error.Message == "completion fault", "Completion fault remains visible"); }
    }

    private static void TestAdapter(Action<bool, string> check)
    {
        var harmony = new Harmony("ftls.loading.boundedfixture");
        try
        {
            BoundedScenes.Install(harmony, name =>
            {
                switch (name)
                {
                    case "EFT.LoadScenesFromPresetOperation": return typeof(FakePreset);
                    case "EFT.AssetsManager.IAssetsManager": return typeof(IFakeManager);
                    case "EFT.ResourceKey": return typeof(FakeKey);
                    case "EFT.AssetsManager.LoadSceneOperation": return typeof(FakeOperation);
                    case "EFT.AssetsManagerExtension": return typeof(FakeExtension);
                    default: throw new Exception("Unexpected bounded ABI type " + name);
                }
            });
            bool enabled = false;
            BoundedScenes.Enabled = () => enabled; BoundedScenes.Window = () => 2;
            BoundedScenes.CaptureActive = () => () => enabled;
            var scenes = new List<FakeKey>();
            for (int i = 0; i < 5; i++) scenes.Add(new FakeKey { Index = i });
            var manager = new FakeManager();
            var preset = new FakePreset { _assetsManager = manager, _allowSceneActivation = true };
            Completed(preset.DirectLoadCoroutine(scenes), check, "Opt-out adapter");
            check(preset.NativeCalls == 1 && manager.Operations.Count == 0, "Opt-out is exact original task path");
            enabled = true; preset._allowSceneActivation = false;
            Completed(preset.DirectLoadCoroutine(scenes), check, "Preload adapter");
            check(preset.NativeCalls == 2 && manager.Operations.Count == 0, "Preload cannot use as-ready activation");
            preset._allowSceneActivation = true;
            var first = new FakeOperation(); first.Finish(); preset._loadSceneOperations.Add(first);
            BoundedSceneStats reported = null;
            BoundedScenes.BeginReport = () => result => reported = result;
            var progress = new Progress();
            Task task = preset.DirectLoadCoroutine(scenes, progress);
            check(!task.IsCompleted && manager.Operations.Count == 2 && preset._loadSceneOperations[0] == first,
                "Native adapter excludes first scene and preserves first operation");
            manager.Operations[1].Finish(true);
            check(manager.Operations.Count == 3, "Native terminal failure releases bounded slot");
            manager.Operations[2].Finish(); manager.Operations[0].Finish(); manager.Operations[3].Finish();
            Completed(task, check, "Native-like adapter");
            check(preset.NativeCalls == 2 && preset._loadSceneOperations.Count == 5,
                "All native operations retained without invoking original direct task");
            check(reported != null && reported.Submitted == 4 && reported.Completed == 4 && reported.Failed == 1 && reported.Outcome == "completed",
                "Failed native operations remain terminal results for original preset error aggregation");
            check(preset._loadSceneOperations[2].Failed && progress.Values.Count == 4,
                "Native failed operation and progress preserved");
            check(string.Join(",", manager.Order) == "1,2,3,4", "Typed native adapter submits original resource keys in order");
        }
        finally { harmony.UnpatchSelf(); }
    }
}
