using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FTLS.LevelStageTimer;
using HarmonyLib;

internal static class RetentionValidation
{
    private sealed class Node { private Node[] children = Array.Empty<Node>(); internal Exception Error; internal Node[] Dependencies { get { if (Error != null) throw Error; return children; } set { children = value; } } }
    private sealed class Graph
    {
        public static int RETAIN_COUNT_FOR_AWAIT = 2, Count;
        internal readonly Dictionary<string, Node> Nodes = new Dictionary<string, Node>();
        internal Exception Error;
        public async Task<int> RetainSeparateAsync(IEnumerable<string> keys, IProgress<float> progress, CancellationToken ct)
        {
            var list = new List<Node>(); int total = 0; Count = 0;
            foreach (string key in keys)
            {
                if (ct.IsCancellationRequested) break;
                list.Clear();
                if (Error != null) throw Error;
                await RetainRecursiveAsync(Nodes[key], list, ct);
                total += list.Count; progress?.Report(total);
            }
            return total;
        }
        public static async Task RetainRecursiveAsync(Node node, List<Node> nodes, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return;
            if (Count >= RETAIN_COUNT_FOR_AWAIT) { Count = 0; await Task.Yield(); }
            Count++;
            if (!nodes.Contains(node))
            {
                nodes.Add(node);
                foreach (Node child in node.Dependencies) await RetainRecursiveAsync(child, nodes, ct);
            }
        }
    }
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
    internal static int Run(string install)
    {
        checks = 0;
        var harmony = new Harmony("ftls.retention.validation");
        var graph = new Graph(); var leaf = new Node(); graph.Nodes["a"] = new Node { Dependencies = new[] { leaf, leaf } }; graph.Nodes["b"] = new Node { Dependencies = new[] { leaf } };
        try
        {
            RetentionProbes.InstallGraph(harmony, typeof(Graph));
            var recorder = new Recorder(); LoadingHooks.Recorder = recorder;
            recorder.Begin("raid", "work", true); Capture capture = recorder.ActiveCapture;
            int total = Task.Run(() => graph.RetainSeparateAsync(new[] { "a", "b" }, null, default)).GetAwaiter().GetResult();
            SpinWait.SpinUntil(() => capture.Retentions.Count == 1 && capture.Retentions[0].Outcome != "open", 1000);
            RetentionRow row = capture.Retentions.Single();
            Check(total == 4 && row.Roots == 2 && row.Visits == 5, "Retention roots and recursive entries match unchanged traversal");
            Check(row.Threshold == 2 && RetentionProbes.Export(capture).Split('\n')[1].Split(',').Skip(5).Take(4).All(v => v == ""), "Threshold snapshot is recorded and unavailable membership/yield counts are blank");
            Check(row.Outcome == "completed" && row.List == null, "Completion releases list association");
            var canceled = new CancellationToken(true);
            Task.Run(() => graph.RetainSeparateAsync(new[] { "a" }, null, canceled)).GetAwaiter().GetResult();
            SpinWait.SpinUntil(() => capture.Retentions.Last().Outcome != "open", 1000);
            row = capture.Retentions.Last();
            Check(row.Roots == 0 && row.Visits == 0 && row.Outcome == "returned-cancellation-requested", "Native cancellation returns normally and records exact zero counts");
            var expected = new InvalidOperationException("identity"); graph.Error = expected;
            Exception seen = null;
            try { Task.Run(() => graph.RetainSeparateAsync(new[] { "a" }, null, default)).GetAwaiter().GetResult(); } catch (Exception error) { seen = error; }
            SpinWait.SpinUntil(() => capture.Retentions.Last().Outcome != "open", 1000);
            Check(ReferenceEquals(seen, expected) && capture.Retentions.Last().Outcome == "faulted", "Original task exception identity preserved; seen=" + seen + "; same=" + ReferenceEquals(seen, expected) + "; outcome=" + capture.Retentions.Last().Outcome); graph.Error = null;
            var recursiveError = new InvalidOperationException("recursive identity"); graph.Nodes["bad"] = new Node { Error = recursiveError }; seen = null;
            try { Task.Run(() => graph.RetainSeparateAsync(new[] { "bad" }, null, default)).GetAwaiter().GetResult(); } catch (Exception error) { seen = error; }
            SpinWait.SpinUntil(() => capture.Retentions.Last().Outcome != "open", 1000);
            Check(ReferenceEquals(seen, recursiveError) && capture.Retentions.Last().Outcome == "faulted", "Recursive body original exception identity preserved; seen=" + seen);
            Task.Run(() => Parallel.For(0, 4, _ => graph.RetainSeparateAsync(new[] { "a" }, null, default).GetAwaiter().GetResult())).GetAwaiter().GetResult();
            Check(capture.Retentions.Skip(4).All(r => r.Roots == 1 && r.Visits == 3), "Concurrent calls associate by list identity rather than continuation thread");
            for (int i = capture.Retentions.Count; i <= RetentionProbes.Limit; i++) graph.RetainSeparateAsync(Array.Empty<string>(), null, default).GetAwaiter().GetResult();
            Check(capture.Retentions.Count == RetentionProbes.Limit && capture.DroppedRetentions == 1, "Independent retention record cap counts overflow");
            Check(RetentionProbes.Export(capture).Split('\n').Length == RetentionProbes.Limit + 2, "Bounded CSV includes all admitted records");
            LoadingHooks.Recorder = null;
            Task.Run(() => graph.RetainSeparateAsync(new[] { "a" }, null, default)).GetAwaiter().GetResult();
            Check(capture.Retentions.Count == RetentionProbes.Limit && capture.DroppedRetentions == 1, "Outside capture has no records");
        }
        finally { LoadingHooks.Recorder = null; harmony.UnpatchSelf(); }
        var installed = new Harmony("ftls.retention.installed.validation");
        try
        {
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp" && a.GetType("Diz.DependencyManager.DependencyGraph`1") != null);
            var coverage = new List<string>(); RetentionProbes.Install(installed, game.GetType, coverage);
            Check(coverage.Count == 1, "Installed closed IEasyBundle kickoff probes compile on Mono");
        }
        finally { installed.UnpatchSelf(); }
        return checks;
    }
}
