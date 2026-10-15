using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace FTLS.LevelStageTimer
{
    internal sealed class RetentionRow
    {
        internal Capture Capture;
        internal Recorder Owner;
        internal WeakReference List;
        internal CancellationToken Cancellation;
        internal int Id, Threshold;
        internal long Start, End, Roots, Visits;
        internal string Outcome = "open";
    }

    // Only synchronous kickoff entries and the original outer Task are observed. No traversal, equality,
    // ordering, cancellation, yielding, original Task or exception is replaced.
    internal static class RetentionProbes
    {
        internal const int Limit = 256;
        private static readonly ConditionalWeakTable<object, RetentionRow> Lists = new ConditionalWeakTable<object, RetentionRow>();
        [ThreadStatic] private static RetentionRow current;
        private static FieldInfo threshold;
        internal struct Scope { internal RetentionRow Previous, Row; }

        internal static void Install(Harmony harmony, Func<string, Type> findType, List<string> coverage)
        {
            Type graph = findType("Diz.DependencyManager.DependencyGraph`1").MakeGenericType(findType("Diz.Resources.IEasyBundle"));
            InstallGraph(harmony, graph);
            coverage.Add("retention.csv | closed IEasyBundle RetainSeparateAsync; actual root recursive entries and all recursive kickoff entries; membership and yield columns unavailable (no MoveNext patches); 256 records; elapsed includes token construction and observer scheduling");
        }

        internal static void InstallGraph(Harmony harmony, Type graph)
        {
            threshold = graph.GetField("RETAIN_COUNT_FOR_AWAIT");
            MethodInfo outer = AccessTools.DeclaredMethod(graph, "RetainSeparateAsync");
            MethodInfo recursive = AccessTools.DeclaredMethod(graph, "RetainRecursiveAsync");
            if (threshold == null || outer == null || recursive == null) throw new MissingMethodException("Retention probe contract");
            harmony.Patch(outer, prefix: new HarmonyMethod(typeof(RetentionProbes), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(RetentionProbes), nameof(Returned)),
                finalizer: new HarmonyMethod(typeof(RetentionProbes), nameof(FinalizeCall)));
            harmony.Patch(recursive, transpiler: new HarmonyMethod(typeof(RetentionProbes), nameof(EntryTranspiler)));


        }

        internal static void Begin(object[] __args, out Scope __state)
        {
            __state = new Scope { Previous = current };
            try
            {
                Recorder owner = LoadingHooks.Recorder;
                Capture capture = owner?.ActiveCapture;
                if (capture != null) lock (capture)
                {
                    if (!capture.Sealed)
                    {
                        if (capture.Retentions.Count >= Limit) capture.DroppedRetentions++;
                        else
                        {
                            var row = new RetentionRow { Capture = capture, Owner = owner, Id = capture.Retentions.Count + 1,
                                Start = owner.Timestamp(), Threshold = (int)threshold.GetValue(null), Cancellation = (CancellationToken)__args[2] };
                            capture.Retentions.Add(row); __state.Row = row;
                        }
                    }
                }
            }
            catch { ObserverError(__state.Row); }
            current = __state.Row;
        }

        internal static void Returned(Task __result, Scope __state)
        {
            current = __state.Previous;
            RetentionRow row = __state.Row;
            if (row == null) return;
            try { __result.ContinueWith(t => Finish(row, t.IsFaulted ? "faulted" : t.IsCanceled ? "task-canceled" : row.Cancellation.IsCancellationRequested ? "returned-cancellation-requested" : "completed"), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
            catch { ObserverError(row); Finish(row, "observer-error"); }
        }
        internal static void FinalizeCall(Exception __exception, Scope __state)
        {
            current = __state.Previous;
            if (__exception != null) Finish(__state.Row, "threw");
        }
        private static void Finish(RetentionRow row, string outcome)
        {
            if (row == null) return;
            try { lock (row.Capture)
            {
                if (!row.Capture.Sealed && row.Outcome == "open") { row.End = row.Owner.Timestamp(); row.Outcome = outcome; }
                object list = row.List?.Target;
                if (list != null) Lists.Remove(list);
                row.List = null;
            } } catch { ObserverError(row); }
        }
        private static void ObserverError(RetentionRow row = null)
        {
            try
            {
                Capture capture = row?.Capture ?? LoadingHooks.Recorder?.ActiveCapture;
                if (capture != null) Interlocked.Increment(ref capture.RetentionObserverErrors);
            }
            catch { }
        }
        internal static void Visit(object list)
        {
            RetentionRow row = null;
            try
            {
                if (list == null) return;
                if (!Lists.TryGetValue(list, out row) && current != null)
                {
                    row = current; Lists.Add(list, row); row.List = new WeakReference(list);
                }
                if (row == null) return;
                lock (row.Capture) if (!row.Capture.Sealed && row.Outcome == "open")
                {
                    row.Visits++;
                    // Native traversal clears this list before each root and adds a node before recursing.
                    if (((System.Collections.ICollection)list).Count == 0) row.Roots++;
                }
            }
            catch { ObserverError(row); }
        }
        internal static IEnumerable<CodeInstruction> EntryTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            // Static recursive kickoff executes once, unlike resumed MoveNext.
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RetentionProbes), nameof(Visit)));
            foreach (var instruction in instructions) yield return instruction;
        }
        internal static string Export(Capture capture)
        {
            var csv = new StringBuilder("id,start_ms,elapsed_ms,roots_started,recursive_entries,membership_checks,admissions_per_root_sum,repeated_visits,actual_yields,threshold_at_entry,outcome\n");
            lock (capture) foreach (RetentionRow row in capture.Retentions)
            {
                csv.Append(row.Id).Append(',').Append(row.Owner.Milliseconds(row.Start - capture.Start).ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.End == 0 ? "" : row.Owner.Milliseconds(row.End - row.Start).ToString("F3", CultureInfo.InvariantCulture)).Append(',').Append(row.Roots).Append(',').Append(row.Visits).Append(',')
                    .Append(",,,,").Append(row.Threshold).Append(',')
                    .Append(row.Outcome == "open" ? "incomplete-at-export" : row.Outcome).Append('\n');
                object list = row.List?.Target; if (list != null) Lists.Remove(list); row.List = null;
            }
            return csv.ToString();
        }
    }
}
