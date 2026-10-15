using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FTLS.LevelStageTimer;
using HarmonyLib;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public sealed class ActivationFixture : MonoBehaviour
{
    public static int Calls;
    public static Exception Failure;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Awake() { Calls++; OnEnable(); }
    [MethodImpl(MethodImplOptions.NoInlining)] public void OnEnable() { Calls++; if (Failure != null) throw Failure; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { Calls++; }
    public void Extra() { }
    public void Extra2() { }
}

internal static class ActivationValidation
{
    private static int checks;
    private static long tick;
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); checks++; }
    private static readonly MethodInfo[] Methods = new[] { "Awake", "OnEnable", "Start", "Extra", "Extra2" }.Select(n => typeof(ActivationFixture).GetMethod(n)).ToArray();
    private static Recorder recorder;
    private static Capture capture;
    private static ActivationRecorder observer;
    private static ScenePhase Setup(string path = "scene")
    {
        tick = 0; recorder = new Recorder(3, 10, 1, 1, () => tick, 1000);
        recorder.Begin("raid", "work", true); capture = recorder.ActiveCapture;
        observer = new ActivationRecorder(recorder, Methods);
        return AddScene(path);
    }
    private static ScenePhase AddScene(string path)
    {
        var scene = new ScenePhase { Id = capture.ScenePhases.Count + 1, Scene = path, ActivationStart = 0, UnityStart = 0 };
        capture.ScenePhases.Add(scene); capture.SceneLookup.Add(path, scene); return scene;
    }
    internal static int Run()
    {
        checks = 0;
        ScenePhase phase = Setup();
        var outer = observer.BeginScene(capture, "scene", 1, 0, false); tick = 3;
        var inner = observer.BeginScene(capture, "scene", 1, 1, false); tick = 8; observer.End(inner, false);
        tick = 10; observer.End(outer, false);
        Check(phase.Lifecycle[2].Calls == 2 && phase.Lifecycle[2].InclusiveTicks == 15 && phase.Lifecycle[2].SelfTicks == 10, "Nested lifecycle self removes observed child double counting");
        Check(capture.LifecycleRows.Values.Single(r => r.Method.EndsWith(".Awake")).Totals.SelfTicks == 5, "Parent self excludes observed child");
        phase.UnityDone = 10; tick = 11;
        var after = observer.BeginScene(capture, "scene", 1, 2, false); tick = 15; observer.End(after, false);
        Check(phase.Lifecycle[3].SelfTicks == 4 && phase.Lifecycle[2].SelfTicks == 10, "Deferred Start kept separate from activation");
        var dispatch = observer.BeginScene(capture, "scene", 1, 3, true); tick++; observer.End(dispatch, false);
        Check(phase.SceneLoadedDispatch.Calls == 1 && phase.Lifecycle[3].Calls == 1, "Scene-loaded dispatch remains a separate category");
        Check(observer.BeginScene(capture, "unrelated", 1, 0, false).Owner == null && capture.UnmatchedLifecycleCalls == 1, "Unrelated scenes cannot be attributed to last requested scene");
        Check(observer.BeginScene(capture, "scene", 2, 0, false).Owner == null && capture.AmbiguousLifecycleCalls == 1, "Mismatched scene handles rejected");
        capture.SceneLookup["ambiguous"] = null;
        Check(observer.BeginScene(capture, "ambiguous", 3, 0, false).Owner == null && capture.AmbiguousLifecycleCalls == 2, "Ambiguous scene paths are reported rather than guessed");
        phase = Setup();
        var crossing = observer.BeginScene(capture, "scene", 1, 0, false); phase.UnityDone = 1; tick = 2; observer.End(crossing, true);
        Check(phase.Lifecycle[2].Threw == 1 && phase.Lifecycle[2].CrossedCompletion == 1, "Cross-boundary/throwing lifecycle calls identified");
        var late = observer.BeginScene(capture, "scene", 1, 0, false); recorder.TakeReady(true); tick++; observer.End(late, false);
        Check(phase.Lifecycle[3].Completed == 0 && phase.Lifecycle[3].Calls == 1, "Late finalizers cannot mutate sealed timing data");

        phase = Setup("Assets/City_SE_01_Pinewood_Hotel_indoor.unity");
        for (int i = 1; i <= 600; i++)
        {
            var token = observer.BeginScene(capture, phase.Scene, 1, 0, false); tick += i; observer.End(token, false);
        }
        Check(capture.PinewoodCalls == 600 && capture.PinewoodDetails.Count == 256 && capture.PinewoodDetails.Min(d => d.Duration) == 345 && capture.PinewoodDetails.Max(d => d.Duration) == 600, "Pinewood retains the slowest256 of all calls");
        Check(observer.ExportPinewood(capture).Split('\n').Length == 258 && observer.ExportMethods(capture).Contains("entry_window"), "Bounded Pinewood/method exports");

        phase = Setup();
        var nested = new LifecycleToken[128];
        for (int i = 0; i < nested.Length; i++) nested[i] = observer.BeginScene(capture, "scene", 1, 0, false);
        Check(observer.BeginScene(capture, "scene", 1, 0, false).Owner == null && capture.DroppedLifecycleDepth == 1, "Lifecycle nesting hard capped");
        for (int i = nested.Length - 1; i >= 0; i--) observer.End(nested[i], false);
        Check(phase.Lifecycle[2].Completed == 128, "Nesting cap does not disrupt outer observers");

        phase = Setup();
        for (int i = 1; i < 512; i++) AddScene("scene" + i);
        foreach (var scene in capture.ScenePhases)
            for (int window = 0; window < 4; window++)
            {
                scene.ReadyObserved = window == 1;
                scene.ActivationStart = window == 2 ? 0 : (double?)null;
                scene.UnityDone = window == 3 ? 0 : (double?)null;
                for (int method = 0; method < Methods.Length; method++)
                {
                    var token = observer.BeginScene(capture, scene.Scene, scene.Id, method, false); tick++; observer.End(token, false);
                }
            }
        Check(capture.LifecycleRows.Count == 8192 && capture.DroppedLifecycleDetails == 2048, "Lifecycle method-detail storage stays bounded");
        Check(capture.ScenePhases.Sum(s => s.Lifecycle.Sum(t => t.Calls)) == 10240 && capture.ScenePhases.Sum(s => s.Lifecycle.Sum(t => t.SelfTicks)) == 10240, "Per-scene totals include calls beyond method-detail cap");

        // Exercise the real prefix/finalizer on a Unity component and preserve the exact original exception.
        var unityScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        const string unityPath = "Assets/LevelTimerActivationFixture.unity";
        var harmony = new Harmony("ftls.levelstagetimer.activation-fixture");
        GameObject gameObject = null;
        try
        {
            Check(EditorSceneManager.SaveScene(unityScene, unityPath), "Save isolated lifecycle fixture scene");
            gameObject = new GameObject("LevelTimerActivationFixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(gameObject, unityScene);
            var component = gameObject.AddComponent<ActivationFixture>();
            phase = Setup(unityPath); ActivationProbes.Recorder = observer;
            foreach (var method in Methods.Take(3)) harmony.Patch(method, prefix: new HarmonyMethod(typeof(ActivationProbes), nameof(ActivationProbes.Prefix)), finalizer: new HarmonyMethod(typeof(ActivationProbes), nameof(ActivationProbes.Finalizer)));
            int before = ActivationFixture.Calls; component.Awake();
            Check(ActivationFixture.Calls == before + 2 && phase.Lifecycle[2].Completed == 2, "Real lifecycle hooks preserve calls and capture nested callback scene");
            var expected = new InvalidOperationException("expected lifecycle failure"); ActivationFixture.Failure = expected;
            try { component.Awake(); throw new Exception("Lifecycle exception suppressed"); }
            catch (InvalidOperationException actual) { Check(ReferenceEquals(expected, actual), "Original lifecycle exception identity preserved"); }
            Check(phase.Lifecycle[2].Threw == 2 && capture.LifecycleObserverErrors == 0, "Throwing callback observers close without error");
            ActivationFixture.Failure = null;
            var delivered = new System.Collections.Generic.List<int>();
            UnityAction<Scene, LoadSceneMode> callback = null;
            UnityAction<Scene, LoadSceneMode> lateSubscriber = (s, m) => delivered.Add(9);
            UnityAction<Scene, LoadSceneMode> first = (s, m) => { Check(s == unityScene && m == LoadSceneMode.Additive, "Scene handler arguments preserved"); delivered.Add(1); tick += 5; callback += lateSubscriber; };
            UnityAction<Scene, LoadSceneMode> second = (s, m) => { delivered.Add(2); tick += 7; };
            callback = first + second + second;
            var fixture = typeof(ActivationValidation).GetMethod(nameof(DispatchFixture), BindingFlags.Static | BindingFlags.NonPublic);
            harmony.Patch(fixture, transpiler: new HarmonyMethod(typeof(ActivationProbes), nameof(ActivationProbes.SceneHandlerTranspiler)));
            DispatchFixture(callback, unityScene, LoadSceneMode.Additive);
            Check(delivered.SequenceEqual(new[] { 1, 2, 2 }) && callback.GetInvocationList().Length == 4, "Subscriber snapshot, duplicate entries and mutation behavior preserved");
            Check(phase.SceneLoadedHandlers.Completed == 3 && phase.SceneLoadedHandlers.SelfTicks == 19 && capture.SceneHandlerMethods.Count == 2, "Per-handler timing accounts for every delivered invocation");
            Check(observer.ExportMethods(capture, handlersOnly: true).Contains("scene-loaded-handler") && observer.ExportMethods(capture, handlersOnly: true).Split('\n').Length == 4, "Dedicated handler export contains only subscriber aggregates");
            delivered.Clear();
            var handlerFailure = new InvalidOperationException("expected subscriber failure");
            UnityAction<Scene, LoadSceneMode> fail = (s, m) => { delivered.Add(3); throw handlerFailure; };
            try { DispatchFixture(second + fail + lateSubscriber, unityScene, LoadSceneMode.Single); throw new Exception("Subscriber exception suppressed"); }
            catch (InvalidOperationException actual) { Check(ReferenceEquals(actual, handlerFailure), "Subscriber exception identity preserved"); }
            Check(delivered.SequenceEqual(new[] { 2, 3 }) && phase.SceneLoadedHandlers.Threw == 1 && capture.LifecycleObserverErrors == 0, "Throw stops later subscriber delivery and closes timing");
            ActivationProbes.Recorder = null; delivered.Clear();
            DispatchFixture(second + lateSubscriber, unityScene, LoadSceneMode.Single);
            Check(delivered.SequenceEqual(new[] { 2, 9 }), "Disarmed handler wrapper forwards original multicast call");
            ActivationProbes.Recorder = observer;
            for (int i = 0; i < 130; i++)
            {
                var dynamicMethod = new System.Reflection.Emit.DynamicMethod("SceneHandler" + i, typeof(void), new[] { typeof(Scene), typeof(LoadSceneMode) });
                dynamicMethod.GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
                DispatchFixture((UnityAction<Scene, LoadSceneMode>)dynamicMethod.CreateDelegate(typeof(UnityAction<Scene, LoadSceneMode>)), unityScene, LoadSceneMode.Single);
            }
            Check(capture.SceneHandlerMethods.Count == 128 && capture.DroppedSceneHandlerCalls > 0, "Distinct scene handler methods are hard capped without stopping delivery");
            var excludedAssembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ORBIT.TimerFixture"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var excludedType = excludedAssembly.DefineDynamicModule("Fixture").DefineType("ExcludedSceneHandler");
            var excludedMethod = excludedType.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(Scene), typeof(LoadSceneMode) });
            var generator = excludedMethod.GetILGenerator();
            var callsField = typeof(ActivationFixture).GetField(nameof(ActivationFixture.Calls));
            generator.Emit(System.Reflection.Emit.OpCodes.Ldsfld, callsField); generator.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
            generator.Emit(System.Reflection.Emit.OpCodes.Add); generator.Emit(System.Reflection.Emit.OpCodes.Stsfld, callsField); generator.Emit(System.Reflection.Emit.OpCodes.Ret);
            var excluded = (UnityAction<Scene, LoadSceneMode>)Delegate.CreateDelegate(typeof(UnityAction<Scene, LoadSceneMode>), excludedType.CreateType().GetMethod("Run"));
            before = ActivationFixture.Calls;
            DispatchFixture(excluded, unityScene, LoadSceneMode.Single);
            Check(ActivationFixture.Calls == before + 1 && capture.OutOfScopeSceneHandlerCalls == 1, "Out-of-scope handlers are forwarded without individual timing");
        }
        finally
        {
            ActivationFixture.Failure = null; ActivationProbes.Recorder = null; harmony.UnpatchSelf();
            if (gameObject != null) UnityEngine.Object.DestroyImmediate(gameObject);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
        return checks;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchFixture(UnityAction<Scene, LoadSceneMode> callback, Scene scene, LoadSceneMode mode) => callback(scene, mode);
    internal static int Installed(Assembly game, Recorder recorder)
    {
        int before = checks;
        MethodInfo[] methods = ActivationProbes.Discover(game, out int generic, out int nonvoid);
        Check(methods.Length == 963 && generic == 21 && nonvoid == 9, "Installed lifecycle discovery matches independently enumerated metadata");
        var harmony = new Harmony("ftls.levelstagetimer.installed-lifecycle");
        try
        {
            var coverage = new System.Collections.Generic.List<string>();
            _ = ActivationProbes.Install(harmony, game, recorder, coverage, out string manifest);
            Check(methods.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(harmony.Id) == true), "All installed lifecycle patches compile in Unity Mono");
            Check(coverage.Count == 1 && manifest.Contains("Internal_SceneLoaded"), "Managed scene-loaded dispatcher compiled and coverage exported");
        }
        finally { harmony.UnpatchSelf(); }
        return checks - before;
    }
}
