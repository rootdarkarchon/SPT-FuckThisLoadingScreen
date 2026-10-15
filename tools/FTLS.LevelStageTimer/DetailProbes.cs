using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace FTLS.LevelStageTimer
{
    // Call-site probes preserve the original arguments and synchronous execution order.
    // Do not patch System.GC or Action<T>.Invoke globally.
    internal static class DetailProbes
    {
        internal static MethodInfo StateMachineStep(MethodInfo method) =>
            method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        internal static void Install(Harmony harmony, Func<string, Type> findType, bool fika, List<string> coverage)
        {
            MethodInfo collect = (MethodInfo)HookCatalog.Resolve(new HookSpec("InGameMemoryManagement", "Collect", 5, ""), findType).Single();
            harmony.Patch(collect, transpiler: new HarmonyMethod(typeof(DetailProbes), nameof(CollectTranspiler)));
            coverage.Add("cleanup.gc-first / cleanup.finalizers / cleanup.gc-aggressive | call sites in " + collect);
            MethodInfo register = (MethodInfo)HookCatalog.Resolve(new HookSpec("EFT.GameWorld", "RegisterPlayer", 1, ""), findType).Single();
            harmony.Patch(register, transpiler: new HarmonyMethod(typeof(DetailProbes), nameof(RegisterTranspiler)));
            coverage.Add("callback.world-person-add[.handler] | Action<IPlayer>.Invoke in GameWorld.RegisterPlayer");
            if (!fika) return;
            MethodInfo create = (MethodInfo)HookCatalog.Resolve(new HookSpec("Fika.Core.Main.Players.FikaPlayer", "Create", 20, ""), findType).Single();
            MethodInfo step = StateMachineStep(create) ?? throw new MissingMethodException("FikaPlayer.Create async state machine");
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(DetailProbes), nameof(SpawnTranspiler)));
            coverage.Add("callback.player-spawned[.handler] | Action<FikaPlayer>.Invoke in " + step.DeclaringType.FullName);
        }

        internal static IEnumerable<CodeInstruction> CollectTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            Replace(list, typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes), nameof(CollectFirst));
            Replace(list, typeof(GC).GetMethod(nameof(GC.WaitForPendingFinalizers), Type.EmptyTypes), nameof(WaitFinalizers));
            Replace(list, typeof(GC).GetMethod(nameof(GC.Collect), new[] { typeof(int), typeof(GCCollectionMode), typeof(bool), typeof(bool) }), nameof(CollectAggressive));
            return list;
        }

        private static void Replace(List<CodeInstruction> list, MethodInfo original, string replacement)
        {
            CodeInstruction[] matches = list.Where(i => i.Calls(original)).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one cleanup call: " + original);
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.Method(typeof(DetailProbes), replacement);
        }

        internal static IEnumerable<CodeInstruction> SpawnTranspiler(IEnumerable<CodeInstruction> instructions) =>
            CallbackTranspiler(instructions, "Fika.Core.Main.Players.FikaPlayer", nameof(PlayerSpawned));

        internal static IEnumerable<CodeInstruction> RegisterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            CallbackTranspiler(instructions, "EFT.IPlayer", nameof(WorldPersonAdd));

        internal static IEnumerable<CodeInstruction> CallbackTranspiler(IEnumerable<CodeInstruction> instructions, string argumentType, string wrapper)
        {
            var list = instructions.ToList();
            CodeInstruction[] matches = list.Where(i => (i.opcode == OpCodes.Callvirt || i.opcode == OpCodes.Call) &&
                i.operand is MethodInfo m && m.Name == "Invoke" && m.DeclaringType.IsGenericType &&
                m.DeclaringType.GetGenericTypeDefinition() == typeof(Action<>) &&
                m.DeclaringType.GetGenericArguments()[0].FullName == argumentType).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one callback invoke: " + argumentType);
            var invoke = (MethodInfo)matches[0].operand;
            matches[0].opcode = OpCodes.Call;
            matches[0].operand = AccessTools.Method(typeof(DetailProbes), wrapper).MakeGenericMethod(invoke.DeclaringType.GetGenericArguments());
            return list; // Labels/exception blocks stay on the exact original call instruction.
        }

        internal static void PlayerSpawned<T>(Action<T> callback, T value) => Dispatch(callback, value, "callback.player-spawned");
        internal static void WorldPersonAdd<T>(Action<T> callback, T value) => Dispatch(callback, value, "callback.world-person-add");

        private static void Dispatch<T>(Action<T> callback, T value, string stage)
        {
            Span total = LoadingHooks.BeginDetail(stage);
            if (total == null) { callback(value); return; }
            bool completed = false;
            try
            {
                // Snapshot is equivalent to normal multicast invocation when subscribers change
                // during delivery. A throw stops delivery and propagates the same exception.
                Delegate[] handlers = callback.GetInvocationList();
                for (int i = 0; i < handlers.Length; i++)
                {
                    Span handler = null;
                    if (i < 64)
                    {
                        string detail = null;
                        try
                        {
                            MethodInfo method = handlers[i].Method;
                            detail = method.Module.Assembly.GetName().Name + " | " + method.DeclaringType?.FullName + "." + method.Name;
                        }
                        catch { }
                        handler = LoadingHooks.BeginDetail(stage + ".handler", detail: detail);
                    }
                    else handler = LoadingHooks.BeginDetail(stage + ".handler"); // Count suppressed subscribers too.
                    bool returned = false;
                    try { ((Action<T>)handlers[i])(value); returned = true; }
                    finally { LoadingHooks.EndDetail(handler, returned); }
                }
                completed = true;
            }
            finally { LoadingHooks.EndDetail(total, completed); }
        }

        internal static void CollectFirst()
        {
            Span span = LoadingHooks.BeginDetail("cleanup.gc-first"); bool completed = false;
            try { GC.Collect(); completed = true; }
            finally { LoadingHooks.EndDetail(span, completed); }
        }
        internal static void WaitFinalizers()
        {
            Span span = LoadingHooks.BeginDetail("cleanup.finalizers"); bool completed = false;
            try { GC.WaitForPendingFinalizers(); completed = true; }
            finally { LoadingHooks.EndDetail(span, completed); }
        }
        internal static void CollectAggressive(int generation, GCCollectionMode mode, bool blocking, bool compacting)
        {
            Span span = LoadingHooks.BeginDetail("cleanup.gc-aggressive"); bool completed = false;
            try { GC.Collect(generation, mode, blocking, compacting); completed = true; }
            finally { LoadingHooks.EndDetail(span, completed); }
        }
    }
}
