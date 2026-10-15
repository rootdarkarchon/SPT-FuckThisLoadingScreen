using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using System.Threading.Tasks;
using FTLS.LevelStageTimer;
using HarmonyLib;
using Mono.Cecil;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }
    private static long tick;
    private static Recorder New(int rows = 20, int samples = 20, int captures = 3) =>
        new Recorder(captures, 10, rows, samples, () => tick, 1000);

    public static async Task Main(string[] args)
    {
        var r = New(); tick = 0;
        Span root = r.Begin("raid", "work", true);
        Span child = r.Begin("async", "work"); tick = 50; child.Finish("completed");
        root.Finish("completed");
        Check(r.TakeReady() == null && r.IsActive, "Root Task success must not prematurely close the network loading capture");
        tick = 100; r.GameStarted();
        Capture c = r.TakeReady();
        Check(c != null && c.Outcome == "game-started" && c.Rows[1].End == 50, "Stage completion and native terminal time");
        Check(r.Milliseconds(c.Terminal - c.Start) == 100, "Wall-clock conversion");
        r = New(); root = r.Begin("raid", "work", true);
        Check(r.Begin("nested-root", "work", true) == null, "Nested live root cannot own capture");
        root.Finish("completed");
        Span replacement = r.Begin("retry", "work", true);
        Check(replacement != null && r.TakeReady().Outcome == "superseded-before-start", "Retry cannot be absorbed by an aborted or completed network root");
        object marker = new object(); r.AttachMarker(marker); r.GameStarted(new object());
        Check(r.IsActive, "Unrelated/stale game-start markers ignored");
        replacement.Finish("completed"); r.GameStarted(marker);
        Check(r.TakeReady().Outcome == "game-started", "Capture owns its native metrics marker");
        r = New(rows: 2, samples: 1, captures: 1); tick = 0;
        root = r.Begin("raid", "work", true); child = r.Begin("slow", "work");
        Check(r.Begin("excess", "work") == null, "Span count bounded");
        r.Sample("one"); r.Sample("two"); tick = 10000;
        c = r.TakeReady();
        Check(c.Outcome == "timeout" && c.DroppedRows == 1 && c.DroppedSamples == 1, "Timeout and dropped counts");
        Check(c.Rows.All(row => row.Outcome == "incomplete-at-export"), "Open spans identified at timeout");
        tick++; child.Finish("completed");
        Check(c.Rows[1].Outcome == "incomplete-at-export", "Late callbacks cannot mutate prior capture");
        Check(r.Begin("excess-capture", "work", true) == null, "Capture count bounded");
        Check(r.Exhausted, "Budget exhaustion can disarm instrumentation");
        r = New(); tick = 0; root = r.Begin("raid", "work", true); root.Finish("canceled");
        Check(r.TakeReady().Outcome == "canceled", "Canceled root closes capture");
        r = New(); root = r.Begin("raid", "work", true); r.GameStarted();
        Check(r.TakeReady() == null, "Wait for outstanding observers after marker"); tick += 1000;
        Check(r.TakeReady().Rows[0].Outcome == "incomplete-at-export", "Terminal drain is bounded");
        r = New(); root = r.Begin("raid", "work", true);
        Check(r.TakeReady(true).Outcome == "plugin-stopped", "Shutdown flush");

        var harmony = new Harmony("ftls.levelstagetimer.tests");
        foreach (string name in new[] { nameof(TaskTarget), nameof(GenericTaskTarget), nameof(ThrowTarget), nameof(CoroutineTarget), nameof(FailedCoroutineTarget) })
        {
            MethodInfo method = typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            LoadingHooks.Hooks.Add(method, new Hook { Stage = name, Kind = "work", Root = false });
            string postfix = typeof(Task).IsAssignableFrom(method.ReturnType) ? nameof(LoadingHooks.TaskPostfix) : nameof(LoadingHooks.EnumeratorPostfix);
            harmony.Patch(method, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Prefix)),
                new HarmonyMethod(typeof(LoadingHooks), postfix), finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Finalizer)));
        }
        try
        {
            r = New(); tick = 0; root = r.Begin("raid", "work", true); LoadingHooks.Recorder = r;
            var source = new TaskCompletionSource<int>();
            Check(ReferenceEquals(TaskTarget(source.Task), source.Task), "Harmony hook preserves exact Task identity");
            Check(ReferenceEquals(GenericTaskTarget(source.Task), source.Task), "Task<T> accepted without replacement");
            tick = 40; source.SetResult(17); await source.Task;
            var fault = Task.FromException(new InvalidOperationException("expected"));
            Check(ReferenceEquals(TaskTarget(fault), fault), "Faulted task identity preserved");
            try { await fault; } catch (InvalidOperationException) { checks++; }
            _ = TaskTarget(Task.FromCanceled(new System.Threading.CancellationToken(true)));
            try { _ = ThrowTarget(); throw new Exception("Exception suppressed"); } catch (InvalidOperationException) { checks++; }
            IEnumerator iterator = CoroutineTarget();
            Check(iterator.MoveNext() && ReferenceEquals(iterator.Current, Sentinel), "Coroutine forwards yielded object exactly");
            Check(!iterator.MoveNext(), "Coroutine completes");
            ((IDisposable)iterator).Dispose();
            iterator = CoroutineTarget(); iterator.MoveNext(); ((IDisposable)iterator).Dispose();
            Check(disposed == 2, "Wrapper forwards original disposal/finally");
            iterator = FailedCoroutineTarget(); iterator.MoveNext();
            try { iterator.MoveNext(); throw new Exception("Coroutine exception suppressed"); } catch (InvalidOperationException) { checks++; }
            root.Finish("completed"); r.GameStarted();
            c = r.TakeReady();
            Check(c != null, "All fixture spans drain");
            Check(c.Rows.Count(row => row.Stage == nameof(TaskTarget) && row.Outcome == "completed") == 1, "Async Task completion, not method return");
            Check(c.Rows.Any(row => row.Outcome == "faulted") && c.Rows.Any(row => row.Outcome == "canceled") && c.Rows.Any(row => row.Outcome == "threw"), "Failure statuses preserved");
            Check(c.Rows.Any(row => row.Stage == nameof(CoroutineTarget) && row.Outcome == "disposed"), "Abandoned coroutine status");
        }
        finally { harmony.UnpatchSelf(); LoadingHooks.Hooks.Clear(); LoadingHooks.Recorder = null; }

        // Detail budgets are per stage and per capture; a hot loop cannot consume cleanup's rows.
        r = New(rows: 20); tick = 0; root = r.Begin("raid", "work", true);
        for (int i = 0; i < 10; i++) r.Begin("zone", "work", limit: 2)?.Finish("completed");
        r.Begin("cleanup", "work", limit: 2).Finish("completed"); root.Finish("completed"); r.GameStarted(); c = r.TakeReady();
        Check(c.Rows.Count == 4 && c.Suppressed["zone"] == 8 && c.DroppedRows == 0, "Per-stage suppression protects other detail stages");
        root = r.Begin("raid", "work", true);
        r.Begin("zone", "work", limit: 2, detail: new string('x', 400)).Finish("completed");
        root.Finish("completed"); r.GameStarted(); c = r.TakeReady();
        Check(c.Suppressed.Count == 0 && c.Rows[1].Detail.Length == 320, "Detail budgets reset and method labels are bounded");

        harmony = new Harmony("ftls.levelstagetimer.detail.tests");
        try
        {
            MethodInfo callbackMethod = AccessTools.Method(typeof(Program), nameof(CallbackTarget));
            harmony.Patch(callbackMethod, transpiler: new HarmonyMethod(typeof(Program), nameof(FixtureCallbackTranspiler)));
            MethodInfo step = DetailProbes.StateMachineStep(AccessTools.Method(typeof(Program), nameof(StepTarget)));
            Check(step != null, "Resolve compiler-described state machine rather than a generated name");
            LoadingHooks.Hooks.Add(step, new Hook { Stage = "fixture.step", Kind = "work", MaxCalls = 64 });
            harmony.Patch(step, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.StepPrefix)),
                new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.SyncPostfix)),
                finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Finalizer)));
            ConstructorInfo ctor = HookCatalog.Resolve(new HookSpec(typeof(ConstructorTarget).FullName, ".ctor", 1, "ctor"),
                name => typeof(Program).Assembly.GetType(name)).Single() as ConstructorInfo;
            Check(ctor != null && HookCatalog.ReturnType(ctor) == typeof(void), "Constructor resolver and sync return classification");
            LoadingHooks.Hooks.Add(ctor, new Hook { Stage = "fixture.constructor", Kind = "work", MaxCalls = 8 });
            harmony.Patch(ctor, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Prefix)),
                new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.SyncPostfix)),
                finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Finalizer)));

            r = New(rows: 2048); tick = 0; root = r.Begin("raid", "work", true); LoadingHooks.Recorder = r;
            Check(new ConstructorTarget(19).Value == 19, "Constructor behavior preserved");
            var gate = new TaskCompletionSource<bool>();
            Task<int> work = StepTarget(gate.Task); tick += 100; gate.SetResult(true);
            Check(await work == 23, "State-machine instrumentation preserves async result");
            object argument = new object(); var delivered = new List<int>();
            Action<object> chain = null;
            Action<object> second = value => { Check(ReferenceEquals(value, argument), "Same callback argument"); delivered.Add(2); };
            chain = value => { delivered.Add(1); chain -= second; tick += 5; };
            chain += second; chain += second;
            CallbackTarget(chain, argument);
            Check(delivered.SequenceEqual(new[] { 1, 2, 2 }), "Multicast order, duplicates and mutation snapshot preserved");
            var expected = new InvalidOperationException("callback failure");
            bool late = false;
            Action<object> failing = value => throw expected;
            failing += value => late = true;
            try { CallbackTarget(failing, argument); throw new Exception("Callback fault swallowed"); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, expected) && !late, "Original callback exception stops delivery unchanged"); }
            try { CallbackTarget(null, argument); throw new Exception("Null callback fault swallowed"); }
            catch (NullReferenceException) { checks++; }
            int actual = 0; Action<object> crowded = null;
            for (int i = 0; i < 100; i++) crowded += value => actual++;
            CallbackTarget(crowded, argument);
            for (int i = 0; i < 70; i++) CallbackTarget(value => actual++, argument);
            Check(actual == 170, "All callbacks still execute after subscriber and aggregate timer budgets expire");
            try { DetailProbes.CollectAggressive(-1, GCCollectionMode.Default, true, false); throw new Exception("GC argument error swallowed"); }
            catch (ArgumentOutOfRangeException) { checks++; }
            root.Finish("completed"); r.GameStarted(); c = r.TakeReady();
            Check(c.Rows.Where(row => row.Stage == "fixture.step").Sum(row => row.End - row.Start) == 18,
                "Synchronous async steps exclude the 100ms await gap");
            Check(c.Rows.Any(row => row.Stage == "fixture.constructor" && row.Outcome == "completed"), "Constructor timing recorded");
            Check(c.Rows.Any(row => row.Stage == "callback.player-spawned.handler" && row.Detail.Contains("Program")), "Callback assembly/method attribution recorded");
            Check(c.Rows.Any(row => row.Stage == "callback.player-spawned.handler" && row.Outcome == "threw"), "Throwing subscriber labeled");
            Check(c.Suppressed.ContainsKey("callback.player-spawned.handler") && c.Suppressed.ContainsKey("callback.player-spawned"), "Both callback bounds reported");
            Check(c.Rows.Any(row => row.Stage == "cleanup.gc-aggressive" && row.Outcome == "threw"), "Cleanup argument failure labeled");
            LoadingHooks.Recorder = null; CallbackTarget(value => actual++, argument);
            Check(actual == 171, "Inactive diagnostic callback call-through");
        }
        finally { harmony.UnpatchSelf(); LoadingHooks.Hooks.Clear(); LoadingHooks.Recorder = null; }

        TestAggregatesAndModPostfixes();

        var gcCalls = new[] {
            new CodeInstruction(OpCodes.Call, typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes)),
            new CodeInstruction(OpCodes.Call, typeof(GC).GetMethod(nameof(GC.WaitForPendingFinalizers), Type.EmptyTypes)),
            new CodeInstruction(OpCodes.Call, typeof(GC).GetMethod(nameof(GC.Collect), new[] { typeof(int), typeof(GCCollectionMode), typeof(bool), typeof(bool) })) };
        gcCalls[0].labels.Add(default(Label));
        gcCalls[0].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
        CodeInstruction[] replaced = DetailProbes.CollectTranspiler(gcCalls).ToArray();
        Check(ReferenceEquals(replaced[0], gcCalls[0]) && replaced[0].labels.Count == 1 && replaced[0].blocks.Count == 1,
            "Cleanup call-site replacement preserves branch labels and exception regions");
        Check(replaced.All(i => ((MethodInfo)i.operand).DeclaringType == typeof(DetailProbes)), "Exactly the three explicit cleanup call sites replaced");
        try { DetailProbes.CollectTranspiler(Array.Empty<CodeInstruction>()).ToArray(); throw new Exception("Missing ABI accepted"); }
        catch (InvalidOperationException) { checks++; }

        string install = args[0];
        checks += ActivationValidation.Run();
        checks += SceneValidation.Run(install);
        checks += RetentionValidation.Run(install);
        using var game = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll"));
        using var fika = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "BepInEx/plugins/Fika/Fika.Core.dll"));
        using var orbit = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "BepInEx/plugins/ORBIT/ORBIT.dll"));
        using var custom = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "BepInEx/plugins/spt/spt-custom.dll"));
        using var sain = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "BepInEx/plugins/SAIN/SAIN.dll"));
        using var abps = ModuleDefinition.ReadModule(System.IO.Path.Combine(install, "BepInEx/plugins/acidphantasm-botplacementsystem/acidphantasm-botplacementsystem.dll"));
        var modules = new[] { game, fika, orbit, custom, sain, abps };
        foreach (HookSpec spec in HookCatalog.Specs.Concat(HookCatalog.OptionalSpecs))
        {
            TypeDefinition type = modules.Select(m => m.GetType(spec.Type.Replace('+', '/'))).FirstOrDefault(t => t != null);
            Check(type != null, "Installed type missing: " + spec.Type);
            if (spec.OptionalGroupType != null)
                Check(modules.Any(m => m.GetType(spec.OptionalGroupType) != null), "Installed optional group sentinel missing: " + spec.OptionalGroupType);
            var methods = type.Methods.Where(m => m.Name == spec.Method && m.Parameters.Count == spec.Parameters);
            if (spec.Stage == "resources.bundles-and-pools") methods = methods.Where(m => m.Parameters[0].Name == "pools");
            if (spec.Parameter1Type != null) methods = methods.Where(m => m.Parameters[1].ParameterType.FullName == spec.Parameter1Type);
            MethodDefinition[] matches = methods.ToArray();
            Check(matches.Length == 1 && matches[0].HasBody && !matches[0].HasGenericParameters,
                "Installed signature mismatch: " + spec.Type + "." + spec.Method);
            string returns = matches[0].ReturnType.FullName;
            Check(!matches[0].ReturnType.IsByReference && !matches[0].ReturnType.IsPointer, "Unsupported result: " + returns);
            if (spec.Aggregate) Check(!returns.StartsWith("System.Threading.Tasks.Task") && returns != "System.Collections.IEnumerator" && spec.MaxCalls > 0,
                "Aggregate hooks remain synchronous and detail-bounded");
        }
        MethodDefinition cleanup = game.GetType("InGameMemoryManagement").Methods.Single(m => m.Name == "Collect" && m.Parameters.Count == 5);
        MethodReference[] calls = cleanup.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Where(m => m.DeclaringType.FullName == "System.GC").ToArray();
        Check(calls.Count(m => m.Name == "Collect" && m.Parameters.Count == 0) == 1 &&
              calls.Count(m => m.Name == "WaitForPendingFinalizers" && m.Parameters.Count == 0) == 1 &&
              calls.Count(m => m.Name == "Collect" && m.Parameters.Count == 4) == 1, "Installed explicit cleanup call-site ABI");
        MethodDefinition create = fika.GetType("Fika.Core.Main.Players.FikaPlayer").Methods.Single(m => m.Name == "Create" && m.Parameters.Count == 20);
        var stateType = (TypeReference)create.CustomAttributes.Single(a => a.AttributeType.FullName == typeof(AsyncStateMachineAttribute).FullName).ConstructorArguments[0].Value;
        MethodDefinition createStep = fika.GetTypes().Single(t => t.FullName == stateType.FullName).Methods.Single(m => m.Name == "MoveNext");
        CheckCallbackSite(createStep, "Fika.Core.Main.Players.FikaPlayer");
        CheckCallbackSite(game.GetType("EFT.GameWorld").Methods.Single(m => m.Name == "RegisterPlayer"), "EFT.IPlayer");
        Check(game.GetType("EFT.Utilities.ClientMetricsEvents").Methods.Any(m => m.Name == "SetGameStarted" && m.Parameters.Count == 0), "Terminal marker ABI");
        Console.WriteLine($"PASS: {checks} lifecycle, Harmony and installed ABI checks; no live raid acceptance claimed.");
    }

    private static void TestAggregatesAndModPostfixes()
    {
        var r = New(rows: 80); tick = 0;
        Span root = r.Begin("raid", "work", true);
        for (int i = 0; i < 97; i++)
        {
            AggregateToken token = r.BeginAggregate("cluster", "work");
            tick += i < 64 ? 1 : 10;
            r.FinishAggregate(ref token, false);
            r.FinishAggregate(ref token, true); // Harmony finalization must not double count a finished call.
        }
        root.Finish("completed"); r.GameStarted(); Capture c = r.TakeReady();
        AggregateRow aggregate = c.Aggregates["cluster"];
        Check(aggregate.Calls == 97 && aggregate.Completed == 97 && aggregate.Threw == 0 &&
            aggregate.TotalTicks == 394 && aggregate.MaxTicks == 10, "Every capped cluster call contributes to total/max/count exactly once");
        Check(c.Rows.Count == 65 && c.Suppressed["cluster"] == 33 && c.Aggregates.Count == 1,
            "All-call aggregation uses one fixed record while individual rows remain capped");
        AggregateToken inactive = r.BeginAggregate("cluster", "work");
        Check(inactive.Owner == null, "Inactive aggregation is a no-op");

        r = New(rows: 2); root = r.Begin("raid", "work", true);
        for (int i = 0; i < 5; i++) { var token = r.BeginAggregate("cluster", "work"); tick++; r.FinishAggregate(ref token, i == 4); }
        root.Finish("completed"); r.GameStarted(); c = r.TakeReady(); aggregate = c.Aggregates["cluster"];
        Check(c.Rows.Count == 2 && c.DroppedRows == 4 && aggregate.TotalTicks == 5 && aggregate.Completed == 4 && aggregate.Threw == 1,
            "Global detail-row exhaustion does not stop aggregate timings or failure counts");

        r = New(rows: 40); root = r.Begin("raid", "work", true);
        var unfinished = r.BeginAggregate("cluster", "work");
        c = r.TakeReady(shutdown: true); tick += 20; r.FinishAggregate(ref unfinished, false);
        Check(c.Aggregates["cluster"].Calls == 1 && c.Aggregates["cluster"].Completed == 0 && c.Aggregates["cluster"].TotalTicks == 0,
            "Sealed captures preserve incomplete aggregates and ignore late completions");
        root = r.Begin("raid", "work", true);
        var next = r.BeginAggregate("cluster", "work"); tick += 3; r.FinishAggregate(ref next, false);
        root.Finish("completed"); r.GameStarted(); Capture replacement = r.TakeReady();
        Check(replacement.Aggregates["cluster"].Calls == 1 && replacement.Aggregates["cluster"].TotalTicks == 3 &&
            !ReferenceEquals(c.Aggregates["cluster"], replacement.Aggregates["cluster"]), "Aggregate counters reset per capture");

        r = New(rows: 40); root = r.Begin("raid", "work", true);
        for (int i = 0; i < 20; i++) { var token = r.BeginAggregate("stage" + i, "work"); r.FinishAggregate(ref token, false); }
        root.Finish("completed"); r.GameStarted(); c = r.TakeReady();
        Check(c.Aggregates.Count == 16 && c.DroppedAggregateStages == 4, "Independent hard bound on aggregate stage storage");

        var optional = new HookSpec("missing", "Missing", 0, "optional", optionalGroupType: "sentinel");
        Check(!HookCatalog.Available(optional, name => null) && HookCatalog.Available(optional, name => name == "sentinel" ? typeof(Program) : null),
            "Optional groups skip only when their sentinel type is absent");
        try { HookCatalog.Resolve(optional, name => name == "sentinel" ? typeof(Program) : null); throw new Exception("Broken present group accepted"); }
        catch (TypeLoadException) { checks++; }

        var timer = new Harmony("ftls.levelstagetimer.aggregate.tests");
        var foreign = new Harmony("ftls.levelstagetimer.foreign.tests");
        MethodInfo target = AccessTools.Method(typeof(Program), nameof(AggregateTarget));
        MethodInfo returnTarget = AccessTools.Method(typeof(Program), nameof(AggregateReturnTarget));
        MethodInfo controller = AccessTools.Method(typeof(Program), nameof(ControllerTarget));
        MethodInfo mod = AccessTools.Method(typeof(Program), nameof(ModPostfix));
        try
        {
            LoadingHooks.Hooks.Add(target, new Hook { Stage = "fixture.cluster", Kind = "work", Aggregate = true, MaxCalls = 2 });
            timer.Patch(target, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregatePrefix)),
                new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregatePostfix)),
                finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregateFinalizer)));
            LoadingHooks.Hooks.Add(returnTarget, new Hook { Stage = "fixture.return", Kind = "work", Aggregate = true, MaxCalls = 2 });
            timer.Patch(returnTarget, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregatePrefix)),
                new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregatePostfix)),
                finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.AggregateFinalizer)));
            r = New(rows: 40); tick = 0; root = r.Begin("raid", "work", true); LoadingHooks.Recorder = r; aggregateCalls = 0;
            for (int i = 0; i < 80; i++) AggregateTarget(false);
            object identity = new object();
            Check(ReferenceEquals(AggregateReturnTarget(identity), identity), "Aggregating a synchronous factory preserves exact object identity");
            try { AggregateTarget(true); throw new Exception("Aggregate target exception swallowed"); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, AggregateFailure), "Aggregated target exception identity preserved"); }
            root.Finish("completed"); r.GameStarted(); c = r.TakeReady(); aggregate = c.Aggregates["fixture.cluster"];
            Check(aggregateCalls == 81 && aggregate.Calls == 81 && aggregate.Completed == 80 && aggregate.Threw == 1 && aggregate.TotalTicks == 162,
                "Real Harmony struct state records success/throws and preserves all calls beyond cap");

            // Mirror ORBIT: its foreign postfix may register before OR after the timer hooks its method.
            foreign.Patch(controller, postfix: new HarmonyMethod(mod));
            ControllerTarget(); // Exercise the already-compiled foreign wrapper before instrumenting its patch method.
            LoadingHooks.Hooks.Add(mod, new Hook { Stage = "fixture.mod-postfix", Kind = "work", MaxCalls = 8 });
            timer.Patch(mod, new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Prefix)),
                new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.SyncPostfix)),
                finalizer: new HarmonyMethod(typeof(LoadingHooks), nameof(LoadingHooks.Finalizer)));
            r = New(rows: 40); tick = 0; root = r.Begin("raid", "work", true); LoadingHooks.Recorder = r;
            controllerOrder.Clear(); ControllerTarget();
            foreign.UnpatchSelf(); foreign.Patch(controller, postfix: new HarmonyMethod(mod)); ControllerTarget();
            root.Finish("completed"); r.GameStarted(); c = r.TakeReady();
            Check(controllerOrder.SequenceEqual(new[] { "body", "postfix", "body", "postfix" }), "Foreign patch call order/count preserved across registration timing");
            Check(c.Rows.Count(row => row.Stage == "fixture.mod-postfix" && row.End - row.Start == 5) == 2,
                "Direct postfix-method probes work with early and late foreign patch registration");
            timer.UnpatchSelf(); LoadingHooks.Recorder = null; ControllerTarget();
            Check(controllerOrder.Count == 6 && controllerOrder[5] == "postfix" && Harmony.GetPatchInfo(controller).Postfixes.Any(p => p.owner == foreign.Id),
                "Removing timer patches retains foreign bot-controller behavior");
        }
        finally { LoadingHooks.Recorder = null; foreign.UnpatchSelf(); timer.UnpatchSelf(); LoadingHooks.Hooks.Clear(); }
    }

    private static int aggregateCalls;
    private static readonly Exception AggregateFailure = new InvalidOperationException("aggregate fault");
    private static readonly List<string> controllerOrder = new List<string>();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void AggregateTarget(bool fail)
    { aggregateCalls++; tick += 2; if (fail) throw AggregateFailure; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static object AggregateReturnTarget(object value) => value;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void ControllerTarget() { controllerOrder.Add("body"); tick += 3; }
    private static void ModPostfix() { controllerOrder.Add("postfix"); tick += 5; }

    private static void CheckCallbackSite(MethodDefinition method, string argument)
    {
        Check(method.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Count(m => m.Name == "Invoke" &&
            m.DeclaringType is GenericInstanceType g && g.ElementType.FullName == "System.Action`1" &&
            g.GenericArguments[0].FullName == argument) == 1, "Installed callback invoke ABI: " + method.FullName);
    }

    private static IEnumerable<CodeInstruction> FixtureCallbackTranspiler(IEnumerable<CodeInstruction> instructions) =>
        DetailProbes.CallbackTranspiler(instructions, typeof(object).FullName, nameof(DetailProbes.PlayerSpawned));
    [MethodImpl(MethodImplOptions.NoInlining)] private static void CallbackTarget(Action<object> callback, object value) => callback(value);
    [MethodImpl(MethodImplOptions.NoInlining)] private static async Task<int> StepTarget(Task gate)
    { tick += 7; await gate; tick += 11; return 23; }
    private sealed class ConstructorTarget
    {
        public readonly int Value;
        [MethodImpl(MethodImplOptions.NoInlining)] public ConstructorTarget(int value) { Value = value; }
    }

    private static readonly object Sentinel = new object();
    private static int disposed;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task TaskTarget(Task task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task<int> GenericTaskTarget(Task<int> task) => task;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task ThrowTarget() => throw new InvalidOperationException("expected");
    [MethodImpl(MethodImplOptions.NoInlining)] private static IEnumerator CoroutineTarget()
    { try { yield return Sentinel; } finally { disposed++; } }
    [MethodImpl(MethodImplOptions.NoInlining)] private static IEnumerator FailedCoroutineTarget()
    { yield return Sentinel; throw new InvalidOperationException("expected"); }
}
