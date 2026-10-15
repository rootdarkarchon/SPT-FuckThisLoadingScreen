using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FTLS.LevelStageTimer
{
    internal sealed class ScenePhase
    {
        public int Id, NullRequests;
        public string Bundle, Scene, Mode, BundleError = "", Outcome = "pending", BundleOutcome = "pending", ActivationKind = "none";
        public bool InitialActivation, ReadyObserved, BundleErrorTruncated;
        public double Start, DispatchMs;
        public double? BundleStart, BundleEnd, UnityStart, Ready, ActivationStart, UnityDone, NativeReady;
        public int? SceneHandle;
        public readonly LifecycleTotals[] Lifecycle = Enumerable.Range(0, 4).Select(_ => new LifecycleTotals()).ToArray();
        public readonly LifecycleTotals SceneLoadedDispatch = new LifecycleTotals();
        public readonly LifecycleTotals SceneLoadedHandlers = new LifecycleTotals();
    }

    // Read status and subscribe only to completion; never call GetResult/MoveNext/Reset.
    internal sealed class OperationAccess
    {
        private readonly PropertyInfo completed, succeed, failed, error;
        private readonly EventInfo completion;
        public OperationAccess(Type type)
        {
            completed = type.GetProperty("Completed") ?? throw new MissingMemberException(type.FullName, "Completed");
            succeed = type.GetProperty("Succeed") ?? throw new MissingMemberException(type.FullName, "Succeed");
            failed = type.GetProperty("Failed") ?? throw new MissingMemberException(type.FullName, "Failed");
            error = type.GetProperty("Error") ?? throw new MissingMemberException(type.FullName, "Error");
            completion = type.GetEvent("CompletedEvent") ?? throw new MissingMemberException(type.FullName, "CompletedEvent");
            if (completed.PropertyType != typeof(bool) || succeed.PropertyType != typeof(bool) || failed.PropertyType != typeof(bool) || error.PropertyType != typeof(string) || completion.EventHandlerType != typeof(Action))
                throw new NotSupportedException("Operation observation ABI mismatch");
        }
        public bool Completed(object value) => (bool)completed.GetValue(value);
        public bool Succeed(object value) => (bool)succeed.GetValue(value);
        public bool Failed(object value) => (bool)failed.GetValue(value);
        public string Error(object value) => (string)error.GetValue(value);
        public void Add(object value, Action handler) => completion.AddEventHandler(value, handler);
        public void Remove(object value, Action handler) => completion.RemoveEventHandler(value, handler);
    }

    internal sealed class SceneWatch
    {
        public object Owner, BundleOperation;
        public Capture Capture;
        public ScenePhase Phase;
        public Span Total, Bundle, Loading, Activation, Dispatch;
        public Action BundleHandler, NativeHandler;
        public AsyncOperation UnityOperation;
        public Action<AsyncOperation> UnityHandler;
        public bool NativeSucceeded, EngineDone, Closed;
        public double DispatchStart;
    }

    internal sealed class SceneTracker
    {
        internal const int MaxScenes = 512, DetailScenes = 64;
        private readonly object gate = new object();
        private readonly Recorder recorder;
        private readonly OperationAccess access;
        private readonly Dictionary<object, SceneWatch> active = new Dictionary<object, SceneWatch>(ReferenceComparer.Instance);
        public SceneTracker(Recorder recorder, OperationAccess access) { this.recorder = recorder; this.access = access; }
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private static string Bounded(string text, int length = 512) => text == null || text.Length <= length ? text : text.Substring(0, length);
        private static string Detail(SceneWatch watch) => "scene_id=" + watch.Phase.Id + "; " + watch.Phase.Scene + "; " + watch.Phase.Bundle;
        private double Now(SceneWatch watch) => recorder.Elapsed(watch.Capture);
        private Span Begin(SceneWatch watch, string stage) => watch.Phase.Id <= DetailScenes ? recorder.Begin(stage, "work", limit: 128, detail: Detail(watch)) : null;
        private static void Finish(ref Span span, string outcome) { Span value = span; span = null; value?.Finish(outcome); }
        private SceneWatch Find(object owner) => owner != null && active.TryGetValue(owner, out SceneWatch watch) && !watch.Closed && !watch.Capture.Sealed ? watch : null;

        public void Start(object owner, string bundle, string scene, string mode, bool initialActivation)
        {
            lock (gate)
            {
                DetachSealed();
                if (owner == null || active.ContainsKey(owner)) return;
                Capture current = recorder.ActiveCapture;
                if (current == null) return;
                current.SceneRequests++;
                if (current.ScenePhases.Count >= MaxScenes) { current.DroppedScenes++; return; }
                Capture capture = current; // Phase records do not depend on the general span budget.
                var phase = new ScenePhase { Id = capture.ScenePhases.Count + 1, Bundle = Bounded(bundle), Scene = Bounded(scene), Mode = mode,
                    InitialActivation = initialActivation, Start = recorder.Elapsed(capture) };
                Span total = phase.Id <= DetailScenes ? recorder.Begin("scene.request", "work", limit: DetailScenes, detail: Bounded(scene)) : null;
                var watch = new SceneWatch { Owner = owner, Capture = capture, Phase = phase, Total = total };
                capture.ScenePhases.Add(phase); active.Add(owner, watch);
                if (scene != null && scene.Length <= 512)
                {
                    string key = scene.Replace('\\', '/');
                    // Repeated paths cannot safely identify an instance until its scene handle is known.
                    if (capture.SceneLookup.ContainsKey(key)) capture.SceneLookup[key] = null;
                    else capture.SceneLookup.Add(key, phase);
                }
                watch.NativeHandler = () => NativeCompleted(watch);
                access.Add(owner, watch.NativeHandler);
                if (access.Completed(owner)) NativeCompleted(watch);
            }
        }
        public void BeginBundle(object owner)
        {
            lock (gate) { var w = Find(owner); if (w == null) return; w.Phase.BundleStart = Now(w); w.Bundle = Begin(w, "scene.bundle-wait"); }
        }
        public void ObserveBundle(object operation, object owner)
        {
            lock (gate)
            {
                var w = Find(owner); if (w == null) return;
                if (operation == null) { Finish(ref w.Bundle, "null-operation"); w.Phase.BundleOutcome = "null-operation"; return; }
                w.BundleOperation = operation; w.BundleHandler = () => BundleCompleted(w);
                access.Add(operation, w.BundleHandler);
                if (access.Completed(operation)) BundleCompleted(w);
            }
        }
        private void BundleCompleted(SceneWatch w)
        {
            lock (gate)
            {
                if (w.Closed || w.Capture.Sealed || w.BundleOperation == null) return;
                try
                {
                    // Recognize the companion's explicit terminal no-bundle operation.
                    Type bundleType = w.BundleOperation.GetType();
                    bool skipped = bundleType.IsGenericType && bundleType.GetGenericTypeDefinition().FullName == "FuckThisLoadingScreen.SceneBundleHooks+SkippedProbe`1" &&
                        bundleType.Assembly.GetName().Name == "FuckThisLoadingScreen";
                    string status = skipped ? "skipped-built-in-scene" : access.Failed(w.BundleOperation) ? "failed" : access.Succeed(w.BundleOperation) ? "completed" : "unknown-status";
                    w.Phase.BundleEnd = Now(w); w.Phase.BundleOutcome = status; Finish(ref w.Bundle, status);
                    if (status == "failed")
                        try
                        {
                            string error = access.Error(w.BundleOperation);
                            w.Phase.BundleErrorTruncated = error != null && error.Length > 1024;
                            w.Phase.BundleError = Bounded(error, 1024) ?? "";
                        }
                        catch (Exception error) { w.Capture.BundleErrorReadFailures++; w.Phase.BundleError = "<error unavailable: " + error.GetType().Name + ">"; }
                    RemoveBundle(w);
                }
                catch { Error(w); }
            }
        }
        public void BeginUnity(object owner)
        {
            lock (gate)
            {
                var w = Find(owner); if (w == null) return;
                w.Phase.UnityStart = Now(w); w.DispatchStart = Now(w);
                w.Loading = Begin(w, "scene.unity-loading"); w.Dispatch = Begin(w, "scene.unity-dispatch");
            }
        }
        // Separate from native event wiring so the state transitions can be tested without a native scene load.
        public void DispatchReturned(object owner, bool returnedOperation)
        {
            lock (gate)
            {
                var w = Find(owner); if (w == null) return;
                w.Phase.DispatchMs += Now(w) - w.DispatchStart;
                Finish(ref w.Dispatch, returnedOperation ? "completed" : "null-operation");
                if (!returnedOperation) { w.Phase.NullRequests++; Finish(ref w.Loading, "null-operation"); w.Phase.UnityStart = null; }
            }
        }
        public void ObserveUnity(AsyncOperation operation, object owner)
        {
            lock (gate)
            {
                DispatchReturned(owner, operation != null);
                var w = Find(owner); if (w == null || operation == null) return;
                w.UnityOperation = operation; w.UnityHandler = _ => { try { EngineCompleted(w); } catch { try { Error(w); } catch { } } };
                operation.completed += w.UnityHandler;
                if (operation.isDone) EngineCompleted(w);
            }
        }
        public void Progress(float value, object owner)
        {
            lock (gate)
            {
                var w = Find(owner); if (w == null || w.EngineDone || w.Phase.ReadyObserved || value < 0.9f) return;
                w.Phase.ReadyObserved = true; w.Phase.Ready = Now(w); Finish(ref w.Loading, "completed");
                if (w.Phase.InitialActivation)
                {
                    w.Phase.ActivationKind = "automatic-observed-tail"; w.Phase.ActivationStart = Now(w);
                    w.Activation = Begin(w, "scene.auto-activation-tail");
                }
            }
        }
        public void ActivationRequested(object owner)
        {
            lock (gate)
            {
                var w = Find(owner); if (w == null) return;
                Finish(ref w.Loading, "completed");
                w.Phase.ActivationKind = "explicit"; w.Phase.ActivationStart = Now(w);
                w.Activation = Begin(w, "scene.activation");
            }
        }
        public void NotifyEngineComplete(object owner) { lock (gate) { var w = Find(owner); if (w != null) EngineCompleted(w); } }
        private void EngineCompleted(SceneWatch w)
        {
            lock (gate)
            {
                if (w.Closed || w.Capture.Sealed || w.EngineDone) return;
                w.EngineDone = true; w.Phase.UnityDone = Now(w);
                if (!w.Phase.ReadyObserved && w.Phase.InitialActivation) w.Phase.ActivationKind = "automatic-unsplit";
                Finish(ref w.Loading, "completed"); Finish(ref w.Activation, "completed"); RemoveUnity(w);
                if (w.NativeSucceeded) Close(w, "completed");
            }
        }
        private void NativeCompleted(SceneWatch w)
        {
            lock (gate)
            {
                if (w.Closed || w.Capture.Sealed) return;
                try
                {
                    w.Phase.NativeReady = Now(w);
                    if (access.Failed(w.Owner)) { Close(w, "failed"); return; }
                    w.NativeSucceeded = access.Succeed(w.Owner);
                    RemoveNative(w);
                    if (w.NativeSucceeded && w.EngineDone) Close(w, "completed");
                }
                catch { Error(w); }
            }
        }
        public void Error(object owner) { lock (gate) { var w = Find(owner); if (w != null) Error(w); } }
        private void Error(SceneWatch w) { w.Capture.SceneObserverErrors++; Close(w, "observer-error"); }
        private void RemoveBundle(SceneWatch w)
        {
            object operation = w.BundleOperation; Action handler = w.BundleHandler;
            w.BundleOperation = null; w.BundleHandler = null;
            if (operation != null && handler != null) try { access.Remove(operation, handler); } catch { w.Capture.SceneObserverErrors++; }
        }
        private void RemoveNative(SceneWatch w)
        {
            Action handler = w.NativeHandler; w.NativeHandler = null;
            if (w.Owner != null && handler != null) try { access.Remove(w.Owner, handler); } catch { w.Capture.SceneObserverErrors++; }
        }
        private void RemoveUnity(SceneWatch w)
        {
            AsyncOperation operation = w.UnityOperation; Action<AsyncOperation> handler = w.UnityHandler;
            w.UnityOperation = null; w.UnityHandler = null;
            if (operation != null && handler != null) try { operation.completed -= handler; } catch { w.Capture.SceneObserverErrors++; }
        }
        private void Close(SceneWatch w, string outcome)
        {
            if (w.Closed) return;
            w.Closed = true; w.Phase.Outcome = outcome;
            Finish(ref w.Total, outcome); Finish(ref w.Bundle, outcome); Finish(ref w.Loading, outcome);
            Finish(ref w.Activation, outcome); Finish(ref w.Dispatch, outcome);
            RemoveBundle(w); RemoveNative(w); RemoveUnity(w); active.Remove(w.Owner); w.Owner = null;
        }
        private void DetachSealed()
        {
            foreach (var w in active.Values.Where(w => w.Capture.Sealed).ToArray()) Close(w, w.NativeSucceeded && w.Phase.ReadyObserved && !w.EngineDone ? "held-at-ready-at-export" : "incomplete-at-export");
        }
        public void Detach(Capture capture)
        {
            lock (gate) foreach (var w in active.Values.Where(w => w.Capture == capture).ToArray())
                Close(w, w.NativeSucceeded && w.Phase.ReadyObserved && !w.EngineDone ? "held-at-ready-at-export" : "incomplete-at-export");
        }
        public void Stop() { lock (gate) foreach (var w in active.Values.ToArray()) Close(w, "plugin-stopped"); }
        private static string Csv(string text) => "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
        private static string Number(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
        // Only expose intervals whose two observations establish the requested ordering.
        private static double? Interval(double? start, double? end) => start.HasValue && end.HasValue && end.Value >= start.Value ? end - start : null;
        public static string Export(Capture capture)
        {
            var csv = new StringBuilder("scene_id,bundle,scene,mode,initial_activation,outcome,bundle_outcome,activation_kind,ready_observed,bundle_ms,unity_to_ready_ms,activation_ms,unity_combined_ms,dispatch_ms,start_ms,bundle_start_ms,unity_start_ms,ready_ms,activation_start_ms,unity_done_ms,native_ready_ms,null_requests,bundle_error,bundle_error_truncated,ready_to_activation_ms,completion_to_native_return_ms\n");
            foreach (var p in capture.ScenePhases)
                csv.Append(p.Id).Append(',').Append(Csv(p.Bundle)).Append(',').Append(Csv(p.Scene)).Append(',').Append(Csv(p.Mode)).Append(',')
                    .Append(p.InitialActivation).Append(',').Append(Csv(p.Outcome)).Append(',').Append(Csv(p.BundleOutcome)).Append(',').Append(Csv(p.ActivationKind)).Append(',').Append(p.ReadyObserved).Append(',')
                    .Append(Number(p.BundleEnd - p.BundleStart)).Append(',').Append(Number(p.Ready - p.UnityStart)).Append(',').Append(Number(p.UnityDone - p.ActivationStart)).Append(',')
                    .Append(Number(p.UnityDone - p.UnityStart)).Append(',').Append(Number(p.DispatchMs)).Append(',').Append(Number(p.Start)).Append(',').Append(Number(p.BundleStart)).Append(',')
                    .Append(Number(p.UnityStart)).Append(',').Append(Number(p.Ready)).Append(',').Append(Number(p.ActivationStart)).Append(',').Append(Number(p.UnityDone)).Append(',').Append(Number(p.NativeReady)).Append(',').Append(p.NullRequests).Append(',').Append(Csv(p.BundleError)).Append(',').Append(p.BundleErrorTruncated).Append(',')
                    .Append(Number(p.ActivationKind == "explicit" && p.ReadyObserved ? Interval(p.Ready, p.ActivationStart) : null)).Append(',')
                    .Append(Number(Interval(p.UnityDone, p.NativeReady))).Append('\n');
            return csv.ToString();
        }
    }

    internal static class SceneProbes
    {
        internal static SceneTracker Tracker;
        internal static void CoroutinePrefix(object __instance, string bundleName, string sceneName, UnityEngine.SceneManagement.LoadSceneMode loadSceneMode, bool allowSceneActivation)
        { try { Tracker?.Start(__instance, bundleName, sceneName, loadSceneMode.ToString(), allowSceneActivation); } catch { try { Tracker?.Error(__instance); } catch { } } }
        internal static void BeginBundle(object owner) { try { Tracker?.BeginBundle(owner); } catch { try { Tracker?.Error(owner); } catch { } } }
        internal static void ObserveBundle(object operation, object owner) { try { Tracker?.ObserveBundle(operation, owner); } catch { try { Tracker?.Error(owner); } catch { } } }
        internal static void BeginUnity(object owner) { try { Tracker?.BeginUnity(owner); } catch { try { Tracker?.Error(owner); } catch { } } }
        internal static void ObserveUnity(AsyncOperation operation, object owner) { try { Tracker?.ObserveUnity(operation, owner); } catch { try { Tracker?.Error(owner); } catch { } } }
        internal static void Progress(float value, object owner) { try { Tracker?.Progress(value, owner); } catch { try { Tracker?.Error(owner); } catch { } } }
        internal static void ActivationRequested(object owner) { try { Tracker?.ActivationRequested(owner); } catch { try { Tracker?.Error(owner); } catch { } } }

        internal static void Install(Harmony harmony, Func<string, Type> findType, List<string> coverage)
        {
            Type type = findType("EFT.AssetsManager.AssetsManager+LoadSceneOperation") ?? throw new TypeLoadException("Native scene operation");
            MethodInfo coroutine = type.GetMethods().Single(m => m.Name == "Coroutine" && m.GetParameters().Length == 6);
            harmony.Patch(coroutine, prefix: new HarmonyMethod(typeof(SceneProbes), nameof(CoroutinePrefix)) { priority = Priority.Last });
            MethodInfo step = DetailProbes.StateMachineStep(coroutine) ?? throw new MissingMethodException("Scene coroutine state machine");
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(SceneProbes), nameof(LoadTranspiler)));
            Type baseType = findType("EFT.AssetsManager.LoadSceneOperation");
            MethodInfo activate = baseType.GetMethod("ActivateScene");
            harmony.Patch(DetailProbes.StateMachineStep(activate), transpiler: new HarmonyMethod(typeof(SceneProbes), nameof(ActivationTranspiler)));
            coverage.Add("scene phases | native scene Coroutine bundle/Unity/progress call sites and entry to actual ActivateScene setter; 512 scenes maximum, independent of general span budget; first64 retain legacy span detail");
        }
        private static FieldInfo Owner(MethodBase step) => step.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(f => f.FieldType == step.DeclaringType.DeclaringType);
        private static CodeInstruction[] OwnerCall(FieldInfo owner, string hook) => new[] {
            new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, owner), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SceneProbes), hook)) };
        internal static IEnumerable<CodeInstruction> LoadTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            RewriteLoad(instructions, Owner(__originalMethod));
        internal static IEnumerable<CodeInstruction> RewriteLoad(IEnumerable<CodeInstruction> instructions, FieldInfo owner)
        {
            var output = new List<CodeInstruction>(); int bundles = 0, unity = 0, progress = 0;
            foreach (var source in instructions)
            {
                var instruction = new CodeInstruction(source);
                var method = instruction.operand as MethodInfo;
                bool call = instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt;
                string before = null, after = null;
                if (call && method?.DeclaringType.FullName == "EFT.AssetsManager.BundlesManager" && method.Name == "LoadBundleAsync")
                { before = nameof(BeginBundle); after = nameof(ObserveBundle); bundles++; }
                else if (call && method?.DeclaringType == typeof(UnityEngine.SceneManagement.SceneManager) && method.Name == "LoadSceneAsync" &&
                    method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(UnityEngine.SceneManagement.LoadSceneMode) }))
                { before = nameof(BeginUnity); after = nameof(ObserveUnity); unity++; }
                else if (call && method?.DeclaringType == typeof(AsyncOperation) && method.Name == "get_progress")
                { after = nameof(Progress); progress++; }
                if (before != null)
                {
                    var entry = OwnerCall(owner, before);
                    entry[0].MoveLabelsFrom(instruction);
                    entry[0].MoveBlocksFrom(instruction);
                    output.AddRange(entry);
                }
                output.Add(instruction); // The native call itself is never replaced.
                if (after != null) { output.Add(new CodeInstruction(OpCodes.Dup)); output.AddRange(OwnerCall(owner, after)); }
            }
            if (bundles != 1 || unity != 2 || progress != 2) throw new InvalidOperationException($"Scene load ABI mismatch: bundle={bundles}, Unity={unity}, progress={progress}");
            return output;
        }
        internal static IEnumerable<CodeInstruction> ActivationTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            RewriteActivation(instructions, Owner(__originalMethod));
        internal static IEnumerable<CodeInstruction> RewriteActivation(IEnumerable<CodeInstruction> instructions, FieldInfo owner)
        {
            var output = new List<CodeInstruction>(); int count = 0;
            foreach (var source in instructions)
            {
                var instruction = new CodeInstruction(source);
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method &&
                    method.DeclaringType == typeof(AsyncOperation) && method.Name == "set_allowSceneActivation")
                {
                    var entry = OwnerCall(owner, nameof(ActivationRequested));
                    entry[0].MoveLabelsFrom(instruction); entry[0].MoveBlocksFrom(instruction);
                    output.AddRange(entry); count++;
                }
                output.Add(instruction); // Preserve the setter and its original arguments, including inline completion.
            }
            if (count != 1) throw new InvalidOperationException("Scene activation ABI mismatch; expected one existing activation setter");
            return output;
        }
    }
}
