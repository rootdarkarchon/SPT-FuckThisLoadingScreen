using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    internal static class BotProfilePreloadDelay
    {
        internal const int NativeMilliseconds = 500;
        internal const int ReducedMilliseconds = 1;
        private static readonly MethodInfo Delay = typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(int) });
        // Access labels without importing Unity/Mono's mscorlib Label into netstandard.
        private static readonly FieldInfo Labels = AccessTools.Field(typeof(CodeInstruction), "labels");

        internal static MethodInfo Install(Harmony harmony, Func<string, Type> find)
        {
            Type client = find("BotProfileClient");
            if (client.Module.ModuleVersionId != new Guid("cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7"))
                throw new NotSupportedException("Unsupported bot profile module");
            MethodInfo load = AccessTools.GetDeclaredMethods(client).Single(m => m.Name == "LoadBots" &&
                m.ReturnType == typeof(Task) && m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsGenericType &&
                m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(List<>) &&
                m.GetParameters()[0].ParameterType.GetGenericArguments()[0].FullName == "EFT.CountTypeBotWave");
            Type state = load.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                ?? throw new MissingMethodException("Bot preload state machine missing");
            MethodInfo step = AccessTools.DeclaredMethod(state, "MoveNext");
            if (step == null || step.IsStatic || step.ReturnType != typeof(void) || step.GetParameters().Length != 0)
                throw new MissingMethodException("Bot preload MoveNext shape changed");
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(BotProfilePreloadDelay), nameof(Transpiler)));
            return step;
        }

        // Change only the delay argument. The native await, requests, profile handling,
        // exception regions and Task identity stay native; no MoveNext wrapper is added.
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(Delay)).ToArray();
            if (calls.Length != 1 || calls[0] == 0)
                throw new InvalidOperationException("Expected one initial bot preload Task.Delay(int)");
            int slot = calls[0];
            if (code[slot - 1].opcode != OpCodes.Ldc_I4 || !(code[slot - 1].operand is int value) ||
                value != NativeMilliseconds || ((ICollection)Labels.GetValue(code[slot])).Count != 0 || code[slot].blocks.Count != 0)
                throw new InvalidOperationException("Initial bot preload delay is not the guarded 500 ms constant");
            code.Insert(slot, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(BotProfilePreloadDelay), nameof(EffectiveDelay))));
            return code;
        }

        internal static int EffectiveDelay(int milliseconds) =>
            PolicyHooks.Policy?.BotProfileDelay(milliseconds) ?? milliseconds;
    }
}
