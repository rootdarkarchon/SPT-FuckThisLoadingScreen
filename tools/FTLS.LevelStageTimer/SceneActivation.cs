using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Events;

namespace FTLS.LevelStageTimer
{
    internal sealed class LifecycleTotals
    {
        public long Calls, Completed, Threw, InclusiveTicks, SelfTicks, MaxTicks, CrossedCompletion;
    }
    internal readonly struct LifecycleKey : IEquatable<LifecycleKey>
    {
        public readonly int Scene, Method, Window;
        public LifecycleKey(int scene, int method, int window) { Scene = scene; Method = method; Window = window; }
        public bool Equals(LifecycleKey other) => Scene == other.Scene && Method == other.Method && Window == other.Window;
        public override bool Equals(object other) => other is LifecycleKey key && Equals(key);
        public override int GetHashCode() => (Scene * 397 ^ Method) * 397 ^ Window;
    }
    internal sealed class LifecycleRow
    {
        public ScenePhase Scene;
        public string Method;
        public int Window;
        public readonly LifecycleTotals Totals = new LifecycleTotals();
    }
    internal sealed class LifecycleDetail
    {
        public ScenePhase Scene;
        public string Method, Outcome;
        public int Window, Depth;
        public long Start, Duration, Self;
        public bool CrossedCompletion;
    }
    internal struct LifecycleToken
    {
        internal ActivationRecorder Owner;
        internal int Slot;
        internal long Sequence;
    }
    internal struct LifecycleFrame
    {
        public ActivationRecorder Owner;
        public Capture Capture;
        public ScenePhase Scene;
        public LifecycleRow Row;
        public LifecycleTotals Summary;
        public string Method;
        public long Start, Children, Sequence;
        public int Window;
        public bool CompletedAtEntry;
    }

    internal sealed class ActivationRecorder
    {
        internal const int MaxMethods = 1024, MaxHandlers = 128, MaxRows = 8192, MaxPinewood = 256, MaxDepth = 128;
        private readonly Recorder recorder;
        private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        private readonly Dictionary<MethodBase, int> methods;
        private readonly Dictionary<MethodBase, int> handlers = new Dictionary<MethodBase, int>();
        private readonly string[] names;
        [ThreadStatic] private static LifecycleFrame[] stack;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static long sequence;
        internal static readonly string[] Windows = { "unity-loading", "held-ready", "activation", "after-completion", "scene-loaded-dispatch", "scene-loaded-handler" };
        public ActivationRecorder(Recorder recorder, MethodInfo[] callbacks)
        {
            this.recorder = recorder;
            methods = callbacks.Select((method, i) => new { method, i }).ToDictionary(p => (MethodBase)p.method, p => p.i);
            names = new string[MaxMethods + MaxHandlers];
            for (int i = 0; i < callbacks.Length; i++) names[i] = callbacks[i].DeclaringType.FullName + "." + callbacks[i].Name;
        }
        public LifecycleToken Begin(Component component, MethodBase method)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) return default;
            Capture capture = recorder.ActiveCapture;
            if (capture == null || capture.ScenePhases.Count == 0 || !methods.TryGetValue(method, out int id)) return default;
            // Read the object's actual scene once, before the callback can move/destroy it.
            Scene scene = component.gameObject.scene;
            return BeginScene(capture, scene.path, scene.handle, id, false);
        }
        public LifecycleToken BeginDispatcher(Scene scene, MethodBase method)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) return default;
            Capture capture = recorder.ActiveCapture;
            if (capture == null || capture.ScenePhases.Count == 0 || !methods.TryGetValue(method, out int id)) return default;
            return BeginScene(capture, scene.path, scene.handle, id, true);
        }
        public bool ObserveHandlers => Thread.CurrentThread.ManagedThreadId == mainThread && recorder.ActiveCapture != null;
        public LifecycleToken BeginHandler(Scene scene, Delegate callback)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) return default;
            Capture capture = recorder.ActiveCapture;
            if (capture == null || capture.ScenePhases.Count == 0) return default;
            MethodInfo method = callback.Method;
            string assembly = method.Module.Assembly.GetName().Name;
            if (assembly.StartsWith("Orbit", StringComparison.OrdinalIgnoreCase)) { capture.OutOfScopeSceneHandlerCalls++; return default; }
            if (!handlers.TryGetValue(method, out int id))
            {
                if (handlers.Count >= MaxHandlers) { capture.DroppedSceneHandlerCalls++; return default; }
                id = MaxMethods + handlers.Count; handlers.Add(method, id);
                string name = assembly + " | " + method.DeclaringType?.FullName + "." + method.Name;
                names[id] = name.Length <= 512 ? name : name.Substring(0, 512);
            }
            capture.SceneHandlerMethods.Add(id);
            return BeginScene(capture, scene.path, scene.handle, id, false, 5);
        }
        // Model seam: no native scene creation is required to validate attribution/nesting.
        internal LifecycleToken BeginScene(Capture capture, string path, int handle, int method, bool dispatcher, int specialWindow = -1)
        {
            if (capture.Sealed) return default;
            if (string.IsNullOrEmpty(path) || !capture.SceneLookup.TryGetValue(path, out ScenePhase scene)) { capture.UnmatchedLifecycleCalls++; return default; }
            if (scene == null || scene.SceneHandle.HasValue && scene.SceneHandle != handle) { capture.AmbiguousLifecycleCalls++; return default; }
            scene.SceneHandle = handle;
            if (depth >= MaxDepth) { capture.DroppedLifecycleDepth++; return default; }
            int window = specialWindow >= 0 ? specialWindow : dispatcher ? 4 : scene.UnityDone.HasValue ? 3 : scene.ActivationStart.HasValue ? 2 : scene.ReadyObserved ? 1 : 0;
            var key = new LifecycleKey(scene.Id, method, window);
            if (!capture.LifecycleRows.TryGetValue(key, out LifecycleRow row))
            {
                if (capture.LifecycleRows.Count < MaxRows)
                {
                    row = new LifecycleRow { Scene = scene, Method = names[method], Window = window };
                    capture.LifecycleRows.Add(key, row);
                }
                else capture.DroppedLifecycleDetails++;
            }
            LifecycleTotals summary = window == 4 ? scene.SceneLoadedDispatch : window == 5 ? scene.SceneLoadedHandlers : scene.Lifecycle[window];
            summary.Calls++; if (row != null) row.Totals.Calls++;
            if (stack == null) stack = new LifecycleFrame[MaxDepth];
            long serial = ++sequence;
            stack[depth] = new LifecycleFrame { Owner = this, Capture = capture, Scene = scene, Row = row, Summary = summary,
                Method = names[method], Window = window, Sequence = serial, CompletedAtEntry = scene.UnityDone.HasValue, Start = recorder.Timestamp() };
            return new LifecycleToken { Owner = this, Slot = depth++, Sequence = serial };
        }
        private static void Add(LifecycleTotals totals, long elapsed, long self, bool threw, bool crossed)
        {
            if (threw) totals.Threw++; else totals.Completed++;
            totals.InclusiveTicks += elapsed; totals.SelfTicks += self; totals.MaxTicks = Math.Max(totals.MaxTicks, elapsed);
            if (crossed) totals.CrossedCompletion++;
        }
        public void End(LifecycleToken token, bool threw)
        {
            long end = recorder.Timestamp();
            if (token.Owner != this || depth != token.Slot + 1 || stack[token.Slot].Sequence != token.Sequence)
            { Error(); return; }
            LifecycleFrame frame = stack[--depth]; stack[depth] = default;
            long elapsed = Math.Max(0, end - frame.Start), self = Math.Max(0, elapsed - frame.Children);
            if (depth > 0) stack[depth - 1].Children += elapsed;
            if (frame.Capture.Sealed) return;
            bool crossed = !frame.CompletedAtEntry && frame.Scene.UnityDone.HasValue;
            Add(frame.Summary, elapsed, self, threw, crossed);
            if (frame.Row != null) Add(frame.Row.Totals, elapsed, self, threw, crossed);
            if (frame.Scene.Scene?.EndsWith("City_SE_01_Pinewood_Hotel_indoor.unity", StringComparison.OrdinalIgnoreCase) != true) return;
            Capture capture = frame.Capture; capture.PinewoodCalls++;
            // Retain the slowest calls, so early cheap callbacks cannot crowd out a later stall.
            if (capture.PinewoodDetails.Count >= MaxPinewood && elapsed <= capture.PinewoodMinimumTicks) return;
            var detail = new LifecycleDetail { Scene = frame.Scene, Method = frame.Method, Window = frame.Window, Depth = token.Slot,
                Start = frame.Start, Duration = elapsed, Self = self, Outcome = threw ? "threw" : "completed", CrossedCompletion = crossed };
            if (capture.PinewoodDetails.Count < MaxPinewood) capture.PinewoodDetails.Add(detail);
            else capture.PinewoodDetails[capture.PinewoodMinimumIndex] = detail;
            if (capture.PinewoodDetails.Count == MaxPinewood)
            {
                int minimum = 0;
                for (int i = 1; i < MaxPinewood; i++) if (capture.PinewoodDetails[i].Duration < capture.PinewoodDetails[minimum].Duration) minimum = i;
                capture.PinewoodMinimumIndex = minimum; capture.PinewoodMinimumTicks = capture.PinewoodDetails[minimum].Duration;
            }
        }
        public void Error() { Capture capture = recorder.ActiveCapture; if (capture != null) capture.LifecycleObserverErrors++; }
        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private string Ms(long ticks) => recorder.Milliseconds(ticks).ToString("F3", CultureInfo.InvariantCulture);
        private void Totals(StringBuilder text, LifecycleTotals totals) => text.Append(totals.Calls).Append(',').Append(totals.Completed).Append(',').Append(totals.Threw).Append(',')
            .Append(totals.Calls - totals.Completed - totals.Threw).Append(',').Append(Ms(totals.InclusiveTicks)).Append(',').Append(Ms(totals.SelfTicks)).Append(',').Append(Ms(totals.MaxTicks)).Append(',').Append(totals.CrossedCompletion);
        public string ExportMethods(Capture capture, bool handlersOnly = false)
        {
            var text = new StringBuilder("scene_id,scene,entry_window,method,calls,completed,threw,incomplete_calls,inclusive_ms,self_ms,max_ms,crossed_completion\n");
            foreach (var row in capture.LifecycleRows.Values.OrderBy(r => r.Scene.Id).ThenBy(r => r.Window).ThenBy(r => r.Method))
            {
                if (handlersOnly && row.Window != 5) continue;
                text.Append(row.Scene.Id).Append(',').Append(Csv(row.Scene.Scene)).Append(',').Append(Windows[row.Window]).Append(',').Append(Csv(row.Method)).Append(',');
                Totals(text, row.Totals); text.Append('\n');
            }
            return text.ToString();
        }
        public string ExportScenes(Capture capture)
        {
            var text = new StringBuilder("scene_id,scene,entry_window,calls,completed,threw,incomplete_calls,inclusive_ms,self_ms,max_ms,crossed_completion\n");
            foreach (var scene in capture.ScenePhases)
                for (int window = 0; window < Windows.Length; window++)
                {
                    text.Append(scene.Id).Append(',').Append(Csv(scene.Scene)).Append(',').Append(Windows[window]).Append(',');
                    Totals(text, window == 4 ? scene.SceneLoadedDispatch : window == 5 ? scene.SceneLoadedHandlers : scene.Lifecycle[window]); text.Append('\n');
                }
            return text.ToString();
        }
        public string ExportPinewood(Capture capture)
        {
            var text = new StringBuilder("scene_id,scene,entry_window,method,start_ms,duration_ms,self_ms,depth,outcome,crossed_completion\n");
            foreach (var row in capture.PinewoodDetails.OrderByDescending(r => r.Duration))
                text.Append(row.Scene.Id).Append(',').Append(Csv(row.Scene.Scene)).Append(',').Append(Windows[row.Window]).Append(',').Append(Csv(row.Method)).Append(',')
                    .Append(Ms(row.Start - capture.Start)).Append(',').Append(Ms(row.Duration)).Append(',').Append(Ms(row.Self)).Append(',').Append(row.Depth).Append(',').Append(row.Outcome).Append(',').Append(row.CrossedCompletion).Append('\n');
            return text.ToString();
        }
    }

    internal static class ActivationProbes
    {
        internal static ActivationRecorder Recorder;
        internal static MethodInfo[] Discover(Assembly game, out int generic, out int nonvoid)
        {
            generic = 0; nonvoid = 0;
            var methods = new List<MethodInfo>();
            foreach (Type type in game.GetTypes().Where(t => typeof(MonoBehaviour).IsAssignableFrom(t)))
                foreach (MethodInfo method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!(method.Name == "Awake" || method.Name == "OnEnable" || method.Name == "Start") || method.GetParameters().Length != 0 || method.IsAbstract || method.GetMethodBody() == null) continue;
                    if (method.ContainsGenericParameters) { generic++; continue; }
                    if (method.ReturnType != typeof(void)) { nonvoid++; continue; }
                    methods.Add(method);
                }
            MethodInfo[] result = methods.Distinct().OrderBy(m => m.DeclaringType.FullName, StringComparer.Ordinal).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray();
            if (result.Length + 1 > ActivationRecorder.MaxMethods) throw new NotSupportedException("Scene lifecycle method budget exceeded (including dispatcher): " + (result.Length + 1));
            return result;
        }
        internal static ActivationRecorder Install(Harmony harmony, Assembly game, Recorder recorder, List<string> coverage, out string manifest)
        {
            MethodInfo[] methods = Discover(game, out int generic, out int nonvoid);
            MethodInfo dispatch = typeof(SceneManager).GetMethod("Internal_SceneLoaded", BindingFlags.Static | BindingFlags.NonPublic);
            if (dispatch == null || dispatch.ReturnType != typeof(void) || !dispatch.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Scene), typeof(LoadSceneMode) }))
                throw new MissingMethodException("SceneManager.Internal_SceneLoaded");
            var observer = new ActivationRecorder(recorder, methods.Concat(new[] { dispatch }).ToArray());
            foreach (MethodInfo method in methods)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ActivationProbes), nameof(Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ActivationProbes), nameof(Finalizer)) { priority = Priority.Last });
            harmony.Patch(dispatch, prefix: new HarmonyMethod(typeof(ActivationProbes), nameof(DispatchPrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(ActivationProbes), nameof(Finalizer)) { priority = Priority.Last },
                transpiler: new HarmonyMethod(typeof(ActivationProbes), nameof(SceneHandlerTranspiler)));
            coverage.Add($"scene lifecycle | Assembly-CSharp only: {methods.Length} closed void Awake/OnEnable/Start methods; skipped {generic} open generic and {nonvoid} nonvoid methods; plus managed sceneLoaded dispatcher and per-handler delivery (128 method identities maximum; ORBIT omitted)");
            manifest = "FTLS Level Stage Timer lifecycle coverage\n" + coverage.Last() + "\n" + string.Join("\n", methods.Concat(new[] { dispatch }).Select(m => m.DeclaringType.FullName + "." + m.Name));
            return observer;
        }
        internal static IEnumerable<CodeInstruction> SceneHandlerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.Select(i => new CodeInstruction(i)).ToList();
            var invoke = typeof(UnityAction<Scene, LoadSceneMode>).GetMethod("Invoke");
            var matches = list.Where(i => i.Calls(invoke)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Scene-loaded delivery ABI mismatch; expected exactly one UnityAction<Scene,LoadSceneMode>.Invoke");
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.Method(typeof(ActivationProbes), nameof(DeliverSceneLoaded));
            return list; // Original stack arguments, labels and exception regions are retained.
        }
        internal static void DeliverSceneLoaded(UnityAction<Scene, LoadSceneMode> callback, Scene scene, LoadSceneMode mode)
        {
            ActivationRecorder observer = Recorder;
            if (callback == null || observer == null || !observer.ObserveHandlers) { callback(scene, mode); return; }
            Delegate[] snapshot;
            try { snapshot = callback.GetInvocationList(); }
            catch { try { observer.Error(); } catch { } callback(scene, mode); return; }
            foreach (Delegate handler in snapshot)
            {
                LifecycleToken token = default;
                try { token = observer.BeginHandler(scene, handler); } catch { try { observer.Error(); } catch { } }
                bool returned = false;
                try { ((UnityAction<Scene, LoadSceneMode>)handler)(scene, mode); returned = true; }
                finally
                {
                    try { token.Owner?.End(token, !returned); } catch { try { observer.Error(); } catch { } }
                }
            }
        }
        internal static void Prefix(Component __instance, MethodBase __originalMethod, out LifecycleToken __state)
        {
            __state = default;
            try { if (Recorder != null) __state = Recorder.Begin(__instance, __originalMethod); } catch { try { Recorder?.Error(); } catch { } }
        }
        internal static void DispatchPrefix(Scene scene, MethodBase __originalMethod, out LifecycleToken __state)
        {
            __state = default;
            try { if (Recorder != null) __state = Recorder.BeginDispatcher(scene, __originalMethod); } catch { try { Recorder?.Error(); } catch { } }
        }
        internal static void Finalizer(LifecycleToken __state, Exception __exception)
        {
            try { __state.Owner?.End(__state, __exception != null); } catch { try { __state.Owner?.Error(); } catch { } }
            // A void finalizer observes the original exception without replacing/suppressing it.
        }
    }
}
