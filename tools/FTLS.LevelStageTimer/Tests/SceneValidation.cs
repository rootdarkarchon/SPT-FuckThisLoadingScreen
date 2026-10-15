using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FTLS.LevelStageTimer;
using HarmonyLib;

internal static class SceneValidation
{
    public sealed class Operation
    {
        public bool Completed { get; private set; }
        public bool Succeed { get; private set; }
        public bool Failed { get; private set; }
        private string error;
        public bool ThrowError;
        public string Error { get { if (ThrowError) throw new InvalidOperationException("expected error read failure"); return error; } set { error = value; } }
        public event Action CompletedEvent;
        public int Listeners => CompletedEvent?.GetInvocationList().Length ?? 0;
        public object Result => throw new Exception("Timer must not consume a result");
        public void Complete(bool success = true) { Completed = true; Succeed = success; Failed = !success; CompletedEvent?.Invoke(); }
    }
    private static int checks;
    private static long tick;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    private static Recorder recorder;
    private static SceneTracker tracker;
    private static Capture capture;
    private static Operation Setup(bool initial = false, int rows = 2048)
    {
        tick = 0; recorder = new Recorder(3, 10, rows, 20, () => tick, 1000);
        recorder.Begin("raid", "work", true); capture = recorder.ActiveCapture;
        tracker = new SceneTracker(recorder, new OperationAccess(typeof(Operation)));
        var owner = new Operation(); tracker.Start(owner, "bundle,\"quoted", "scene", "Additive", initial); return owner;
    }
    public static int Run(string install)
    {
        checks = 0;
        var owner = Setup(); var bundle = new Operation();
        tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner);
        tick = 10; Task.Run(() => bundle.Complete()).GetAwaiter().GetResult();
        Check(bundle.Listeners == 0 && capture.ScenePhases[0].BundleEnd == 10, "Worker bundle completion is observed and detached");
        tracker.BeginUnity(owner); tick = 12; tracker.DispatchReturned(owner, true);
        tick = 20; tracker.Progress(.9f, owner); owner.Complete();
        Check(owner.Listeners == 0 && capture.ScenePhases[0].Outcome == "pending", "Held-ready success does not imply Unity activation");
        tick = 50; tracker.ActivationRequested(owner); tick = 70; tracker.NotifyEngineComplete(owner);
        var phase = capture.ScenePhases[0];
        Check(phase.Outcome == "completed" && phase.ActivationKind == "explicit", "Explicit activation completes scene");
        Check(phase.BundleEnd - phase.BundleStart == 10 && phase.Ready - phase.UnityStart == 10 && phase.UnityDone - phase.ActivationStart == 20, "Phase attribution excludes the held activation gap");
        Check(phase.UnityDone - phase.UnityStart == 60 && phase.DispatchMs == 2, "Combined wall interval and synchronous dispatch preserved");
        Check(SceneTracker.Export(capture).Contains("\"bundle,\"\"quoted\""), "Scene CSV quotes names");
        Check(SceneTracker.Export(capture).Split('\n')[0].EndsWith(",ready_to_activation_ms,completion_to_native_return_ms"), "Scene diagnostic columns append to the existing CSV contract");
        Check(SceneTracker.Export(capture).EndsWith(",30.000,\n"), "Explicit readiness hold is reported while native readiness before Unity completion leaves return delay blank");

        owner = Setup(true); tracker.BeginUnity(owner); tracker.DispatchReturned(owner, true);
        tick = 7; tracker.Progress(.9f, owner); tick = 9; tracker.NotifyEngineComplete(owner); owner.Complete();
        Check(capture.ScenePhases[0].ActivationKind == "automatic-observed-tail" && capture.ScenePhases[0].ActivationStart == 7, "Automatic activation records observed tail only");
        Check(SceneTracker.Export(capture).EndsWith(",,0.000\n"), "Automatic readiness cannot invent an explicit hold interval; simultaneous native completion has a zero return delay");
        owner = Setup(true); tracker.BeginUnity(owner); tracker.DispatchReturned(owner, true); tick = 9; tracker.NotifyEngineComplete(owner); owner.Complete();
        Check(capture.ScenePhases[0].ActivationKind == "automatic-unsplit" && !capture.ScenePhases[0].ActivationStart.HasValue && !capture.ScenePhases[0].Ready.HasValue, "Unobserved readiness is never fabricated");
        Check(SceneTracker.Export(capture).EndsWith(",,0.000\n"), "Automatic unsplit scene keeps its readiness interval unknown");

        owner = Setup(); tracker.BeginUnity(owner); tracker.DispatchReturned(owner, true);
        tick = 5; tracker.ActivationRequested(owner); tick = 10; tracker.NotifyEngineComplete(owner);
        Check(SceneTracker.Export(capture).EndsWith(",,\n"), "Missing readiness and native return observations produce blank intervals");
        tick = 15; owner.Complete();
        Check(SceneTracker.Export(capture).EndsWith(",,5.000\n"), "Native return observed after Unity completion reports only the positive tail");
        // Defensive export checks: partial/inconsistent observations must not become negative or invented durations.
        phase = capture.ScenePhases[0]; phase.ReadyObserved = true; phase.Ready = 8;
        Check(SceneTracker.Export(capture).EndsWith(",,5.000\n"), "Readiness observed after explicit activation does not produce a negative hold");
        phase.ActivationStart = null; phase.Ready = 2;
        Check(SceneTracker.Export(capture).EndsWith(",,5.000\n"), "Explicit activation without its timestamp leaves the hold unknown");
        phase.ReadyObserved = false; phase.ActivationStart = 5;
        Check(SceneTracker.Export(capture).EndsWith(",,5.000\n"), "Readiness timestamp without an observed readiness flag cannot establish the hold");

        owner = Setup(); bundle = new Operation(); bundle.Complete(); tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner);
        Check(bundle.Listeners == 0 && capture.ScenePhases[0].BundleOutcome == "completed", "Already completed bundles detach synchronously");
        tracker.BeginUnity(owner); tick = 2; tracker.DispatchReturned(owner, false); tracker.BeginUnity(owner); tick = 5; tracker.DispatchReturned(owner, true);
        Check(capture.ScenePhases[0].NullRequests == 1 && capture.ScenePhases[0].UnityStart == 2 && capture.ScenePhases[0].DispatchMs == 5, "Native null-operation fallback tracked without replacement");
        owner.Complete(false);
        Check(owner.Listeners == 0 && capture.ScenePhases[0].Outcome == "failed", "Failed native operation detaches observers");

        owner = Setup(); bundle = new Operation(); tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner);
        tick = 10000; var closed = recorder.TakeReady(); tracker.Detach(closed);
        Check(owner.Listeners == 0 && bundle.Listeners == 0 && closed.ScenePhases[0].Outcome == "incomplete-at-export", "Timeout export removes subscriptions");
        tick++; bundle.Complete(); owner.Complete();
        Check(!closed.ScenePhases[0].BundleEnd.HasValue, "Late completion cannot mutate exported scene data");
        owner = Setup(); tracker.BeginUnity(owner); tracker.DispatchReturned(owner, true); tracker.Progress(.9f, owner); owner.Complete();
        tracker.Detach(recorder.TakeReady(true));
        Check(capture.ScenePhases[0].Outcome == "held-at-ready-at-export", "Unactivated ready scenes explicitly marked");

        owner = Setup();
        for (int i = 1; i < 520; i++) tracker.Start(new Operation(), "b", "s" + i, "Additive", false);
        Check(capture.ScenePhases.Count == 512 && capture.DroppedScenes == 8 && capture.SceneRequests == 520, "Independent scene observer budget capped at 512");
        tracker.Stop(); Check(owner.Listeners == 0 && capture.ScenePhases.All(p => p.Outcome == "plugin-stopped"), "Stop detaches every pending scene");
        owner = Setup(rows: 1);
        Check(capture.ScenePhases.Count == 1 && capture.DroppedScenes == 0 && owner.Listeners == 1, "Scene records survive general row budget exhaustion");
        tracker.Stop();
        owner = Setup(rows: 1);
        for (int i = 0; i < 248; i++)
        {
            if (i != 0) { owner = new Operation(); tracker.Start(owner, "b", "s" + i, "Additive", false); }
            bundle = new Operation { Error = "missing test bundle" }; tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner);
            tick++; bundle.Complete(false); tracker.BeginUnity(owner); tracker.DispatchReturned(owner, true);
            tick++; tracker.Progress(.9f, owner); owner.Complete(); tick++; tracker.ActivationRequested(owner); tick++; tracker.NotifyEngineComplete(owner);
        }
        Check(capture.ScenePhases.Count == 248 && capture.DroppedScenes == 0 && capture.Rows.Count == 1 && capture.ScenePhases.All(s => s.Outcome == "completed" && s.ActivationKind == "explicit" && s.BundleError == "missing test bundle"), "All 248 Streets scenes finish phase/error recording even with exhausted general span budget");
        tracker.Stop();
        owner = Setup(); bundle = new Operation { Error = "Can't load bundle: \"missing\", file error\nsecond line" };
        tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner); bundle.Complete(false);
        Check(capture.ScenePhases[0].BundleError == bundle.Error && SceneTracker.Export(capture).Contains("\"\"missing\"\""), "Bundle failure reasons are retained and CSV escaped");
        tracker.Stop();
        owner = Setup(); bundle = new Operation { Error = new string('x', 2000) };
        tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner); bundle.Complete(false);
        Check(capture.ScenePhases[0].BundleError.Length == 1024 && capture.ScenePhases[0].BundleErrorTruncated, "Bundle failure strings are bounded and truncation is reported"); tracker.Stop();
        owner = Setup(); bundle = new Operation { ThrowError = true };
        tracker.BeginBundle(owner); tracker.ObserveBundle(bundle, owner); bundle.Complete(false);
        Check(capture.BundleErrorReadFailures == 1 && capture.SceneObserverErrors == 0 && capture.ScenePhases[0].BundleError.Contains("unavailable"), "A diagnostic error getter failure cannot abort scene observation"); tracker.Stop();

        // Compile patches over the actual installed state machines in Unity Mono; never invoke scene loading.
        string managed = Path.Combine(install, "EscapeFromTarkov_Data", "Managed");
        ResolveEventHandler resolve = (_, args) => {
            string path = Path.Combine(managed, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        var harmony = new Harmony("ftls.levelstagetimer.scene-validation");
        try
        {
            Assembly game = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
            var frameTicks = game.GetType("Diz.Jobs.JobScheduler").GetProperty("FrameTicks", BindingFlags.Public | BindingFlags.Instance);
            Check(frameTicks != null && frameTicks.PropertyType == typeof(long) && frameTicks.GetGetMethod() != null && !frameTicks.GetGetMethod().IsStatic,
                "Installed scheduler FrameTicks is a public instance Int64 property for read-only samples");
            var coverage = new System.Collections.Generic.List<string>();
            SceneProbes.Install(harmony, name => game.GetType(name), coverage);
            Check(coverage.Count == 1, "Installed scene Coroutine and activation MoveNext patches compile");
            Type native = game.GetType("EFT.AssetsManager.AssetsManager+LoadSceneOperation");
            Type comfort = Assembly.LoadFrom(Path.Combine(managed, "Comfort.Unity.dll")).GetType("Comfort.Common.IOperation");
            Check(comfort.IsAssignableFrom(native), "Installed native scene operation supports observed completion ABI");
            _ = new OperationAccess(comfort);
            checks += ActivationValidation.Installed(game, recorder);
            bool rejected = false;
            try { SceneProbes.RewriteLoad(Array.Empty<CodeInstruction>(), null).ToArray(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unexpected native scene call counts refuse instrumentation");
            rejected = false;
            try { SceneProbes.RewriteActivation(Array.Empty<CodeInstruction>(), null).ToArray(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unexpected activation setter count refuses instrumentation");
            var setter = typeof(UnityEngine.AsyncOperation).GetProperty("allowSceneActivation").GetSetMethod();
            var rewritten = SceneProbes.RewriteActivation(new[] { new CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt, setter) }, null).ToArray();
            int probeIndex = Array.FindIndex(rewritten, i => i.operand is MethodInfo method && method.Name == nameof(SceneProbes.ActivationRequested));
            int setterIndex = Array.FindIndex(rewritten, i => Equals(i.operand, setter));
            Check(probeIndex >= 0 && setterIndex > probeIndex && rewritten.Count(i => Equals(i.operand, setter)) == 1, "Activation observation precedes the unchanged setter, including inline completion");
        }
        finally { harmony.UnpatchSelf(); AppDomain.CurrentDomain.AssemblyResolve -= resolve; SceneProbes.Tracker = null; }
        return checks;
    }
}
