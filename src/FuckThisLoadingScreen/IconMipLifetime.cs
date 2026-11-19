using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // A render owns its requests until its Task terminates, including failed/canceled renders.
    // AsyncLocal follows native awaits; terminal cleanup drops model/texture references.
    internal static class IconMipLifetime
    {
        internal sealed class Scope
        {
            internal Scope Previous;
            internal readonly HashSet<Texture2D> Textures = new HashSet<Texture2D>(Identity<Texture2D>.Instance);
            internal readonly HashSet<StreamingController> Cameras = new HashSet<StreamingController>(Identity<StreamingController>.Instance);
            internal bool Budget, ItemCount, PlayerCount, Finished, Disposing;
            internal bool ItemRender, PlayerRender;
            internal GameObject Model;
            internal object Creator;
            internal bool OwnsPlayerContainer;
            internal IconMipProbe Probe;
        }
        private sealed class TextureState
        {
            internal int Owners, Requested, Minimum;
            internal bool HasRequested, HasMinimum;
        }
        private sealed class BiasState { internal int Owners; internal float Before, Applied; }
        private sealed class Identity<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly Identity<T> Instance = new Identity<T>();
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
        internal static readonly AsyncLocal<Scope> Current = new AsyncLocal<Scope>();
        private static Scope LiveScope => Current.Value != null && (!Current.Value.Finished || Current.Value.Disposing) ? Current.Value : null;
        // Native ClearMinimumMipmapLevel reports 8 in this Unity build, for both
        // 64px (7 mips) and 1024px (11 mips) imported streaming textures. It is not
        // derived from a texture's mip count. Streaming-disabled getters report -1.
        internal const int ClearedMinimumMip = 8;
        private static readonly Dictionary<Texture2D, TextureState> Textures = new Dictionary<Texture2D, TextureState>(Identity<Texture2D>.Instance);
        private static readonly Dictionary<StreamingController, BiasState> Cameras = new Dictionary<StreamingController, BiasState>(Identity<StreamingController>.Instance);
        [SuppressMessage("Style", "IDE1006", Justification = "Assigned by name through reflection from the validation harness")]
        private static FieldInfo itemCount, playerCount, itemBudget, playerBudget, controller, playerBusy;
        private static FieldInfo _cameraInstance;
        [SuppressMessage("Style", "IDE1006", Justification = "Assigned by name through reflection from the validation harness")]
        private static MethodInfo playerComplete;
        [SuppressMessage("Style", "IDE1006", Justification = "Assigned by name through reflection from the validation harness")]
        private static Func<float> nativeIncrease;
        [SuppressMessage("Style", "IDE1006", Justification = "Assigned by name through reflection from the validation harness")]
        private static Action nativeRestore;
        private static int _budgetOwners;
        private static int _activeScopes;
        private static Action _drained;
        private static float _budgetBefore, _budgetApplied;
        private static int _reductionBefore;
        internal static Action<string> Warning;
        internal static long Completed, ReleasedTextures, Failed;
        internal static int ActiveTextures => Textures.Count;
        internal static int ActiveBudgets => _budgetOwners;
        internal static int ActiveScopes => _activeScopes;

        internal static void Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type item = find("ItemIconCreator"), player = find("EFT.PlayerIcons.PlayerIconCreator"), graphics = find("IconCreatorGraphicsSettings"),
                policy = find("EFT.Settings.Graphics.GraphicsSettingsController"), camera = find("EFT.CameraControl.CameraManager");
            if (new[] { item, player, graphics, policy, camera }.Any(t => t.Module.ModuleVersionId != module))
                throw new NotSupportedException("Icon mip lifetimes require the supported EFT module");
            itemCount = RequireField(item, "requests", typeof(int), true);
            playerCount = RequireField(graphics, "_requests", typeof(int), true);
            itemBudget = RequireField(item, "mipBudget", typeof(float), true);
            playerBudget = RequireField(graphics, "_mipBudget", typeof(float), true);
            controller = RequireField(camera, "_streamingController", typeof(StreamingController), false);
            _cameraInstance = RequireField(camera, "instance", camera, true);
            playerBusy = RequireField(player, "_containerIsBusy", typeof(bool), false);
            playerComplete = AccessTools.DeclaredMethod(player, "CompleteRender", Type.EmptyTypes);
            MethodInfo increase = AccessTools.DeclaredMethod(policy, "IncreaseMemoryBudgetForIcon", Type.EmptyTypes);
            MethodInfo restore = AccessTools.DeclaredMethod(policy, "RestoreMemoryBudget", Type.EmptyTypes);
            if (increase?.ReturnType != typeof(float) || !increase.IsStatic || restore?.ReturnType != typeof(void) || !restore.IsStatic)
                throw new MissingMethodException("Icon budget ABI");
            FieldInfo baseBudget = RequireField(policy, "MipStreamingMemoryBudget", typeof(float), true);
            FieldInfo baseReduction = RequireField(policy, "StreamingMipmapsMaxLevelReduction", typeof(int), true);
            // These are the exact supported native policy bodies. Calling a delegate to
            // the patched methods would recurse, so read their guarded base fields.
            nativeIncrease = () => { QualitySettings.streamingMipmapsMaxLevelReduction = 4; float value = (float)baseBudget.GetValue(null); QualitySettings.streamingMipmapsMemoryBudget = value + 100; return value; };
            nativeRestore = () => { QualitySettings.streamingMipmapsMaxLevelReduction = (int)baseReduction.GetValue(null); QualitySettings.streamingMipmapsMemoryBudget = (float)baseBudget.GetValue(null); };
            foreach (Type root in new[] { item, player })
            {
                MethodInfo render = root.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Single(m => m.Name == "RenderModel");
                if (!render.ReturnType.IsGenericType || render.ReturnType.GetGenericTypeDefinition() != typeof(Task<>))
                    throw new MissingMethodException("Icon render Task ABI");
                harmony.Patch(render, prefix: Hook(nameof(RenderBegin)), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IconMipLifetime), nameof(End)).MakeGenericMethod(render.ReturnType.GetGenericArguments())), finalizer: Hook(nameof(LaunchFailed)));
            }
            // No detours on compiler-generated MoveNext methods: a no-op roundtrip
            // of the native item request's nested finally blocks breaks completion
            // in the supported Unity/Mono runtime. Patch ordinary entry points only.
            MethodInfo itemRender = AccessTools.GetDeclaredMethods(item).Single(m => m.Name == "RenderModel");
            var state = itemRender.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ?? throw new MissingMethodException("Item render async ABI");
            var nativeFlow = DecalResultSynchronization.Original(AccessTools.DeclaredMethod(state, "MoveNext")).ToList();
            MethodInfo returned = nativeFlow.Select(i => i.operand).OfType<MethodInfo>().Single(m => m.Name == "ReturnToPool" && m.GetParameters()[0].ParameterType == typeof(GameObject));
            if (nativeFlow.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(Interlocked) && m.Name == "Increment") != 1 ||
                nativeFlow.Count(i => i.operand is MethodInfo m && m.DeclaringType == typeof(Interlocked) && m.Name == "Decrement") != 1)
                throw new MissingMethodException("Native item render counter flow changed");
            harmony.Patch(returned, prefix: Hook(nameof(ModelReturned)));
            harmony.Patch(AccessTools.DeclaredMethod(graphics, "Prepare"), prefix: Hook(nameof(Prepare)));
            harmony.Patch(AccessTools.DeclaredMethod(graphics, "Restore"), prefix: Hook(nameof(RestorePlayer)));
            harmony.Patch(AccessTools.DeclaredMethod(player, "WaitForRender"), postfix: Hook(nameof(ContainerAcquired)));
            harmony.Patch(playerComplete, postfix: new HarmonyMethod(typeof(IconMipLifetime), nameof(ContainerReleased)) { priority = Priority.First });
            foreach (Type owner in new[] { item, graphics })
            {
                harmony.Patch(AccessTools.DeclaredMethod(owner, "RequestMipZero"), prefix: Hook(nameof(Request)));
                harmony.Patch(AccessTools.DeclaredMethod(owner, "ResetRequestedMip"), prefix: Hook(nameof(Reset)));
            }
            harmony.Patch(AccessTools.DeclaredMethod(graphics, "RequestMipZeroFast"), prefix: Hook(nameof(Request)));
            harmony.Patch(increase, prefix: Hook(nameof(IncreasePolicy)));
            harmony.Patch(restore, prefix: Hook(nameof(RestorePolicy)));
            harmony.Patch(AccessTools.DeclaredMethod(camera, "IncreaseStreamingMipMapBias"), prefix: Hook(nameof(BiasPolicy)));
            // If settings change during an icon batch, adopt the newly selected base policy.
            harmony.Patch(AccessTools.DeclaredMethod(policy, "RefreshStreamingMemoryBudget"), postfix: Hook(nameof(PolicyRefreshed)));
        }
        private static FieldInfo RequireField(Type type, string name, Type fieldType, bool isStatic)
        {
            FieldInfo field = AccessTools.Field(type, name);
            if (field == null || field.FieldType != fieldType || field.IsStatic != isStatic)
                throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(IconMipLifetime), name);
        internal static void Begin(out Scope __state)
        { __state = new Scope { Previous = Current.Value }; _activeScopes++; Current.Value = __state; }
        internal static void RenderBegin(MethodBase __originalMethod, object __instance, out Scope __state)
        {
            Begin(out __state);
            __state.ItemRender = __originalMethod.DeclaringType.Name == "ItemIconCreator";
            __state.PlayerRender = !__state.ItemRender;
            __state.Creator = __instance;
            __state.Probe = IconMipProbe.Start(__state);
        }
        internal static void End<T>(ref Task<T> __result, Scope __state)
        {
            Current.Value = __state.Previous;
            __result = Finish(__result, __state);
        }
        internal static void LaunchFailed(Exception __exception, Scope __state)
        {
            if (__exception == null || __state == null)
                return;
            Current.Value = __state.Previous;
            Release(__state, true);
        }
        internal static async Task<T> Finish<T>(Task<T> task, Scope scope)
        {
            bool failed = true;
            try
            { T result = await task; failed = false; return result; }
            finally { Release(scope, failed); }
        }
        internal static void Release(Scope scope, bool failed)
        {
            if (scope.Finished)
                return;
            scope.Finished = true;
            scope.Disposing = true;
            // Independent resources must all be released even if a destroyed Unity object throws.
            foreach (Texture2D texture in scope.Textures)
                Safely(() => ReleaseTexture(texture));
            foreach (StreamingController camera in scope.Cameras)
                Safely(() => ReleaseCamera(camera));
            if (failed && scope.OwnsPlayerContainer)
                Safely(() =>
            {
                Scope previous = Current.Value;
                Current.Value = scope;
                try
                { playerComplete.Invoke(scope.Creator, null); }
                finally
                {
                    // A failed DisposeBody must not leave every queued render (and
                    // its policy lease) waiting on the failed owner's busy flag.
                    try
                    { playerBusy.SetValue(scope.Creator, false); scope.OwnsPlayerContainer = false; }
                    finally { Current.Value = previous; }
                }
            });
            // A successful native item render already decremented its counter.
            // Faults before that point are tracked by the policy/request entry;
            // last-owner restore and exact-model pool return mark native cleanup.
            if (failed && scope.ItemCount)
                Safely(() =>
            {
                int remaining = Math.Max(0, (int)itemCount.GetValue(null) - 1);
                itemCount.SetValue(null, remaining);
                if (itemBudget != null)
                    itemBudget.SetValue(null, remaining == 0 ? 0f : Sentinel(_budgetBefore));
            });
            if (scope.PlayerCount)
                Safely(() =>
            {
                int remaining = Math.Max(0, (int)playerCount.GetValue(null) - 1);
                playerCount.SetValue(null, remaining);
                if (remaining == 0 && playerBudget != null)
                    playerBudget.SetValue(null, 0f);
            });
            if (scope.Budget && --_budgetOwners == 0)
                Safely(() =>
            {
                if (MipBudgetAccounting.Active != null)
                    MipBudgetAccounting.Active.IconsFinished();
                else if (QualitySettings.streamingMipmapsMemoryBudget == _budgetApplied)
                    QualitySettings.streamingMipmapsMemoryBudget = _budgetBefore;
                if (QualitySettings.streamingMipmapsMaxLevelReduction == 4)
                    QualitySettings.streamingMipmapsMaxLevelReduction = _reductionBefore;
            });
            Completed++;
            if (failed)
                Failed++;
            if (scope.Probe != null)
                Safely(() => scope.Probe.Finish(failed));
            scope.Probe = null;
            scope.Textures.Clear();
            scope.Cameras.Clear();
            scope.Model = null;
            scope.Creator = null;
            scope.Previous = null;
            scope.Disposing = false;
            if (--_activeScopes == 0 && _drained != null)
            { Action callback = _drained; _drained = null; Safely(callback); }
        }
        internal static void WhenDrained(Action callback)
        { if (_activeScopes == 0) callback(); else _drained += callback; }
        private static void Safely(Action action)
        { try { action(); } catch (Exception e) { try { Warning?.Invoke("Icon mip cleanup failed: " + e.GetType().Name + ": " + e.Message); } catch { } } }
        private static TextureState Own(Texture2D texture)
        {
            Scope scope = LiveScope;
            if (scope == null || scope.Finished || !texture || !texture.streamingMipmaps)
                return null;
            if (!Textures.TryGetValue(texture, out TextureState state))
                Textures.Add(texture, state = new TextureState());
            if (scope.Textures.Add(texture))
                state.Owners++;
            return state;
        }
        internal static int TextureOwners(Texture2D texture) => Textures.TryGetValue(texture, out var state) ? state.Owners : 0;
        internal static void SetRequested(Texture2D texture, int mip)
        {
            TextureState state = Own(texture);
            if (state != null && !state.HasRequested)
            { state.Requested = texture.requestedMipmapLevel; state.HasRequested = true; }
            texture.requestedMipmapLevel = mip;
        }
        internal static void SetMinimum(Texture2D texture, int mip)
        {
            TextureState state = Own(texture);
            if (state != null && !state.HasMinimum)
            { state.Minimum = texture.minimumMipmapLevel; state.HasMinimum = true; }
            texture.minimumMipmapLevel = mip;
        }
        internal static void ClearRequested(Texture2D texture)
        { if (LiveScope == null && !Textures.ContainsKey(texture)) texture.ClearRequestedMipmapLevel(); }
        internal static void ClearMinimum(Texture2D texture)
        { if (LiveScope == null && !Textures.ContainsKey(texture)) texture.ClearMinimumMipmapLevel(); }
        private static void ReleaseTexture(Texture2D texture)
        {
            TextureState state = Textures[texture];
            if (--state.Owners != 0)
                return;
            Textures.Remove(texture);
            ReleasedTextures++;
            if (!texture)
                return;
            if (state.HasRequested && texture.requestedMipmapLevel == 0)
            { if (state.Requested < 0) texture.ClearRequestedMipmapLevel(); else texture.requestedMipmapLevel = state.Requested; }
            if (state.HasMinimum && texture.minimumMipmapLevel == 0)
            { if (state.Minimum < 0 || state.Minimum == ClearedMinimumMip) texture.ClearMinimumMipmapLevel(); else texture.minimumMipmapLevel = state.Minimum; }
        }
        private static float Sentinel(float budget) => budget == 0 ? 1f : budget;
        internal static bool IncreasePolicy(ref float __result)
        {
            Scope scope = LiveScope;
            if (scope == null)
                return true;
            if (scope.ItemRender)
                scope.ItemCount = true;
            __result = Sentinel(Increase());
            return false;
        }
        internal static bool RestorePolicy()
        {
            Scope scope = LiveScope;
            if (scope == null)
                return true;
            if (scope.ItemRender)
                scope.ItemCount = false; // native last-owner decrement already ran
            return false;
        }
        internal static bool BiasPolicy(object __instance, float __0, ref float __result)
        {
            if (LiveScope == null)
                return true;
            __result = Bias(__instance, __0);
            return false;
        }
        private static void OwnPolicyAndCamera()
        {
            Increase();
            object camera = _cameraInstance?.GetValue(null);
            if (camera != null)
                Bias(camera, 2f);
        }
        internal static bool Prepare(ref Task __result)
        {
            Scope scope = LiveScope;
            if (scope == null)
                return true;
            playerCount.SetValue(null, (int)playerCount.GetValue(null) + 1);
            scope.PlayerCount = true;
            OwnPolicyAndCamera();
            playerBudget.SetValue(null, Sentinel(_budgetBefore));
            __result = Task.CompletedTask;
            return false;
        }
        internal static bool RestorePlayer()
        {
            Scope scope = LiveScope;
            if (scope == null)
                return true;
            if (!scope.PlayerCount)
                return false;
            int remaining = Math.Max(0, (int)playerCount.GetValue(null) - 1);
            playerCount.SetValue(null, remaining);
            scope.PlayerCount = false;
            if (remaining == 0)
                playerBudget.SetValue(null, 0f);
            return false;
        }
        internal static void ContainerAcquired(ref Task __result)
        {
            Scope scope = LiveScope;
            if (scope?.PlayerRender == true)
                __result = TrackContainer(__result, scope);
        }
        internal static async Task TrackContainer(Task native, Scope scope)
        { await native; scope.OwnsPlayerContainer = true; }
        internal static void ContainerReleased()
        { if (LiveScope?.PlayerRender == true) LiveScope.OwnsPlayerContainer = false; }
        internal static void ModelReturned(GameObject __0)
        {
            Scope scope = LiveScope;
            if (scope != null && scope.ItemRender && ReferenceEquals(scope.Model, __0))
                scope.ItemCount = false;
        }
        internal static bool Reset(ref Task __result)
        {
            if (LiveScope == null)
                return true;
            __result = Task.CompletedTask;
            return false;
        }
        internal static bool Request(GameObject __0, MethodBase __originalMethod, ref Task<bool> __result)
        {
            Scope scope = LiveScope;
            if (scope == null)
                return true;
            bool player = __originalMethod.DeclaringType.Name == "IconCreatorGraphicsSettings";
            if (scope.ItemRender && !player)
                scope.ItemCount = true;
            scope.Model = __0;
            OwnPolicyAndCamera();
            bool fast = __originalMethod.Name == "RequestMipZeroFast";
            if (scope.Probe == null)
                scope.Probe = IconMipProbe.Start(scope);
            var probe = scope.Probe?.BeginRequest(__0, player ? (fast ? "player-fast" : "player-slow") : "item");
            __result = RequestMips(__0, player, fast, probe);
            if (probe != null)
                __result = probe.Observe(__result);
            return false;
        }
        private struct WaitingTexture
        {
            internal Texture2D Texture;
            internal int Attempts;
            internal bool Logged;
            internal IconMipProbe.TextureRecord Probe;
        }
        private static async Task<bool> RequestMips(GameObject model, bool player, bool fast, IconMipProbe.RequestProbe probe)
        {
            using (UnityEngine.Pool.CollectionPool<List<WaitingTexture>, WaitingTexture>.Get(out var pending))
            using (UnityEngine.Pool.CollectionPool<HashSet<Texture2D>, Texture2D>.Get(out var seen))
            using (UnityEngine.Pool.CollectionPool<List<Material>, Material>.Get(out var materials))
            using (UnityEngine.Pool.CollectionPool<List<string>, string>.Get(out var names))
            {
                foreach (MeshRenderer renderer in model.GetComponentsInChildren<MeshRenderer>())
                {
                    materials.Clear();
                    renderer.GetSharedMaterials(materials);
                    foreach (Material material in materials)
                    {
                        if (!material)
                            continue;
                        names.Clear();
                        material.GetTexturePropertyNames(names);
                        foreach (string name in names)
                        {
                            if (!(material.GetTexture(name) is Texture2D texture) || !texture || texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D || !seen.Add(texture))
                                continue;
                            var textureProbe = probe?.Collect(texture, material, name);
                            SetRequested(texture, 0);
                            SetMinimum(texture, 0);
                            pending.Add(new WaitingTexture { Texture = texture, Probe = textureProbe });
                            probe?.Requested(textureProbe);
                        }
                    }
                }
                if (player && fast)
                {
                    for (int attempt = 0; attempt < 50; attempt++)
                    {
                        await Task.Yield();
                        bool loaded = true;
                        for (int n = 0; n < pending.Count; n++)
                            if (!pending[n].Texture.IsRequestedMipmapLevelLoaded())
                            { loaded = false; break; }
                        if (loaded)
                            return true;
                    }
                    if (probe != null)
                        for (int n = 0; n < pending.Count; n++)
                            if (!pending[n].Texture.IsRequestedMipmapLevelLoaded())
                                probe.Timeout(pending[n].Probe, 50);
                    return false;
                }
                if (player)
                {
                    bool waiting;
                    bool loaded;
                    do
                    {
                        await Task.Yield();
                        waiting = false;
                        loaded = true;
                        for (int n = 0; n < pending.Count; n++)
                        {
                            WaitingTexture value = pending[n];
                            if (value.Texture.IsRequestedMipmapLevelLoaded())
                                continue;
                            loaded = false;
                            if (value.Attempts < 200)
                            { value.Attempts++; pending[n] = value; waiting = true; break; }
                            if (!value.Logged)
                            { probe?.Timeout(value.Probe, value.Attempts); Debug.LogError("Mip 0 waiting timeout: " + value.Texture.name); value.Logged = true; pending[n] = value; }
                        }
                    } while (waiting);
                    return loaded;
                }
                bool result = true;
                while (pending.Count > 0)
                {
                    for (int n = pending.Count - 1; n >= 0; n--)
                    {
                        WaitingTexture value = pending[n];
                        if (value.Texture.IsRequestedMipmapLevelLoaded())
                        { pending.RemoveAt(n); continue; }
                        value.Attempts++;
                        pending[n] = value;
                        if (value.Attempts < 200)
                            continue;
                        probe?.Timeout(value.Probe, value.Attempts);
                        Debug.LogError("Mip 0 waiting timeout: " + value.Texture.name);
                        pending.RemoveAt(n);
                        result = false;
                    }
                    await Task.Yield();
                }
                return result;
            }
        }
        internal static float Increase()
        {
            Scope scope = LiveScope;
            if (scope == null)
                return nativeIncrease();
            if (!scope.Budget)
            {
                scope.Budget = true;
                if (_budgetOwners++ == 0)
                {
                    _budgetBefore = QualitySettings.streamingMipmapsMemoryBudget;
                    _reductionBefore = QualitySettings.streamingMipmapsMaxLevelReduction;
                    try
                    { nativeIncrease(); }
                    finally { MipBudgetAccounting.Active?.NativePolicyWritten(); _budgetApplied = QualitySettings.streamingMipmapsMemoryBudget; }
                }
            }
            return _budgetBefore;
        }
        internal static void Restore()
        { if (LiveScope == null) nativeRestore(); }
        internal static float Bias(object manager, float delta)
        {
            var camera = (StreamingController)controller.GetValue(manager);
            if (!camera)
                return 0;
            float before = camera.streamingMipmapBias;
            Scope scope = LiveScope;
            if (scope == null)
            { camera.streamingMipmapBias += delta; return before; }
            if (delta > 0 && scope.Cameras.Add(camera))
            {
                if (!Cameras.TryGetValue(camera, out BiasState state))
                {
                    state = new BiasState { Owners = 1, Before = before, Applied = before + delta };
                    Cameras.Add(camera, state);
                    camera.streamingMipmapBias = state.Applied;
                }
                else
                    state.Owners++;
            }
            return before;
        }
        private static void ReleaseCamera(StreamingController camera)
        {
            BiasState state = Cameras[camera];
            if (--state.Owners != 0)
                return;
            Cameras.Remove(camera);
            if (camera && camera.streamingMipmapBias == state.Applied)
                camera.streamingMipmapBias = state.Before;
        }
        internal static void PolicyRefreshed()
        {
            if (_budgetOwners == 0)
                return;
            _budgetBefore = QualitySettings.streamingMipmapsMemoryBudget;
            _reductionBefore = QualitySettings.streamingMipmapsMaxLevelReduction;
            try
            { nativeIncrease(); }
            finally { MipBudgetAccounting.Active?.NativePolicyWritten(); _budgetApplied = QualitySettings.streamingMipmapsMemoryBudget; }
        }
    }
}
