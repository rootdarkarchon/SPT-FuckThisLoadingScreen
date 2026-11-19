using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    // Managed-only state. The background writer never acquires EFT's tree lock or calls Unity.
    internal sealed class HideoutDecalState
    {
        internal const string Header = "elapsed_ms,worker_thread_id,worker_phase,thread_state,tree_lock_held,tree_lock_age_ms,trees_at_acquisition,last_lock_hold_ms,max_lock_hold_ms,lock_acquisitions";
        private readonly Func<HideoutResearchCapture> capture;
        private readonly Func<int> treeCount;
        private Thread worker;
        private volatile string phase = "not-observed";
        private int held, trees = -1;
        private long acquired, lastHold, maxHold, acquisitions;

        internal HideoutDecalState(Func<HideoutResearchCapture> capture, Func<int> treeCount)
        { this.capture = capture; this.treeCount = treeCount; }

        internal void Started()
        {
            worker = Thread.CurrentThread; Volatile.Write(ref held, 0); phase = "running";
            capture()?.Note("decals.texture-worker", "started thread=" + worker.ManagedThreadId);
        }
        internal void Waiting() { phase = "waiting-tree-lock"; }
        internal void Acquired()
        {
            Interlocked.Exchange(ref acquired, Stopwatch.GetTimestamp());
            Volatile.Write(ref held, 1); phase = "holding-tree-lock"; Interlocked.Increment(ref acquisitions);
            // Called only after the native worker has acquired the lock already.
            try { Volatile.Write(ref trees, treeCount()); } catch { Volatile.Write(ref trees, -1); }
        }
        internal void Released()
        {
            long ticks = Stopwatch.GetTimestamp() - Interlocked.Read(ref acquired);
            Volatile.Write(ref held, 0); Interlocked.Exchange(ref lastHold, ticks);
            if (ticks > Interlocked.Read(ref maxHold)) Interlocked.Exchange(ref maxHold, ticks);
            phase = "running";
        }
        internal void Exited(Exception exception)
        {
            // If a native exception escaped while holding the lock, retain held=true as evidence.
            phase = exception == null ? "exited" : "threw:" + exception.GetType().Name;
            capture()?.Note("decals.texture-worker", phase + " thread=" + Thread.CurrentThread.ManagedThreadId);
        }
        internal HideoutResearchSpan BeginWait(string stage, string detail)
        { try { return capture()?.Begin(stage, detail: detail); } catch { return null; } }
        internal string Snapshot()
        {
            Thread observed = worker;
            string state;
            try { state = observed == null ? "not-observed" : observed.ThreadState.ToString(); } catch { state = "unavailable"; }
            bool locked = Volatile.Read(ref held) != 0;
            double age = locked ? (Stopwatch.GetTimestamp() - Interlocked.Read(ref acquired)) * 1000.0 / Stopwatch.Frequency : 0;
            return (observed?.ManagedThreadId ?? 0) + "," + HideoutResearchCapture.Quote(phase) + "," + HideoutResearchCapture.Quote(state) + "," +
                (locked ? "true" : "false") + "," + HideoutResearchCapture.Number(age) + "," + Volatile.Read(ref trees) + "," +
                HideoutResearchCapture.Number(Interlocked.Read(ref lastHold) * 1000.0 / Stopwatch.Frequency) + "," +
                HideoutResearchCapture.Number(Interlocked.Read(ref maxHold) * 1000.0 / Stopwatch.Frequency) + "," + Interlocked.Read(ref acquisitions);
        }
    }

    internal static class HideoutDecalProbes
    {
        private static HideoutDecalState State => HideoutResearch.Active?.Decals;
        private static readonly MethodInfo Enter = AccessTools.Method(typeof(Monitor), nameof(Monitor.Enter), new[] { typeof(object) });
        private static readonly MethodInfo Exit = AccessTools.Method(typeof(Monitor), nameof(Monitor.Exit), new[] { typeof(object) });
        private static readonly MethodInfo Join = AccessTools.Method(typeof(Thread), nameof(Thread.Join), Type.EmptyTypes);

        internal static HideoutDecalState Bind(Func<string, Type> find, Func<HideoutResearchCapture> capture)
        {
            FieldInfo trees = AccessTools.Field(find("TextureQuadTree"), "_trees") ?? throw new MissingFieldException("TextureQuadTree._trees");
            PropertyInfo count = trees.FieldType.GetProperty("Count") ?? throw new MissingMemberException("TextureQuadTree._trees.Count");
            return new HideoutDecalState(capture, () => (int)count.GetValue(trees.GetValue(null)));
        }
        internal static void Install(Harmony harmony, Type tree)
        {
            MethodInfo dispose = AccessTools.DeclaredMethod(tree, "Dispose", Type.EmptyTypes);
            MethodInfo worker = AccessTools.DeclaredMethod(tree, "WorkThread", new[] { typeof(object) });
            if (dispose?.ReturnType != typeof(void) || worker?.ReturnType != typeof(void) || !worker.IsStatic)
                throw new MissingMethodException("TextureQuadTree disposal/worker ABI");
            harmony.Patch(dispose, transpiler: Hook(nameof(DisposeTranspiler)));
            harmony.Patch(worker, prefix: Hook(nameof(WorkerPrefix)), finalizer: Hook(nameof(WorkerFinalizer)), transpiler: Hook(nameof(WorkerTranspiler)));
        }
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(HideoutDecalProbes), name);

        internal static IEnumerable<CodeInstruction> DisposeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int enters = 0, joins = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (code[i].Calls(Enter))
                {
                    if (i == 0 || !(code[i - 1].operand is FieldInfo field) || field.DeclaringType.Name != "TextureQuadTree" || field.Name != "_treesLock")
                        throw new InvalidOperationException("TextureQuadTree.Dispose tree-lock call-site mismatch");
                    code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(HideoutDecalProbes), nameof(DisposeEnter)); enters++;
                }
                else if (code[i].Calls(Join))
                { code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(HideoutDecalProbes), nameof(DisposeJoin)); joins++; }
            }
            if (enters != 1 || joins != 1) throw new InvalidOperationException("TextureQuadTree.Dispose requires exactly one native Enter and Join");
            return code;
        }
        internal static IEnumerable<CodeInstruction> WorkerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int enters = 0, exits = 0;
            for (int i = 1; i < code.Count; i++)
            {
                bool treeLock = code[i - 1].operand is FieldInfo field && field.DeclaringType.Name == "TextureQuadTree" && field.Name == "_treesLock";
                if (code[i].Calls(Enter))
                {
                    if (!treeLock) throw new InvalidOperationException("Texture worker tree-lock call-site mismatch");
                    code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(HideoutDecalProbes), nameof(WorkerEnter)); enters++;
                }
                else if (code[i].Calls(Exit) && treeLock)
                { code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(typeof(HideoutDecalProbes), nameof(WorkerExit)); exits++; }
            }
            if (enters != 1 || exits != 1) throw new InvalidOperationException("Texture worker requires exactly one global tree-lock Enter/Exit pair");
            return code;
        }

        internal static void DisposeEnter(object sync)
        {
            var span = State?.BeginWait("decals.texture-tree.dispose.lock-wait", "Native Monitor.Enter; thread=" + Thread.CurrentThread.ManagedThreadId);
            string outcome = "threw";
            try { Monitor.Enter(sync); outcome = "acquired"; }
            finally { try { span?.Finish(outcome); } catch { } }
        }
        internal static void DisposeJoin(Thread thread)
        {
            var span = State?.BeginWait("decals.texture-tree.dispose.join", "Native Thread.Join; joining_thread=" + thread?.ManagedThreadId);
            string outcome = "threw";
            try { thread.Join(); outcome = "joined"; }
            finally { try { span?.Finish(outcome); } catch { } }
        }
        internal static void WorkerPrefix() { try { State?.Started(); } catch { } }
        internal static void WorkerFinalizer(Exception __exception) { try { State?.Exited(__exception); } catch { } }
        internal static void WorkerEnter(object sync)
        {
            var state = State;
            try { state?.Waiting(); } catch { }
            Monitor.Enter(sync);
            try { state?.Acquired(); } catch { }
        }
        internal static void WorkerExit(object sync)
        {
            Monitor.Exit(sync);
            try { State?.Released(); } catch { }
        }
    }
}
