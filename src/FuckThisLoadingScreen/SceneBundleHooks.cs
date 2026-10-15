using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FuckThisLoadingScreen
{
    internal enum ProbeFallback { Shape, BuiltIn, Location, Manifest, Registered, ExistingOperation, File, ObserverError }

    // Redirect only the preliminary bundle call in the native scene coroutine. All other
    // bundle callers and the scene loader/activation code retain their original behavior.
    internal static class SceneBundleHooks
    {
        private static readonly char[] AmbiguousUriCharacters = { ':', '?', '#', '%', '\\' };
        internal static MethodInfo NativeMethod;
        internal static Func<string, bool> IsBuiltIn = ExactBuiltIn;
        internal static string ExpectedDirectory;
        internal static Func<object, string> Url;
        internal static Func<object, IDictionary> Manifest, Loaded, Pending;
        internal static Func<IDictionary> Registered;

        internal sealed class SkippedProbe<T> : Operation<T>
        {
            // The existing caller awaits completion and ignores the failure/result. Keep
            // that contract; never fabricate a successfully loaded AssetBundle or cache it.
            internal SkippedProbe() { Error = "FTLS skipped redundant built-in scene bundle probe"; }
        }
        private static class Native<TManager, TAsset>
        { internal static Func<TManager, string, bool, IOperation<TAsset>> Load; }
        internal static void Bind<TManager, TAsset>(MethodInfo method) =>
            Native<TManager, TAsset>.Load = (Func<TManager, string, bool, IOperation<TAsset>>)Delegate.CreateDelegate(
                typeof(Func<TManager, string, bool, IOperation<TAsset>>), method);

        internal static bool ExactBuiltIn(string path)
        {
            int index = SceneUtility.GetBuildIndexByScenePath(path);
            return index >= 0 && string.Equals(SceneUtility.GetScenePathByBuildIndex(index), path, StringComparison.Ordinal);
        }

        internal static bool CanSkip(object manager, string bundle, string scene, out ProbeFallback reason)
        {
            reason = ProbeFallback.Shape;
            if (manager == null || string.IsNullOrEmpty(bundle) || !bundle.EndsWith(".unity", StringComparison.Ordinal) ||
                bundle.IndexOfAny(AmbiguousUriCharacters) >= 0 || !string.Equals(bundle, scene, StringComparison.Ordinal)) return false;
            reason = ProbeFallback.BuiltIn;
            if (!IsBuiltIn(scene)) return false;
            reason = ProbeFallback.Location;
            string url = Url(manager);
            if (url == null || !url.EndsWith("/", StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !uri.IsFile || uri.IsUnc ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !string.Equals(Path.GetFullPath(uri.LocalPath).TrimEnd('/', '\\'), ExpectedDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            reason = ProbeFallback.Manifest;
            // Inspect the current dictionary rather than caching a manifest snapshot.
            IDictionary manifest = Manifest(manager);
            if (manifest == null || manifest.Contains(bundle)) return false;
            reason = ProbeFallback.Registered;
            IDictionary registered = Registered();
            if (registered == null || registered.Contains(bundle)) return false;
            reason = ProbeFallback.ExistingOperation;
            IDictionary loaded = Loaded(manager), pending = Pending(manager);
            if (loaded == null || pending == null || loaded.Contains(bundle) || pending.Contains(bundle)) return false;
            reason = ProbeFallback.File;
            string target = Path.GetFullPath(Path.Combine(uri.LocalPath, bundle));
            if (!target.StartsWith(ExpectedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            try { System.IO.File.GetAttributes(target); return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            // Access/IO errors propagate to the wrapper's fail-closed native fallback.
            return true;
        }

        internal static IOperation<TAsset> Load<TManager, TAsset>(TManager manager, string bundle, bool logErrors, string scene)
        {
            LoadingRun run = PolicyHooks.Policy?.SceneProbeRun;
            if (run == null) return Native<TManager, TAsset>.Load(manager, bundle, logErrors);
            long started = Stopwatch.GetTimestamp();
            bool skip = false;
            ProbeFallback reason = ProbeFallback.ObserverError;
            try { if (run.Options.SkipBuiltInSceneProbes) skip = CanSkip(manager, bundle, scene, out reason); }
            catch (Exception error)
            {
                reason = ProbeFallback.ObserverError;
                if (run.SceneProbeFirstError == null) run.SceneProbeFirstError = error.GetType().Name;
            }
            run.SceneProbeRequests++;
            run.SceneProbeGuardTicks += Stopwatch.GetTimestamp() - started;
            if (skip) { run.SceneProbeSkipped++; return new SkippedProbe<TAsset>(); }
            run.SceneProbeNative++;
            if (run.Options.SkipBuiltInSceneProbes) run.SceneProbeFallbacks[(int)reason]++;
            return Native<TManager, TAsset>.Load(manager, bundle, logErrors);
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            FieldInfo scene = __originalMethod.DeclaringType.GetField("sceneName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (scene?.FieldType != typeof(string)) throw new MissingFieldException("Scene coroutine sceneName ABI");
            return Rewrite(instructions, scene);
        }
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, FieldInfo scene)
        {
            var result = new List<CodeInstruction>(); int count = 0;
            foreach (var original in instructions)
            {
                var code = new CodeInstruction(original);
                if (code.Calls(NativeMethod))
                {
                    var entry = new CodeInstruction(OpCodes.Ldarg_0);
                    entry.MoveLabelsFrom(code); entry.MoveBlocksFrom(code);
                    result.Add(entry); result.Add(new CodeInstruction(OpCodes.Ldfld, scene));
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(SceneBundleHooks), nameof(Load)).MakeGenericMethod(
                        NativeMethod.DeclaringType, NativeMethod.ReturnType.GetGenericArguments().Single());
                    count++;
                }
                result.Add(code);
            }
            if (count != 1) throw new InvalidOperationException("Scene bundle probe ABI mismatch: " + count);
            return result;
        }

        internal static void Install(Harmony harmony, Func<string, Type> find)
        {
            Type manager = find("EFT.AssetsManager.BundlesManager");
            NativeMethod = AccessTools.DeclaredMethod(manager, "LoadBundleAsync", new[] { typeof(string), typeof(bool) });
            Type operation = NativeMethod?.ReturnType;
            if (operation == null || !operation.IsGenericType || operation.GetGenericTypeDefinition() != typeof(IOperation<>))
                throw new MissingMethodException("Scene bundle operation ABI");
            AccessTools.Method(typeof(SceneBundleHooks), nameof(Bind)).MakeGenericMethod(manager, operation.GetGenericArguments()[0]).Invoke(null, new object[] { NativeMethod });
            PropertyInfo url = manager.GetProperty("DownloadingUrl"), manifest = manager.GetProperty("Manifest");
            FieldInfo details = manifest == null ? null : AccessTools.Field(manifest.PropertyType, "m_Details");
            FieldInfo loaded = AccessTools.Field(manager, "_bundles"), pending = AccessTools.Field(manager, "_loadBundleOperations");
            FieldInfo mods = AccessTools.Field(find("SPT.Custom.Utils.BundleManager"), "Bundles");
            if (url?.PropertyType != typeof(string) || details == null || loaded == null || pending == null || mods == null ||
                new[] { details, loaded, pending, mods }.Any(f => !typeof(IDictionary).IsAssignableFrom(f.FieldType)))
                throw new MissingMemberException("Scene probe live dictionary ABI");
            Url = obj => (string)url.GetValue(obj);
            Manifest = obj => (IDictionary)details.GetValue(manifest.GetValue(obj));
            Loaded = obj => (IDictionary)loaded.GetValue(obj); Pending = obj => (IDictionary)pending.GetValue(obj);
            Registered = () => (IDictionary)mods.GetValue(null);
            ExpectedDirectory = Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, "Windows")).TrimEnd('/', '\\');
            Type scene = find("EFT.AssetsManager.AssetsManager+LoadSceneOperation");
            MethodInfo coroutine = AccessTools.GetDeclaredMethods(scene).Single(m => m.Name == "Coroutine" && m.GetParameters().Length == 6);
            MethodInfo step = coroutine.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (step == null) throw new MissingMethodException("Scene coroutine state machine ABI");
            // Timer probes must see the native call first; preserve their surrounding hooks.
            harmony.Patch(step, transpiler: new HarmonyMethod(typeof(SceneBundleHooks), nameof(Transpiler)) { priority = Priority.Last });
        }
    }
}
