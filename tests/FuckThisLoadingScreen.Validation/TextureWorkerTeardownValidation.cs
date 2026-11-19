using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using FuckThisLoadingScreen;
using HarmonyLib;

internal static class TextureWorkerTeardownValidation
{
    internal static void Run(Action<bool, string> check)
    {
        object a = new object(), b = new object();
        check(TextureWorkerTeardown.OwnsAll(new[] { a, b }, new[] { b, a }), "Ownership is an exact unordered reference set");
        check(!TextureWorkerTeardown.OwnsAll(new[] { a }, new[] { a, b }), "Partial renderer ownership leaves the shared worker alone");
        check(!TextureWorkerTeardown.OwnsAll(new object[0], new object[0]), "Empty tree ownership does not cancel a worker");
        check(!TextureWorkerTeardown.OwnsAll(new[] { a, a }, new[] { a }), "Duplicate ownership cannot masquerade as complete coverage");
        check(!TextureWorkerTeardown.OwnsAll(new object[] { null }, new[] { a }), "Invalid owner data declines quiescence");
        check(TextureWorkerTeardown.StopWorker(null, null, 1) == "no-live-worker", "No worker leaves native cleanup in place");
        using (var self = new CancellationTokenSource())
            check(TextureWorkerTeardown.StopWorker(Thread.CurrentThread, self, 1) == "same-thread-skipped" && !self.IsCancellationRequested, "Never cancel or join the calling thread");

        using (var release = new ManualResetEventSlim())
        using (var started = new ManualResetEventSlim())
        using (var cancellation = new CancellationTokenSource())
        {
            var stubborn = new Thread(() => { started.Set(); release.Wait(5000); }) { IsBackground = true };
            stubborn.Start(); check(started.Wait(5000), "Timeout fixture worker started");
            try
            {
                check(TextureWorkerTeardown.StopWorker(stubborn, null, 10) == "no-cancellation-source" && stubborn.IsAlive, "Missing cancellation state does not terminate a worker");
                check(TextureWorkerTeardown.StopWorker(stubborn, cancellation, 10) == "join-timeout" && cancellation.IsCancellationRequested && stubborn.IsAlive,
                    "Timeout retains the live worker for native cleanup instead of discarding its handle");
            }
            finally { release.Set(); stubborn.Join(5000); }
        }

        var harmony = new Harmony("ftls.textureworker.fixture");
        bool master = true, option = true;
        var warnings = new List<string>();
        Func<string, Type> find = name => name == "TextureQuadTree" ? typeof(TextureQuadTree) : typeof(StaticDeferredDecalRenderer);
        Guid module = typeof(TextureQuadTree).Module.ModuleVersionId;
        try
        {
            var fix = TextureWorkerTeardown.Install(harmony, find, module, () => master && option, warnings.Add);
            TextureWorkerTeardown.Active = fix;
            for (int generation = 0; generation < 3; generation++)
            {
                var owner = CreateOwner(31);
                StartWorker();
                var originalThread = TextureQuadTree._workThreads;
                var originalSource = TextureQuadTree._terminateTokenSource;
                fix.Before(owner);
                check(!originalThread.IsAlive && originalSource.IsCancellationRequested, "Current worker is canceled and joined before native disposal, generation " + generation);
                check(ReferenceEquals(TextureQuadTree._workThreads, originalThread) && ReferenceEquals(TextureQuadTree._terminateTokenSource, originalSource) &&
                    TextureQuadTree._trees.Count == 31 && owner._texturesQuadTrees.Count == 31, "Fix leaves worker handles and all trees for native cleanup");
                owner.ClearTree();
                check(TextureQuadTree._trees.Count == 0 && TextureQuadTree._workThreads == null && TextureQuadTree._terminateTokenSource == null && owner.NativeDisposed == 31,
                    "Native cleanup removes every tree and owns worker/token disposal");
            }
            var restarted = CreateOwner(3); StartWorker();
            option = false; fix.Before(restarted);
            check(!TextureQuadTree._terminateTokenSource.IsCancellationRequested && TextureQuadTree._workThreads.IsAlive, "Independent comparison toggle preserves the running native worker");
            option = true; master = false; fix.Before(restarted);
            check(!TextureQuadTree._terminateTokenSource.IsCancellationRequested, "Master switch disables texture-worker quiescence");
            master = true;
            var partial = new StaticDeferredDecalRenderer(); partial._texturesQuadTrees.Add(new object(), restarted._texturesQuadTrees.Values.First());
            fix.Before(partial);
            check(!TextureQuadTree._terminateTokenSource.IsCancellationRequested && TextureQuadTree._trees.Count == 3, "Another renderer's registered trees prevent early cancellation");
            var otherThread = new Thread(() => fix.Before(restarted)) { IsBackground = true };
            otherThread.Start(); check(otherThread.Join(5000) && !TextureQuadTree._terminateTokenSource.IsCancellationRequested, "Off-main-thread cleanup declines the fix");
            using (var wrongSource = new CancellationTokenSource())
            {
                CancellationToken original = TextureQuadTree._terminateToken;
                TextureQuadTree._terminateToken = wrongSource.Token; fix.Before(restarted);
                check(!TextureQuadTree._terminateTokenSource.IsCancellationRequested && !wrongSource.IsCancellationRequested, "Cancellation generation mismatch leaves both sources untouched");
                TextureQuadTree._terminateToken = original;
            }
            restarted.ClearTree();
            check(restarted.NativeDisposed == 3 && TextureQuadTree._workThreads == null, "Actual Harmony prefix quiesces before the original disposal loop executes");
            check(warnings.Count == 0, "Normal guarded lifecycle emits no fallback warnings");
            try { TextureWorkerTeardown.Install(harmony, find, Guid.Empty, () => true, warnings.Add); check(false, "Unsupported module should fail"); }
            catch (NotSupportedException) { check(true, "Unsupported EFT identity refuses the fix"); }
            try { TextureWorkerTeardown.GuardClearTree(new[] { new CodeInstruction(OpCodes.Ret) }).ToList(); check(false, "Invalid ClearTree should fail"); }
            catch (MissingMethodException) { check(true, "Changed native disposal shape refuses the fix"); }
        }
        finally
        {
            TextureWorkerTeardown.Active = null; harmony.UnpatchSelf();
            TextureQuadTree._terminateTokenSource?.Cancel(); TextureQuadTree._workThreads?.Join(5000);
            TextureQuadTree._terminateTokenSource?.Dispose(); TextureQuadTree._terminateTokenSource = null; TextureQuadTree._workThreads = null; TextureQuadTree._trees.Clear();
        }
    }

    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        var harmony = new Harmony("ftls.textureworker.installed");
        try
        {
            var fix = TextureWorkerTeardown.Install(harmony, find, find("TextureQuadTree").Module.ModuleVersionId, () => true, _ => { });
            check(Harmony.GetPatchInfo(fix.Clear).Owners.Contains(harmony.Id), "Teardown fix binds the installed ClearTree method and passes native IL guard");
            var probes = HideoutResearch.Resolve(find);
            HideoutResearch.Install(harmony, probes);
            var prefixes = Harmony.GetPatchInfo(fix.Clear).Prefixes;
            check(prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(TextureWorkerTeardown) && p.priority == Priority.Last) &&
                prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(HideoutResearch) && p.priority == Priority.First),
                "Research ClearTree interval opens before the fix's quiescence wait");
            check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(find("TextureQuadTree"), "Dispose")).Transpilers.Count > 0,
                "Fix composes with exact disposal lock/join observers");
        }
        finally { TextureWorkerTeardown.Active = null; harmony.UnpatchSelf(); }
    }

    private static StaticDeferredDecalRenderer CreateOwner(int count)
    {
        TextureQuadTree._terminateTokenSource = new CancellationTokenSource(); TextureQuadTree._terminateToken = TextureQuadTree._terminateTokenSource.Token;
        var owner = new StaticDeferredDecalRenderer();
        for (int i = 0; i < count; i++)
        {
            var tree = new TextureQuadTree { Owner = owner };
            TextureQuadTree._trees.Add(tree); owner._texturesQuadTrees.Add(new object(), tree);
        }
        return owner;
    }
    private static void StartWorker()
    {
        var entered = new ManualResetEventSlim();
        TextureQuadTree._workThreads = new Thread(() =>
        {
            bool first = true;
            try
            {
                while (!TextureQuadTree._terminateToken.IsCancellationRequested)
                {
                    lock (TextureQuadTree._treesLock)
                    {
                        if (first) { entered.Set(); first = false; }
                        Thread.Sleep(60);
                    }
                }
            }
            finally { entered.Dispose(); }
        }) { IsBackground = true };
        TextureQuadTree._workThreads.Start();
        if (!entered.Wait(5000)) throw new TimeoutException("Texture worker fixture did not enter its lock");
    }
    private sealed class StaticDeferredDecalRenderer
    {
        public readonly Dictionary<object, TextureQuadTree> _texturesQuadTrees = new Dictionary<object, TextureQuadTree>();
        internal int NativeDisposed;
        [MethodImpl(MethodImplOptions.NoInlining)] public void ClearTree()
        {
            foreach (var tree in _texturesQuadTrees.Values) tree.Dispose();
            _texturesQuadTrees.Clear();
        }
    }
    private sealed class TextureQuadTree
    {
        public static readonly HashSet<TextureQuadTree> _trees = new HashSet<TextureQuadTree>();
        public static Thread _workThreads;
        public static CancellationTokenSource _terminateTokenSource;
        public static CancellationToken _terminateToken;
        public static readonly object _treesLock = new object();
        internal StaticDeferredDecalRenderer Owner;
        public void Dispose()
        {
            lock (_treesLock) _trees.Remove(this);
            Owner.NativeDisposed++;
            if (_trees.Count == 0)
            {
                _terminateTokenSource.Cancel(); _workThreads.Join(); _workThreads = null;
                _terminateTokenSource.Dispose(); _terminateTokenSource = null;
            }
        }
    }
}
