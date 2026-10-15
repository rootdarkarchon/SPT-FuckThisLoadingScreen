using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    internal sealed class SpawnReconciliation
    {
        internal SpawnReconciliation Previous;
        internal LoadingRun Run;
        internal bool Closed;
        internal void Close()
        {
            if (Closed) return;
            Closed = true;
            if (ReferenceEquals(SpawnZoneHooks.Reconciliation, this)) SpawnZoneHooks.Reconciliation = Previous;
            Previous = null; Run = null;
        }
    }

    internal sealed class SpawnZoneScope
    {
        internal SpawnZoneScope Previous;
        internal SpawnReconciliation Window;
        internal Array Zones;
        internal bool Closed, Invalidated;
        internal void Close()
        {
            if (Closed) return;
            Closed = true;
            if (ReferenceEquals(SpawnZoneHooks.Lookup, this)) SpawnZoneHooks.Lookup = Previous;
            Previous = null; Window = null; Zones = null;
        }
    }

    // Only the one FindObjectsProxy<BotZone> call inside Create(params, GameObject)
    // is redirected. Native FirstOrDefault/name matching and marker attachment remain intact.
    internal static class SpawnZoneHooks
    {
        [ThreadStatic] internal static SpawnReconciliation Reconciliation;
        [ThreadStatic] internal static SpawnZoneScope Lookup;
        internal static MethodInfo NativeFindMethod;

        internal static class Native<T> where T : UnityEngine.Object
        {
            internal static Func<T[]> Find;
        }

        internal static void BindNative<T>(MethodInfo method) where T : UnityEngine.Object =>
            Native<T>.Find = (Func<T[]>)Delegate.CreateDelegate(typeof(Func<T[]>), method);

        internal static void CollectionPrefix(out SpawnReconciliation __state)
        {
            __state = null;
            LoadingRun run = PolicyHooks.Policy?.SpawnZoneRun;
            if (run == null) return;
            __state = new SpawnReconciliation { Previous = Reconciliation, Run = run };
            Reconciliation = __state;
        }
        internal static void CollectionPostfix(SpawnReconciliation __state) => __state?.Close();
        internal static void CollectionFinalizer(Exception __exception, SpawnReconciliation __state)
        { if (__exception != null) __state?.Close(); }

        internal static void FixPrefix(object[] __args, out SpawnZoneScope __state)
        {
            __state = null;
            SpawnReconciliation window = Reconciliation;
            if (window == null || window.Closed || !ReferenceEquals(window.Run, PolicyHooks.Policy?.SpawnZoneRun)) return;
            // Native CreateFromScene supplies this freshly discovered, ordered zone array.
            // FixSpawns mutates spawn markers only, not the BotZone population.
            __state = new SpawnZoneScope { Previous = Lookup, Window = window, Zones = __args[2] as Array };
            Lookup = __state;
            PolicyHooks.Policy?.CountSpawnZoneScope();
        }
        internal static void FixPostfix(SpawnZoneScope __state) => __state?.Close();
        internal static void FixFinalizer(Exception __exception, SpawnZoneScope __state)
        { if (__exception != null) __state?.Close(); }

        internal static T[] FindZones<T>() where T : UnityEngine.Object
        {
            SpawnZoneScope scope = Lookup;
            if (scope != null && !scope.Closed && !scope.Invalidated &&
                ReferenceEquals(scope.Window?.Run, PolicyHooks.Policy?.SpawnZoneRun) && scope.Window?.Run != null)
            {
                if (scope.Zones is T[] zones && AreLive(zones))
                {
                    PolicyHooks.Policy?.CountSpawnZoneLookup(true);
                    return zones; // Retain the original order and duplicate-name first match.
                }
                scope.Invalidated = true;
                PolicyHooks.Policy?.CountSpawnZoneInvalidation();
            }
            PolicyHooks.Policy?.CountSpawnZoneLookup(false);
            return Native<T>.Find();
        }

        private static bool AreLive<T>(T[] zones) where T : UnityEngine.Object
        {
            foreach (T zone in zones)
                if (zone == null || (zone is Component component && !component.gameObject.activeInHierarchy)) return false;
            return true;
        }

        internal static IEnumerable<CodeInstruction> MarkerTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            CodeInstruction[] calls = code.Where(i => i.Calls(NativeFindMethod)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Spawn marker ABI mismatch; expected exactly one BotZone discovery call.");
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(SpawnZoneHooks), nameof(FindZones)).MakeGenericMethod(NativeFindMethod.GetGenericArguments());
            return code;
        }

        internal static void Reset()
        {
            // Main-thread synchronous scopes normally close before control returns to Unity.
            Lookup = null; Reconciliation = null;
        }
    }
}
