using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace FuckThisLoadingScreen
{
    internal sealed class BoundedSceneStats
    {
        internal int Requested, Window, Submitted, Completed, Failed, Peak;
        internal long ElapsedTicks;
        internal string Outcome;
    }

    // Replaces only the additional-scene scheduling task, never the first scene or
    // preset completion/child ordering. No operation is held at Unity's 0.9 gate.
    internal static class BoundedScenes
    {
        internal static Func<bool> Enabled;
        internal static Func<int> Window;
        internal static Func<Func<bool>> CaptureActive;
        // Capture the owning run before any await. The callback must not throw.
        internal static Func<Action<BoundedSceneStats>> BeginReport;
        private static Func<object, bool> allowActivation;
        private static Func<object, CancellationToken> cancellation;
        private static Func<object, IList> operations;

        private static class Native<TManager, TKey, TOperation> where TOperation : IOperation
        {
            internal static Func<object, TManager> Manager;
            internal static Func<TManager, TKey, LoadSceneMode, bool, Action<float>, TOperation> Load;
        }

        internal static bool CanReplace(bool enabled, bool permitsActivation) => enabled && permitsActivation;

        private static bool Prefix<TManager, TKey, TOperation>(object __instance, IList<TKey> scenes,
            IProgress<float> progress, ref Task __result) where TOperation : IOperation
        {
            if (!CanReplace(Enabled?.Invoke() == true, allowActivation(__instance)) || CaptureActive == null) return true;
            Func<bool> active = CaptureActive();
            if (active == null || !active()) return true;
            var manager = Native<TManager, TKey, TOperation>.Manager(__instance);
            IList nativeOperations = operations(__instance);
            CancellationToken token = cancellation(__instance);
            var stats = new BoundedSceneStats();
            Action<BoundedSceneStats> finished = BeginReport?.Invoke();
            int window = Math.Max(1, Math.Min(4, Window?.Invoke() ?? 2));
            __result = Run(scenes.Count, window, token, active,
                index =>
                {
                    TOperation operation = Native<TManager, TKey, TOperation>.Load(manager, scenes[index], LoadSceneMode.Additive, true, null);
                    // Keep every original operation for native error aggregation, final
                    // activation/progress accounting, and later unloading/reference use.
                    nativeOperations.Add(operation);
                    return AwaitOperation(operation, stats);
                }, progress, stats, finished);
            return false;
        }

        private static Task AwaitOperation(IOperation operation, BoundedSceneStats stats)
        {
            if (operation.Completed)
            {
                if (operation.Failed) stats.Failed++;
                return Task.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>();
            int completed = 0;
            Action complete = null;
            complete = () =>
            {
                if (Interlocked.Exchange(ref completed, 1) != 0) return;
                operation.CompletedEvent -= complete;
                if (operation.Failed) stats.Failed++;
                completion.TrySetResult(true);
            };
            operation.CompletedEvent += complete;
            // Native events normally run on Unity's main thread. Cover a completion
            // between the initial check and subscription without retaining the handler.
            if (operation.Completed) complete();
            return completion.Task;
        }

        // Deliberately small pure scheduling core, also used by deterministic fixtures.
        // launch(0) is never called: ScenesLoadCoroutine already loaded the first scene.
        internal static async Task Run(int sceneCount, int window, CancellationToken token,
            Func<bool> active, Func<int, Task> launch, IProgress<float> progress, BoundedSceneStats stats,
            Action<BoundedSceneStats> finished = null)
        {
            stats.Requested = Math.Max(0, sceneCount - 1);
            stats.Window = Math.Max(1, Math.Min(4, window));
            var pending = new List<Task>(stats.Window);
            long started = Stopwatch.GetTimestamp();
            int next = 1;
            ExceptionDispatchInfo fault = null;
            bool scopeEnded = false;
            try
            {
                while (next < sceneCount || pending.Count != 0)
                {
                    // Stop admission on cancellation, scope shutdown, or a launch/
                    // progress fault. In-flight Unity operations cannot be canceled;
                    // drain them before completing this task or moving to child presets.
                    if (next < sceneCount && !token.IsCancellationRequested && fault == null && !active()) scopeEnded = true;
                    while (next < sceneCount && pending.Count < stats.Window &&
                        !token.IsCancellationRequested && !scopeEnded && fault == null)
                    {
                        try
                        {
                            if (!active()) { scopeEnded = true; break; }
                            Task task = launch(next);
                            if (task == null) throw new InvalidOperationException("Native scene completion task is null.");
                            next++;
                            stats.Submitted++;
                            pending.Add(task);
                            int inFlight = 0;
                            foreach (Task item in pending) if (!item.IsCompleted) inFlight++;
                            stats.Peak = Math.Max(stats.Peak, inFlight);
                        }
                        catch (Exception error) { fault = ExceptionDispatchInfo.Capture(error); }
                    }
                    if (pending.Count == 0) break;
                    await Task.WhenAny(pending);
                    // Process all completions before replenishing. Iterate in original
                    // submission order even if several completions arrive in one frame.
                    for (int i = 0; i < pending.Count;)
                    {
                        Task task = pending[i];
                        if (!task.IsCompleted) { i++; continue; }
                        pending.RemoveAt(i);
                        try
                        {
                            await task;
                            stats.Completed++;
                            if (fault == null && !token.IsCancellationRequested && !scopeEnded) progress?.Report(0.5f);
                        }
                        catch (Exception error) { if (fault == null) fault = ExceptionDispatchInfo.Capture(error); }
                    }
                }
                if (fault != null) { stats.Outcome = "faulted"; fault.Throw(); }
                if (token.IsCancellationRequested) stats.Outcome = "canceled";
                else if (scopeEnded)
                {
                    stats.Outcome = "scope-ended";
                    throw new OperationCanceledException("Bounded scene loading scope ended before submission completed.");
                }
                else stats.Outcome = "completed";
            }
            finally
            {
                if (stats.Outcome == null) stats.Outcome = "faulted";
                stats.ElapsedTicks = Stopwatch.GetTimestamp() - started;
                // Telemetry cannot change scene loading outcomes.
                try { finished?.Invoke(stats); } catch { }
                // Completed Tasks may be retained by callers. Do not let their state
                // machines retain resource keys, managers, or native operation lists.
                launch = null; active = null; progress = null; finished = null;
                pending.Clear();
            }
        }

        private static Func<object, T> Field<T>(Type owner, string name)
        {
            FieldInfo field = AccessTools.Field(owner, name);
            if (field == null || !typeof(T).IsAssignableFrom(field.FieldType))
                throw new MissingFieldException(owner.FullName, name + " bounded scene ABI");
            var instance = Expression.Parameter(typeof(object), "instance");
            return Expression.Lambda<Func<object, T>>(Expression.Convert(
                Expression.Field(Expression.Convert(instance, owner), field), typeof(T)), instance).Compile();
        }

        private static void Bind<TManager, TKey, TOperation>(Type preset, MethodInfo load) where TOperation : IOperation
        {
            Native<TManager, TKey, TOperation>.Manager = Field<TManager>(preset, "_assetsManager");
            Native<TManager, TKey, TOperation>.Load =
                (Func<TManager, TKey, LoadSceneMode, bool, Action<float>, TOperation>)Delegate.CreateDelegate(
                    typeof(Func<TManager, TKey, LoadSceneMode, bool, Action<float>, TOperation>), load);
        }

        internal static void Install(Harmony harmony, Func<string, Type> find)
        {
            Type preset = find("EFT.LoadScenesFromPresetOperation");
            Type manager = find("EFT.AssetsManager.IAssetsManager"), key = find("EFT.ResourceKey");
            Type operation = find("EFT.AssetsManager.LoadSceneOperation");
            Type extension = find("EFT.AssetsManagerExtension");
            if (!typeof(IOperation).IsAssignableFrom(operation)) throw new InvalidOperationException("Bounded scene operation ABI");
            Type list = typeof(IList<>).MakeGenericType(key);
            MethodInfo direct = AccessTools.DeclaredMethod(preset, "DirectLoadCoroutine", new[] { list, typeof(IProgress<float>) });
            MethodInfo load = AccessTools.DeclaredMethod(extension, "LoadScene", new[] { manager, key, typeof(LoadSceneMode), typeof(bool), typeof(Action<float>) });
            if (direct?.ReturnType != typeof(Task) || load?.ReturnType != operation || !load.IsStatic)
                throw new MissingMethodException("Bounded scene scheduling ABI");
            allowActivation = Field<bool>(preset, "_allowSceneActivation");
            cancellation = Field<CancellationToken>(preset, "_cancellationToken");
            operations = Field<IList>(preset, "_loadSceneOperations");
            var types = new[] { manager, key, operation };
            AccessTools.Method(typeof(BoundedScenes), nameof(Bind)).MakeGenericMethod(types).Invoke(null, new object[] { preset, load });
            harmony.Patch(direct, prefix: new HarmonyMethod(AccessTools.Method(typeof(BoundedScenes), nameof(Prefix)).MakeGenericMethod(types)));
        }
    }
}
