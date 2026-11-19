using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace FuckThisLoadingScreen
{
    // Separate from _treesLock: never held during worker traversal, Sleep, or Join.
    // All dictionary mutations and result field writes share the reader's gate.
    internal static class DecalResultSynchronization
    {
        internal static readonly object Gate = new object();

        internal static void Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type tree = find("TextureQuadTree");
            if (tree.Module.ModuleVersionId != module)
                throw new NotSupportedException("Decal result synchronization requires the supported EFT module");
            MethodInfo read = AccessTools.DeclaredMethod(tree, "UpdateTextureRequestedMipMapLevel", Type.EmptyTypes);
            MethodInfo work = AccessTools.DeclaredMethod(tree, "WorkThread", new[] { typeof(object) });
            if (read?.ReturnType != typeof(void) || read.IsStatic || work?.ReturnType != typeof(void) || !work.IsStatic)
                throw new MissingMethodException("Decal results reader/worker ABI");
            // Validate both before installing either half. The caller rolls back this owner on failure.
            ReaderGuard(Original(read)).ToList();
            Worker(Original(work)).ToList();
            harmony.Patch(work, transpiler: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(Worker)));
            harmony.Patch(read, prefix: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(Exit)),
                transpiler: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(ReaderGuard)));
        }
        internal static IEnumerable<CodeInstruction> Original(MethodBase method)
        {
            // Harmony's net-framework ILGenerator optional argument cannot be bound by
            // netstandard at compile time; resolve the exact two-argument API at startup.
            MethodInfo get = typeof(PatchProcessor).GetMethods().Single(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 && !m.GetParameters()[1].ParameterType.IsByRef);
            return (IEnumerable<CodeInstruction>)get.Invoke(null, new object[] { method, null });
        }
        internal static void Enter(out bool __state)
        { __state = false; Monitor.Enter(Gate, ref __state); }
        internal static void Exit(bool __state)
        { if (__state) Monitor.Exit(Gate); }
        internal static void Add<T>(Dictionary<int, T> results, int key, T value)
        { lock (Gate) results.Add(key, value); }
        private static class Fields<T> where T : class
        {
            internal static readonly AccessTools.FieldRef<T, int> Frame = AccessTools.FieldRefAccess<T, int>("updatedDuringFrame");
            internal static readonly AccessTools.FieldRef<T, int> Mip = AccessTools.FieldRefAccess<T, int>("requestedMipMapLevel");
            internal static readonly AccessTools.FieldRef<T, float> H = AccessTools.FieldRefAccess<T, float>("pixelsHBuf");
            internal static readonly AccessTools.FieldRef<T, float> V = AccessTools.FieldRefAccess<T, float>("pixelsVBuf");
            internal static readonly AccessTools.FieldRef<T, float> Distance = AccessTools.FieldRefAccess<T, float>("closestDist");
        }
        internal static void Frame<T>(T value, int field) where T : class { lock (Gate) Fields<T>.Frame(value) = field; }
        internal static void Mip<T>(T value, int field) where T : class { lock (Gate) Fields<T>.Mip(value) = field; }
        internal static void H<T>(T value, float field) where T : class { lock (Gate) Fields<T>.H(value) = field; }
        internal static void V<T>(T value, float field) where T : class { lock (Gate) Fields<T>.V(value) = field; }
        internal static void Distance<T>(T value, float field) where T : class { lock (Gate) Fields<T>.Distance(value) = field; }

        internal static IEnumerable<CodeInstruction> ReaderGuard(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            if (code.Count(i => i.operand is MethodInfo m && m.Name == "GetEnumerator" && IsResults(m.DeclaringType)) != 1 ||
                code.Count(i => i.operand is FieldInfo f && f.Name == "_results" && IsResults(f.FieldType)) != 1)
                throw new MissingMethodException("Decal result enumeration shape changed");
            return code;
        }
        private static bool IsResults(Type type) => type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            type.GetGenericArguments()[0] == typeof(int) && type.GetGenericArguments()[1].Name == "ThreadResults";
        internal static IEnumerable<CodeInstruction> Worker(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.Select(i => new CodeInstruction(i)).ToList();
            var counts = new Dictionary<string, int>();
            int adds = 0;
            foreach (var instruction in code)
            {
                if (instruction.operand is MethodInfo method && method.Name == "Add" && IsResults(method.DeclaringType))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(DecalResultSynchronization), nameof(Add)).MakeGenericMethod(method.DeclaringType.GetGenericArguments()[1]);
                    adds++;
                }
                else if (instruction.opcode == OpCodes.Stfld && instruction.operand is FieldInfo field && field.DeclaringType.Name == "ThreadResults")
                {
                    string helper;
                    switch (field.Name)
                    {
                        case "updatedDuringFrame":
                            helper = nameof(Frame);
                            break;
                        case "requestedMipMapLevel":
                            helper = nameof(Mip);
                            break;
                        case "pixelsHBuf":
                            helper = nameof(H);
                            break;
                        case "pixelsVBuf":
                            helper = nameof(V);
                            break;
                        case "closestDist":
                            helper = nameof(Distance);
                            break;
                        default:
                            throw new MissingFieldException("Unknown worker result write: " + field.Name);
                    }
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.DeclaredMethod(typeof(DecalResultSynchronization), helper).MakeGenericMethod(field.DeclaringType);
                    counts[helper] = counts.TryGetValue(helper, out int n) ? n + 1 : 1;
                }
            }
            if (adds != 1 || counts.Count != 5 || counts[nameof(Mip)] != 2 || counts.Where(p => p.Key != nameof(Mip)).Any(p => p.Value != 1))
                throw new MissingMethodException("Decal worker result publication shape changed");
            return code;
        }
    }
}
