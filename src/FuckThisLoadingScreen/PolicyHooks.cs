using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    internal sealed class SchedulingScope
    {
        internal bool Closed;
        internal void Close() { if (!Closed) { Closed = true; PolicyHooks.ParallelSchedulingDepth--; } }
    }

    internal sealed class LootLaunchScope
    {
        internal bool Closed;
        internal void Close() { if (!Closed) { Closed = true; PolicyHooks.LootLaunchDepth--; } }
    }

    internal static class PolicyHooks
    {
        internal static LoadingPolicy Policy;
        internal static Func<float> NativeMeasure;
        internal static MethodInfo NativeMeasureMethod;
        [ThreadStatic] internal static int ParallelSchedulingDepth;
        [ThreadStatic] internal static int LootLaunchDepth;

        internal static void RootPrefix(out LoadingRun __state) { __state = Policy?.Begin(); }
        internal static void RootPostfix(Task __result, LoadingRun __state)
        {
            LoadingPolicy owner = Policy;
            if (owner == null || __state == null) return;
            if (__result == null) { owner.RootFinished(__state, true); return; }
            __result.ContinueWith(task => owner.RootFinished(__state, task.IsCanceled || task.IsFaulted),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal static void RootFinalizer(Exception __exception, LoadingRun __state)
        { if (__exception != null) Policy?.RootFinished(__state, true); }

        internal static void MapPrefix(out MapScope __state) { __state = Policy?.EnterMap(); }
        internal static void MapPostfix(Task __result, MapScope __state)
        {
            LoadingPolicy owner = Policy;
            if (owner == null || __state == null) return;
            if (__result == null) { owner.LeaveMap(__state); return; }
            __result.ContinueWith(task => owner.LeaveMap(__state), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal static void MapFinalizer(Exception __exception, MapScope __state)
        { if (__exception != null) Policy?.LeaveMap(__state); }
        // The native SpawnLoot starts its one bundle/pool Task before its first await.
        // Tag only that synchronous kickoff; never hold thread-local state across awaits.
        internal static void LootLaunchPrefix(out LootLaunchScope __state)
        {
            __state = null;
            if (Policy?.LootBundlePriority == true) { LootLaunchDepth++; __state = new LootLaunchScope(); }
        }
        internal static void LootLaunchPostfix(LootLaunchScope __state) => __state?.Close();
        internal static void LootLaunchFinalizer(Exception __exception, LootLaunchScope __state)
        { if (__exception != null) __state?.Close(); }
        internal static void LootBundlesPrefix(out BundleScope __state)
        { __state = LootLaunchDepth > 0 ? Policy?.EnterLootBundles() : null; }
        internal static void LootBundlesPostfix(Task __result, BundleScope __state)
        {
            LoadingPolicy owner = Policy;
            if (owner == null || __state == null) return;
            if (__result == null) { owner.LeaveLootBundles(__state); return; }
            __result.ContinueWith(task => owner.LeaveLootBundles(__state), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal static void LootBundlesFinalizer(Exception __exception, BundleScope __state)
        { if (__exception != null) Policy?.LeaveLootBundles(__state); }
        internal static void MetricsCreated(object __instance) => Policy?.AttachMetrics(__instance);
        internal static void GameStarted(object __instance) => Policy?.GameStarted(__instance);

        internal static int EffectiveAudioBatchSize() => Policy?.AudioBatchSize ?? 300;
        internal static IEnumerable<CodeInstruction> AudioTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int count = 0;
            for (int i = 0; i + 1 < code.Count; i++)
                if (code[i].LoadsConstant(300) && code[i + 1].opcode == OpCodes.Rem)
                {
                    code[i].opcode = OpCodes.Call;
                    code[i].operand = AccessTools.Method(typeof(PolicyHooks), nameof(EffectiveAudioBatchSize));
                    count++;
                }
            if (count != 1) throw new InvalidOperationException("Audio batch ABI mismatch; expected one modulo-300 divisor.");
            return code;
        }
        internal static float MeasureMemory()
        {
            LoadingPolicy owner = Policy;
            if (owner?.NonCollectingMeasurements == true)
            {
                owner.CountNonCollectingCall();
                // Keep EFT's existing units (MiB despite its helper's GB name).
                return (float)GC.GetTotalMemory(false) / 1024f / 1024f;
            }
            return NativeMeasure();
        }
        internal static IEnumerable<CodeInstruction> MemoryTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int count = 0;
            foreach (CodeInstruction instruction in code)
                if (instruction.Calls(NativeMeasureMethod))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(PolicyHooks), nameof(MeasureMemory));
                    count++;
                }
            if (count != 2) throw new InvalidOperationException("Cleanup ABI mismatch; expected two diagnostic memory calls.");
            return code;
        }

        internal static void PresetPrefix(ref bool loadInParallelMode, bool allowSceneActivation = true)
        {
            if (allowSceneActivation && Policy?.BoundedSceneRun != null) loadInParallelMode = false;
            else if (Policy?.ParallelScenes == true) loadInParallelMode = true;
        }
        internal static void SchedulingPrefix(out SchedulingScope __state)
        {
            __state = null;
            if (Policy?.ParallelScenes == true) { ParallelSchedulingDepth++; __state = new SchedulingScope(); }
        }
        internal static void SchedulingPostfix(SchedulingScope __state) => __state?.Close();
        internal static void SchedulingFinalizer(Exception __exception, SchedulingScope __state)
        { if (__exception != null) __state?.Close(); }
        internal static void ScenePrefix(ref bool allowSceneActivation)
        {
            // Native ParallelLoadCoroutine queues all additional scenes before awaiting any.
            // Holding those scenes at 0.9 would stall Unity's queue. Opt-in parallel mode
            // therefore activates those queued scenes as ready; ordinary loads keep native flags.
            if (ParallelSchedulingDepth > 0 && Policy?.ParallelScenes == true) allowSceneActivation = true;
        }
    }
}
