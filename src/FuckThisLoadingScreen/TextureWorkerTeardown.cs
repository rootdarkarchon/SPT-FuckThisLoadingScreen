using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    // Stop the native reader once, before the owning renderer removes trees one at a time.
    // Native Dispose still removes each tree, joins/disposes the old worker, and permits restart.
    internal sealed class TextureWorkerTeardown
    {
        internal static TextureWorkerTeardown Active;
        private readonly Func<bool> _enabled;
        private readonly Action<string> _warning;
        private readonly int _mainThread = Environment.CurrentManagedThreadId;
        private readonly FieldInfo _ownedTrees, _trees, _worker, _source, _token;
        private bool _warned;
        internal readonly MethodInfo Clear;

        private TextureWorkerTeardown(Type renderer, Type tree, Func<bool> enabled, Action<string> warning)
        {
            _enabled = enabled;
            _warning = warning;
            _ownedTrees = RequireField(renderer, "_texturesQuadTrees", false);
            _trees = RequireField(tree, "_trees", true);
            _worker = RequireField(tree, "_workThreads", true);
            _source = RequireField(tree, "_terminateTokenSource", true);
            _token = RequireField(tree, "_terminateToken", true);
            if (!_ownedTrees.FieldType.IsGenericType || _ownedTrees.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>) || _ownedTrees.FieldType.GetGenericArguments()[1] != tree ||
                _trees.FieldType != typeof(HashSet<>).MakeGenericType(tree) || _worker.FieldType != typeof(Thread) ||
                _source.FieldType != typeof(CancellationTokenSource) || _token.FieldType != typeof(CancellationToken))
                throw new MissingFieldException("Texture-worker ownership/cancellation ABI mismatch");
            Clear = AccessTools.DeclaredMethod(renderer, "ClearTree", Type.EmptyTypes);
            if (Clear?.ReturnType != typeof(void) || Clear.IsStatic || Clear.GetMethodBody() == null)
                throw new MissingMethodException("StaticDeferredDecalRenderer.ClearTree ABI");
        }
        private static FieldInfo RequireField(Type type, string name, bool isStatic)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null || field.IsStatic != isStatic)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }
        internal static TextureWorkerTeardown Install(Harmony harmony, Func<string, Type> find, Guid expectedModule, Func<bool> enabled, Action<string> warning)
        {
            Type tree = find("TextureQuadTree"), renderer = find("StaticDeferredDecalRenderer");
            if (tree.Module.ModuleVersionId != expectedModule || renderer.Module.ModuleVersionId != expectedModule)
                throw new NotSupportedException("Texture-worker fix requires the supported EFT module identity");
            var fix = new TextureWorkerTeardown(renderer, tree, enabled, warning);
            // The research prefix opens ClearTree first, so its duration includes quiescence.
            harmony.Patch(fix.Clear, prefix: new HarmonyMethod(typeof(TextureWorkerTeardown), nameof(Prefix)) { priority = Priority.Last },
                transpiler: new HarmonyMethod(typeof(TextureWorkerTeardown), nameof(GuardClearTree)));
            return fix;
        }
        internal static void Prefix(object __instance)
        {
            try
            { Active?.Before(__instance); }
            catch (Exception error) { Active?.Warn("Texture-worker teardown fix declined; native cleanup retained: " + error.GetType().Name + ": " + error.Message); }
        }
        internal void Before(object renderer)
        {
            if (!_enabled() || Environment.CurrentManagedThreadId != _mainThread)
                return;
            var thread = _worker.GetValue(null) as Thread;
            if (thread == null || !thread.IsAlive || ReferenceEquals(thread, Thread.CurrentThread))
                return;
            var owned = _ownedTrees.GetValue(renderer) as IDictionary;
            object registered = _trees.GetValue(null);
            // In this EFT ABI only main-thread construction/disposal changes the set; the
            // worker only enumerates it. Do not take the very lock that is being starved.
            if (owned == null || !OwnsAll(owned.Values, registered as IEnumerable))
            { Note("skipped-shared-or-empty-tree-set"); return; }
            var cancellation = _source.GetValue(null) as CancellationTokenSource;
            if (cancellation == null || !((CancellationToken)_token.GetValue(null)).Equals(cancellation.Token) || !ReferenceEquals(_worker.GetValue(null), thread))
            { Note("skipped-worker-generation-mismatch"); return; }
            var span = BeginTrace();
            string result = "failed";
            try
            {
                // No renderer/tree lock is held. Cancellation prevents the next unfair
                // reacquisition; wait for the existing iteration to release the lock.
                result = StopWorker(thread, cancellation, 2000);
                if (result == "join-timeout")
                    Warn("Texture-worker quiescence exceeded 2s; native cleanup retained.");
            }
            finally { try { span?.Finish(result); } catch { } }
        }
        private static HideoutResearchSpan BeginTrace()
        { try { return HideoutResearch.Active?.BeginDetail("decals.texture-worker.quiesce", "Cancel and join before native texture-tree disposal; wait limit=2000ms"); } catch { return null; } }
        private static void Note(string result)
        { try { HideoutResearch.Active?.NoteDetail("decals.texture-worker.quiesce", result); } catch { } }
        private void Warn(string text) { if (!_warned) { _warned = true; try { _warning(text); } catch { } } }

        internal static string StopWorker(Thread thread, CancellationTokenSource cancellation, int timeoutMs)
        {
            if (thread == null || !thread.IsAlive)
                return "no-live-worker";
            if (ReferenceEquals(thread, Thread.CurrentThread))
                return "same-thread-skipped";
            if (cancellation == null)
                return "no-cancellation-source";
            cancellation.Cancel();
            return thread.Join(timeoutMs) ? "stopped-before-disposal" : "join-timeout";
        }
        internal static IEnumerable<CodeInstruction> GuardClearTree(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            if (code.Count(i => i.operand is MethodInfo m && m.DeclaringType.Name == "TextureQuadTree" && m.Name == "Dispose" && m.GetParameters().Length == 0) != 1 ||
                !code.Any(i => i.operand is FieldInfo f && f.DeclaringType.Name == "StaticDeferredDecalRenderer" && f.Name == "_texturesQuadTrees"))
                throw new MissingMethodException("ClearTree must retain the native texture-tree disposal loop");
            return code; // Validate only; retain native IL and exception paths unchanged.
        }
        internal static bool OwnsAll(IEnumerable owned, IEnumerable registered)
        {
            if (owned == null || registered == null)
                return false;
            var ownership = new HashSet<object>(ReferenceIdentity.Instance);
            foreach (object tree in owned)
            { if (tree == null || !ownership.Add(tree)) return false; }
            if (ownership.Count == 0)
                return false;
            int count = 0;
            foreach (object tree in registered)
            { if (!ownership.Contains(tree)) return false; count++; }
            return count == ownership.Count;
        }
        private sealed class ReferenceIdentity : IEqualityComparer<object>
        {
            internal static readonly ReferenceIdentity Instance = new ReferenceIdentity();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
