using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace FTLS.LevelStageTimer
{
    internal sealed class Hook
    {
        public string Stage, Kind;
        public bool Root;
        public int CountArgument = -1;
        public int MaxCalls;
        public bool Aggregate;
    }

    internal static class LoadingHooks
    {
        internal static Recorder Recorder;
        internal static object RootInstance;
        internal static readonly Dictionary<MethodBase, Hook> Hooks = new Dictionary<MethodBase, Hook>();

        internal static void AggregatePrefix(MethodBase __originalMethod, out AggregateToken __state)
        {
            __state = default;
            try
            {
                if (Recorder != null && Hooks.TryGetValue(__originalMethod, out Hook hook))
                    __state = Recorder.BeginAggregate(hook.Stage, hook.Kind, detailLimit: hook.MaxCalls);
            }
            catch { }
        }

        internal static void AggregatePostfix(ref AggregateToken __state)
        {
            try { __state.Owner?.FinishAggregate(ref __state, false); } catch { }
        }

        internal static void AggregateFinalizer(Exception __exception, ref AggregateToken __state)
        {
            if (__exception != null) { try { __state.Owner?.FinishAggregate(ref __state, true); } catch { } }
        }

        // Async state machines can be value types. Avoid Harmony boxing __instance
        // for their MoveNext methods; no receiver or argument inspection is needed.
        internal static void StepPrefix(MethodBase __originalMethod, out Span __state)
        {
            __state = null;
            try
            {
                if (Recorder != null && Hooks.TryGetValue(__originalMethod, out Hook hook))
                    __state = Recorder.Begin(hook.Stage, hook.Kind, limit: hook.MaxCalls);
            }
            catch { }
        }

        internal static void Prefix(MethodBase __originalMethod, object __instance, object[] __args, out Span __state)
        {
            __state = null;
            try
            {
                if (Recorder == null || !Hooks.TryGetValue(__originalMethod, out Hook hook)) return;
                // The getter is declared on the base token; time only grouped resource requests.
                if (hook.Stage == "resources.bundle-job" && (__instance == null || __instance.GetType().Name != "TokenContainer")) return;
                int count = hook.CountArgument >= 0 && __args[hook.CountArgument] is ICollection collection ? collection.Count : -1;
                __state = Recorder.Begin(hook.Stage, hook.Kind, hook.Root, count, hook.MaxCalls);
                if (__state != null && hook.Root) RootInstance = __instance;
            }
            catch { /* Diagnostics must not change game control flow. */ }
        }

        internal static Span BeginDetail(string stage, int count = -1, string detail = null)
        {
            try { return Recorder?.Begin(stage, "work", count: count, limit: 64, detail: detail); }
            catch { return null; }
        }

        internal static void EndDetail(Span span, bool completed)
        {
            try { span?.Finish(completed ? "completed" : "threw"); } catch { }
        }

        internal static void TaskPostfix(Task __result, Span __state)
        {
            if (__state == null) return;
            try
            {
                if (__result == null) { __state.Finish("null-task"); return; }
                // Observe status only. Keep the exact Task and its exception-observation semantics.
                __result.ContinueWith(task =>
                {
                    try { __state.Finish(task.IsCanceled ? "canceled" : task.IsFaulted ? "faulted" : "completed"); }
                    catch { }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { __state.Finish("observer-failed"); }
        }

        internal static void SyncPostfix(Span __state)
        {
            try { __state?.Finish("completed"); } catch { }
        }

        internal static void EnumeratorPostfix(ref IEnumerator __result, Span __state)
        {
            if (__state == null) return;
            if (__result == null) { __state.Finish("null-enumerator"); return; }
            __result = new TimedEnumerator(__result, __state);
        }

        internal static void Finalizer(Exception __exception, Span __state)
        {
            if (__exception != null) { try { __state?.Finish("threw"); } catch { } }
        }

        internal static void MarkerCreatedPostfix(object __instance) { try { Recorder?.AttachMarker(__instance); } catch { } }
        internal static void StartedPostfix(object __instance) { try { Recorder?.GameStarted(__instance); } catch { } }

        private sealed class TimedEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator inner;
            private readonly Span span;
            public TimedEnumerator(IEnumerator inner, Span span) { this.inner = inner; this.span = span; }
            public object Current => inner.Current;
            public bool MoveNext()
            {
                try
                {
                    bool next = inner.MoveNext();
                    if (!next) span.Finish("completed");
                    return next;
                }
                catch { span.Finish("threw"); throw; }
            }
            public void Reset() => inner.Reset();
            public void Dispose()
            {
                try { (inner as IDisposable)?.Dispose(); }
                finally { span.Finish("disposed"); }
            }
        }
    }
}
