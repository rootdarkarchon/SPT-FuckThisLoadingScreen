using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;

internal static class BotProfilePreloadDelayValidation
{
    private static LoadingPolicy Make(Func<Options> options) =>
        new LoadingPolicy(options, () => 0, _ => { }, _ => 0, (_, __) => { });

    internal static void Run(Action<bool, string> check)
    {
        LoadingPolicy previous = PolicyHooks.Policy;
        var harmony = new Harmony("ftls.botpreload.fixture");
        try
        {
            foreach (bool master in new[] { false, true })
            foreach (bool enabled in new[] { false, true })
            {
                bool next = enabled;
                var policy = Make(() => new Options { Enabled = master, ReduceBotProfilePreloadDelay = next });
                PolicyHooks.Policy = policy;
                check(BotProfilePreloadDelay.EffectiveDelay(500) == 500, "Bot delay stays native outside startup");
                LoadingRun run = policy.Begin();
                next = !enabled;
                int expected = master && enabled ? 1 : 500;
                check(BotProfilePreloadDelay.EffectiveDelay(500) == expected, "Master and frozen per-attempt bot delay toggle");
                check(BotProfilePreloadDelay.EffectiveDelay(17) == 17, "Unrelated delay argument is unchanged");
                check(Task.Run(() => BotProfilePreloadDelay.EffectiveDelay(500)).GetAwaiter().GetResult() == 500,
                    "Unrelated worker thread cannot shorten bot delay");
                check(run.BotPreloadDelaysReduced == (expected == 1 ? 1 : 0) &&
                    run.BotPreloadDelayNominalSavedMs == (expected == 1 ? 499 : 0), "Nominal delay counters count only reduced waits");
                policy.RootFinished(run, true);
                check(BotProfilePreloadDelay.EffectiveDelay(500) == 500, "Failed/finished attempt restores native bot delay");
                policy.Stop();
            }
            MethodInfo delay = typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(int) });
            var native = new[] { new CodeInstruction(OpCodes.Ldc_I4, 500), new CodeInstruction(OpCodes.Call, delay), new CodeInstruction(OpCodes.Ret) };
            var changed = BotProfilePreloadDelay.Transpiler(native).ToArray();
            check(changed.Length == 4 && changed[1].Calls(AccessTools.Method(typeof(BotProfilePreloadDelay), nameof(BotProfilePreloadDelay.EffectiveDelay))) &&
                changed[2].Calls(delay), "Only the native delay argument gains an adapter");
            check(native.Length == 3 && native[1].Calls(delay), "Transpiler does not mutate input instructions");
            foreach (CodeInstruction[] invalid in new[] {
                new[] { new CodeInstruction(OpCodes.Ldc_I4, 250), new CodeInstruction(OpCodes.Call, delay) },
                native.Concat(native).ToArray(),
                new[] { new CodeInstruction(OpCodes.Ret) }
            })
            {
                bool rejected = false;
                try { BotProfilePreloadDelay.Transpiler(invalid).ToArray(); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected, "Changed or ambiguous native delay shape is rejected");
            }
            MethodInfo method = typeof(BotProfilePreloadDelayValidation).GetMethod(nameof(Sequence), BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo step = method.GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(BotProfilePreloadDelay), nameof(BotProfilePreloadDelay.Transpiler)));
            var active = Make(() => new Options { Enabled = true, ReduceBotProfilePreloadDelay = true });
            PolicyHooks.Policy = active;
            LoadingRun owner = active.Begin();
            var calls = new List<int>();
            Sequence(calls, null).GetAwaiter().GetResult();
            check(calls.SequenceEqual(new[] { 1, 2 }) && owner.BotPreloadDelaysReduced == 1,
                "Patched async body keeps sequential work and the native await");
            var failure = new InvalidOperationException("bot fixture exception");
            bool sameFailure = false;
            try { Sequence(calls, failure).GetAwaiter().GetResult(); }
            catch (Exception error) { sameFailure = ReferenceEquals(error, failure); }
            check(sameFailure, "Async fault identity survives the delay-only transform");
            active.Stop();
        }
        finally { harmony.UnpatchSelf(); PolicyHooks.Policy = previous; }
    }

    private static async Task Sequence(List<int> calls, Exception failure)
    {
        await Task.Delay(500).ConfigureAwait(false);
        calls.Add(1);
        if (failure != null) throw failure;
        await Task.CompletedTask.ConfigureAwait(false);
        calls.Add(2);
    }

    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        LoadingPolicy previous = PolicyHooks.Policy;
        var harmony = new Harmony("ftls.botpreload.installed");
        SynchronizationContext context = SynchronizationContext.Current;
        try
        {
            MethodInfo step = BotProfilePreloadDelay.Install(harmony, find);
            check(step.DeclaringType.DeclaringType == find("BotProfileClient"), "Exact installed bot preload state machine accepts guarded patch");
            Type client = find("BotProfileClient");
            MethodInfo load = client.GetMethod("LoadBots");
            check(Harmony.GetPatchInfo(load) == null, "Public bot preload Task wrapper is unchanged");
            // Avoid constructors: the native client constructor starts backup timers.
            object instance = FormatterServices.GetUninitializedObject(client);
            object emptyWaves = Activator.CreateInstance(load.GetParameters()[0].ParameterType);
            var policy = Make(() => new Options { Enabled = true, ReduceBotProfilePreloadDelay = true });
            PolicyHooks.Policy = policy;
            LoadingRun run = policy.Begin();
            SynchronizationContext.SetSynchronizationContext(null);
            ((Task)load.Invoke(instance, new[] { emptyWaves })).GetAwaiter().GetResult();
            check(run.BotPreloadDelaysReduced == 1 && run.BotPreloadDelayNominalSavedMs == 499,
                "Installed native preload executes the reduced delay once");
            check((bool)client.BaseType.GetField("_startProfilesLoaded").GetValue(instance), "Installed preload still reaches its native completion flag");
            check(client.GetField("_backupLoader").GetValue(instance) == null, "Installed fixture starts no backup timers or network requests");
            policy.Stop();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
            harmony.UnpatchSelf(); PolicyHooks.Policy = previous;
        }
    }
}
