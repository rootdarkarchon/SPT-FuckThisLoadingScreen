using System;
using System.Collections;
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
    // Mip streaming keeps renders in flight for many frames, so a model or texture can be
    // destroyed mid-render. Destroyed textures count as loaded; a render that ends without a
    // sprite drops its empty cache entry, and a failed capture releases the native capture flag.
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
            internal readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();
            internal int Stage = StageAssets;
            internal long StageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            /// <summary>Stopwatch ticks spent in each stage of <see cref="StageNames"/>.</summary>
            internal readonly long[] StageTicks = new long[StageNames.Length];
            internal int MipStartFrame, MipFrames, MipTextures, LargestWidth, LargestHeight;
            internal bool MipsLoaded, MipRequested;
            internal bool Reported, NoSprite;
            /// <summary>The item being rendered (item renders only); used for watchdog descriptions.</summary>
            internal object Item;
            /// <summary>The sprite-less ItemIcon FillIconWithNewSpriteAsync cached under <see cref="IconHash"/> before calling RenderModel, or null.</summary>
            internal object FillIcon;
            internal int IconHash;
        }
        private sealed class TextureState
        {
            internal int Owners, Requested, Minimum, AppliedRequested, AppliedMinimum;
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
        private static readonly List<Scope> Active = new List<Scope>();
        private static Action _drained;
        private static float _budgetBefore, _budgetApplied;
        private static int _reductionBefore;
        private static FieldInfo _iconCache, _iconCreating, _resultSprite, _prefabPath;
        private static MethodInfo _itemHash;
        private static PropertyInfo _iconSprite, _itemPrefab;
        private static bool _trackWarned;
        private static long _nextWatch;
        private static readonly string[] StageNames = { "bundles/prefab/budget wait", "mip wait", "camera/capture queue", "capture", "finish" };
        private const int StageAssets = 0, StageMips = 1, StageQueue = 2, StageCapture = 3, StageFinish = 4;
        private static readonly Timing Batch = new Timing();
        private const int StalledSeconds = 10, TimingSeconds = 5;
        internal static Action<string> Warning;
        /// <summary>Debug output (watchdog, cache eviction, capture-flag release); null when debug logging is not wired.</summary>
        internal static Action<string> DebugLog;
        /// <summary>Destination of the item render timing summary; null when not wired.</summary>
        internal static Action<string> TimingLog;
        /// <summary>Whether timings are collected (Debug / Logging); null disables collection.</summary>
        internal static Func<bool> DebugEnabled;
        internal static long Completed, ReleasedTextures, Failed, EvictedIcons, CaptureFlagsCleared;
        internal static int ActiveTextures => Textures.Count;
        internal static int ActiveBudgets => _budgetOwners;
        internal static int ActiveScopes => Active.Count;

        internal static void Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type item = find("ItemIconCreator"), player = find("EFT.PlayerIcons.PlayerIconCreator"), graphics = find("IconCreatorGraphicsSettings"),
                policy = find("EFT.Settings.Graphics.GraphicsSettingsController"), camera = find("EFT.CameraControl.CameraManager"),
                icon = find("ItemIcon"), hashes = find("IconsHash"), itemType = find("EFT.InventoryLogic.Item");
            if (new[] { item, player, graphics, policy, camera, icon, hashes, itemType }.Any(t => t.Module.ModuleVersionId != module))
                throw new NotSupportedException("Icon mip lifetimes require the supported EFT module");
            // ItemIconCreator : IconCreatorBase<Item, ItemIcon>; the cache, capture flag and capture method live on that closed base.
            Type creatorBase = item.BaseType;
            _iconCache = RequireField(creatorBase, "_memoryCacheIndex", typeof(Dictionary<,>).MakeGenericType(typeof(int), icon), false);
            _iconCreating = RequireField(creatorBase, "_isIconCreating", typeof(bool), false);
            _itemHash = AccessTools.DeclaredMethod(hashes, "GetItemHash", new[] { itemType });
            if (_itemHash?.ReturnType != typeof(int) || !_itemHash.IsStatic)
                throw new MissingMethodException(hashes.FullName, "GetItemHash");
            _iconSprite = AccessTools.DeclaredProperty(icon, "Sprite");
            if (_iconSprite?.PropertyType != typeof(Sprite))
                throw new MissingMemberException(icon.FullName, "Sprite");
            _itemPrefab = AccessTools.Property(itemType, "Prefab");
            _prefabPath = _itemPrefab == null ? null : AccessTools.Field(_itemPrefab.PropertyType, "path");
            if (_prefabPath?.FieldType != typeof(string))
                throw new MissingMemberException(itemType.FullName, "Prefab.path");
            MethodInfo capture = AccessTools.DeclaredMethod(creatorBase, "CaptureSpriteOfModel");
            if (capture?.ReturnType != typeof(Sprite))
                throw new MissingMethodException(creatorBase.FullName, "CaptureSpriteOfModel");
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
            _resultSprite = AccessTools.Field(itemRender.ReturnType.GetGenericArguments()[0], "sprite");
            if (_resultSprite?.FieldType != typeof(Sprite))
                throw new MissingFieldException(itemRender.ReturnType.GetGenericArguments()[0].FullName, "sprite");
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
            harmony.Patch(capture, prefix: Hook(nameof(CaptureBegin)), postfix: Hook(nameof(CaptureEnded)), finalizer: Hook(nameof(CaptureFailed)));
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
        { __state = new Scope { Previous = Current.Value }; Active.Add(__state); Current.Value = __state; }
        internal static void RenderBegin(MethodBase __originalMethod, object __instance, object __0, out Scope __state)
        {
            Begin(out __state);
            __state.ItemRender = __originalMethod.DeclaringType.Name == "ItemIconCreator";
            __state.PlayerRender = !__state.ItemRender;
            __state.Creator = __instance;
            if (__state.ItemRender)
            {
                __state.Item = __0;
                TrackFillIcon(__state);
            }
            __state.Probe = IconMipProbe.Start(__state);
        }
        /// <summary>
        /// FillIconWithNewSpriteAsync caches its new, sprite-less ItemIcon synchronously right before it calls RenderModel.
        /// Remember that entry so a render that ends without a sprite can drop it instead of leaving a permanent loading icon.
        /// </summary>
        private static void TrackFillIcon(Scope scope)
        {
            if (_iconCache == null || scope.Item == null)
                return;
            try
            {
                int hash = (int)_itemHash.Invoke(null, new[] { scope.Item });
                object icon = ((IDictionary)_iconCache.GetValue(scope.Creator))?[hash];
                if (icon == null || _iconSprite.GetValue(icon) is Sprite sprite && sprite)
                    return;
                scope.FillIcon = icon;
                scope.IconHash = hash;
            }
            catch (Exception error)
            {
                // Prefix boundary: the render must proceed; only empty-icon recovery is lost for it.
                if (_trackWarned)
                    return;
                _trackWarned = true;
                Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                Warning?.Invoke("Icon cache tracking failed; empty icons from failed renders are not retried: " + cause.GetType().Name + ": " + cause.Message);
            }
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
            {
                T result = await task;
                failed = false;
                scope.NoSprite = MissingSprite(result);
                return result;
            }
            finally { Release(scope, failed); }
        }
        /// <summary>True when <paramref name="result"/> is an item RenderModelResult without a live sprite.</summary>
        private static bool MissingSprite<T>(T result) =>
            _resultSprite != null && typeof(T) == _resultSprite.DeclaringType && !(_resultSprite.GetValue(result) is Sprite sprite && sprite);
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
            if (scope.FillIcon != null && (failed || scope.NoSprite))
                Safely(() => EvictIcon(scope));
            CloseStage(scope);
            if (scope.Reported)
                Safely(() => DebugLog?.Invoke("Icon render finished" + (failed ? " (failed)" : scope.NoSprite ? " (no sprite)" : "") + ": " + Describe(scope)));
            if (scope.ItemRender && TimingLog != null && DebugEnabled?.Invoke() == true)
                Safely(() => Batch.Add(scope, failed));
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
            scope.Item = null;
            scope.FillIcon = null;
            scope.Disposing = false;
            Active.Remove(scope);
            if (Active.Count == 0 && _drained != null)
            { Action callback = _drained; _drained = null; Safely(callback); }
        }
        internal static void WhenDrained(Action callback)
        { if (Active.Count == 0) callback(); else _drained += callback; }
        /// <summary>
        /// Drops the sprite-less cache entry of a render that failed or produced no sprite, so the next ItemView refresh
        /// renders the icon again instead of showing EFT's loading icon for the rest of the session.
        /// </summary>
        private static void EvictIcon(Scope scope)
        {
            var cache = (IDictionary)_iconCache.GetValue(scope.Creator);
            // A newer render or a cache-file load may own this hash by now; only drop the entry this render left empty.
            if (!ReferenceEquals(cache[scope.IconHash], scope.FillIcon) || _iconSprite.GetValue(scope.FillIcon) is Sprite sprite && sprite)
                return;
            cache.Remove(scope.IconHash);
            EvictedIcons++;
            DebugLog?.Invoke("Icon render ended without a sprite; removed its empty cache entry so the next refresh renders it again: " + Describe(scope));
        }
        internal static void CaptureBegin(object __instance)
        {
            Scope scope = LiveScope;
            if (scope != null && ReferenceEquals(scope.Creator, __instance))
                SetStage(scope, StageCapture);
        }
        internal static void CaptureEnded(object __instance)
        {
            Scope scope = LiveScope;
            if (scope != null && ReferenceEquals(scope.Creator, __instance))
                SetStage(scope, StageFinish);
        }
        private static void SetStage(Scope scope, int stage)
        {
            if (scope.Finished)
                return;
            CloseStage(scope);
            scope.Stage = stage;
        }
        /// <summary>Adds the time since the last stage change to the current stage.</summary>
        private static void CloseStage(Scope scope)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            scope.StageTicks[scope.Stage] += now - scope.StageStarted;
            scope.StageStarted = now;
        }
        /// <summary>
        /// FillIconWithNewSpriteAsync's sprite factory sets _isIconCreating before CaptureSpriteOfModel and clears it only after a
        /// successful capture. A throwing capture would leave it set and stall every later icon render, so release it here.
        /// The exception still propagates.
        /// </summary>
        internal static void CaptureFailed(Exception __exception, object __instance)
        {
            if (__exception == null)
                return;
            Scope scope = LiveScope;
            // Only FillIconWithNewSpriteAsync renders (tracked FillIcon) hold the flag; GetItemSpriteAsync captures do not.
            if (scope?.FillIcon == null || !ReferenceEquals(scope.Creator, __instance) || !(bool)_iconCreating.GetValue(__instance))
                return;
            _iconCreating.SetValue(__instance, false);
            CaptureFlagsCleared++;
            DebugLog?.Invoke("Icon capture threw " + __exception.GetType().Name + "; released the native capture flag so other icons continue: " + Describe(scope));
        }
        /// <summary>
        /// Debug pump, once per second: reports each icon render still running after <see cref="StalledSeconds"/> s, once, and writes the
        /// item render timing summary once the current batch of renders has drained (or every <see cref="TimingSeconds"/> s while renders continue).
        /// </summary>
        internal static void Pump()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextWatch)
                return;
            _nextWatch = now + System.Diagnostics.Stopwatch.Frequency;
            if (DebugLog != null)
                for (int i = 0; i < Active.Count; i++)
                {
                    Scope scope = Active[i];
                    if (scope.Reported || now - scope.Started < StalledSeconds * System.Diagnostics.Stopwatch.Frequency)
                        continue;
                    scope.Reported = true;
                    DebugLog("Icon render still running: " + Describe(scope));
                }
            if (TimingLog != null && Batch.Count > 0 && (Active.Count == 0 || now - Batch.Opened >= TimingSeconds * System.Diagnostics.Stopwatch.Frequency))
            {
                string summary = Batch.Summary();
                Batch.Reset();
                TimingLog(summary);
            }
        }
        private static string Describe(Scope scope)
        {
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - scope.Started) / (double)System.Diagnostics.Stopwatch.Frequency;
            string text = (scope.ItemRender ? "item" : "player") + " render, " + seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s, stage=" + StageNames[scope.Stage];
            if (scope.ItemRender)
            {
                object key = scope.Item == null ? null : _itemPrefab?.GetValue(scope.Item);
                text += ", prefab=" + (key == null ? "?" : _prefabPath.GetValue(key)) +
                    ", item requests=" + itemCount?.GetValue(null) + ", item mip budget=" + itemBudget?.GetValue(null) +
                    ", capture busy=" + (scope.Creator == null ? "?" : _iconCreating?.GetValue(scope.Creator));
            }
            return text + ", active renders=" + Active.Count + ", budget owners=" + _budgetOwners;
        }
        /// <summary>
        /// Item render timings since the last summary: per-stage mean and maximum, mip-wait frames and textures, and the slowest
        /// render's breakdown. Main thread only.
        /// </summary>
        private sealed class Timing
        {
            internal int Count, Failed, NoSprite, MipsNotLoaded, MipRequests;
            internal long Opened, TotalTicks, MaxTicks, MipFrames, MipTextures;
            internal int MaxMipFrames;
            internal readonly long[] StageTicks = new long[StageNames.Length], StageMax = new long[StageNames.Length];
            internal string Slowest;
            internal void Add(Scope scope, bool failed)
            {
                long total = System.Diagnostics.Stopwatch.GetTimestamp() - scope.Started;
                if (Count++ == 0)
                    Opened = scope.Started;
                if (failed)
                    Failed++;
                else if (scope.NoSprite)
                    NoSprite++;
                TotalTicks += total;
                for (int i = 0; i < StageTicks.Length; i++)
                {
                    StageTicks[i] += scope.StageTicks[i];
                    StageMax[i] = Math.Max(StageMax[i], scope.StageTicks[i]);
                }
                if (scope.MipRequested)
                {
                    MipRequests++;
                    MipFrames += scope.MipFrames;
                    MaxMipFrames = Math.Max(MaxMipFrames, scope.MipFrames);
                    MipTextures += scope.MipTextures;
                    if (!scope.MipsLoaded)
                        MipsNotLoaded++;
                }
                if (total <= MaxTicks)
                    return;
                MaxTicks = total;
                object key = scope.Item == null ? null : _itemPrefab?.GetValue(scope.Item);
                var text = new System.Text.StringBuilder();
                text.Append(Ms(total)).Append(" ms (");
                for (int i = 0; i < StageNames.Length; i++)
                    text.Append(i == 0 ? "" : ", ").Append(StageNames[i]).Append(' ').Append(Ms(scope.StageTicks[i]));
                text.Append("), mip wait ").Append(scope.MipFrames).Append(" frames for ").Append(scope.MipTextures).Append(" textures, largest ")
                    .Append(scope.LargestWidth).Append('x').Append(scope.LargestHeight).Append(", prefab=").Append(key == null ? "?" : _prefabPath.GetValue(key));
                Slowest = text.ToString();
            }
            internal string Summary()
            {
                var text = new System.Text.StringBuilder("Icon render timing: ");
                text.Append(Count).Append(" item renders");
                if (Failed > 0 || NoSprite > 0)
                    text.Append(" (").Append(Failed).Append(" failed, ").Append(NoSprite).Append(" without sprite)");
                text.Append("; total mean ").Append(Ms(TotalTicks / Count)).Append(" ms, max ").Append(Ms(MaxTicks)).Append(" ms; stage mean/max ms:");
                for (int i = 0; i < StageNames.Length; i++)
                    text.Append(i == 0 ? " " : ", ").Append(StageNames[i]).Append(' ').Append(Ms(StageTicks[i] / Count)).Append('/').Append(Ms(StageMax[i]));
                if (MipRequests > 0)
                    text.Append("; mip wait frames mean ").Append((MipFrames / (double)MipRequests).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(", max ").Append(MaxMipFrames).Append(", textures mean ").Append((MipTextures / (double)MipRequests).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                        .Append(", mips not loaded ").Append(MipsNotLoaded);
                text.Append("; frame ").Append((Time.smoothDeltaTime * 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" ms; upload slice ").Append(QualitySettings.asyncUploadTimeSlice).Append(" ms, buffer ").Append(QualitySettings.asyncUploadBufferSize)
                    .Append(" MB, persistent ").Append(QualitySettings.asyncUploadPersistentBuffer)
                    .Append("; streaming budget ").Append(QualitySettings.streamingMipmapsMemoryBudget.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" MB, max reduction ").Append(QualitySettings.streamingMipmapsMaxLevelReduction).Append(", file IO requests ").Append(QualitySettings.streamingMipmapsMaxFileIORequests)
                    .Append("; slowest: ").Append(Slowest);
                return text.ToString();
            }
            internal void Reset()
            {
                Count = Failed = NoSprite = MipsNotLoaded = MipRequests = MaxMipFrames = 0;
                Opened = TotalTicks = MaxTicks = MipFrames = MipTextures = 0;
                Array.Clear(StageTicks, 0, StageTicks.Length);
                Array.Clear(StageMax, 0, StageMax.Length);
                Slowest = null;
            }
            private static string Ms(long ticks) => (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        }
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
            if (state != null)
            {
                if (!state.HasRequested)
                { state.Requested = texture.requestedMipmapLevel; state.HasRequested = true; }
                state.AppliedRequested = mip;
            }
            texture.requestedMipmapLevel = mip;
        }
        internal static void SetMinimum(Texture2D texture, int mip)
        {
            TextureState state = Own(texture);
            if (state != null)
            {
                if (!state.HasMinimum)
                { state.Minimum = texture.minimumMipmapLevel; state.HasMinimum = true; }
                state.AppliedMinimum = mip;
            }
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
            // Restore only values that still hold this render's request; a newer external write wins.
            if (state.HasRequested && texture.requestedMipmapLevel == state.AppliedRequested)
            { if (state.Requested < 0) texture.ClearRequestedMipmapLevel(); else texture.requestedMipmapLevel = state.Requested; }
            if (state.HasMinimum && texture.minimumMipmapLevel == state.AppliedMinimum)
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
            SetStage(scope, StageMips);
            scope.MipStartFrame = Time.frameCount;
            scope.MipRequested = true;
            OwnPolicyAndCamera();
            bool fast = __originalMethod.Name == "RequestMipZeroFast";
            if (scope.Probe == null)
                scope.Probe = IconMipProbe.Start(scope);
            var probe = scope.Probe?.BeginRequest(__0, player ? (fast ? "player-fast" : "player-slow") : "item");
            __result = RequestMips(__0, player, fast, probe, scope);
            if (probe != null)
                __result = probe.Observe(__result);
            // Synchronous continuation: no extra frame before the native render resumes.
            __result.ContinueWith(RequestFinished, scope, TaskContinuationOptions.ExecuteSynchronously);
            return false;
        }
        private static void RequestFinished(Task<bool> request, object state)
        {
            var scope = (Scope)state;
            scope.MipFrames = Time.frameCount - scope.MipStartFrame;
            scope.MipsLoaded = request.Status == TaskStatus.RanToCompletion && request.Result;
            SetStage(scope, StageQueue);
        }
        /// <summary>
        /// Mip streaming keeps renders waiting here for many frames, long enough for a texture's bundle or the model to be torn down.
        /// A destroyed texture throws from IsRequestedMipmapLevelLoaded and would fault the whole render; count it as loaded instead.
        /// </summary>
        private static bool Loaded(Texture2D texture) => !texture || texture.IsRequestedMipmapLevelLoaded();
        /// <summary>
        /// The highest mip the texture can hold under the current texture quality. EFT requests mip 0, but with a mipmap limit above 0
        /// (Apply Texture Quality With Streaming at Medium/Low) mip 0 never becomes resident: every streamed texture would wait the full
        /// attempt limit, and EFT would then skip saving the icon. Request the top available mip instead; at limit 0 this is mip 0.
        /// </summary>
        internal static int TopMip(Texture2D texture) => Math.Min(Math.Max(0, texture.activeMipmapLimit), Math.Max(0, texture.mipmapCount - 1));
        private struct WaitingTexture
        {
            internal Texture2D Texture;
            internal int Attempts;
            internal bool Logged;
            internal IconMipProbe.TextureRecord Probe;
        }
        private static async Task<bool> RequestMips(GameObject model, bool player, bool fast, IconMipProbe.RequestProbe probe, Scope scope)
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
                            int top = TopMip(texture);
                            SetRequested(texture, top);
                            SetMinimum(texture, top);
                            pending.Add(new WaitingTexture { Texture = texture, Probe = textureProbe });
                            probe?.Requested(textureProbe);
                            if (texture.width * texture.height > scope.LargestWidth * scope.LargestHeight)
                            { scope.LargestWidth = texture.width; scope.LargestHeight = texture.height; }
                        }
                    }
                }
                scope.MipTextures = pending.Count;
                if (player && fast)
                {
                    for (int attempt = 0; attempt < 50; attempt++)
                    {
                        await Task.Yield();
                        bool loaded = true;
                        for (int n = 0; n < pending.Count; n++)
                            if (!Loaded(pending[n].Texture))
                            { loaded = false; break; }
                        if (loaded)
                            return true;
                    }
                    if (probe != null)
                        for (int n = 0; n < pending.Count; n++)
                            if (!Loaded(pending[n].Texture))
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
                            if (Loaded(value.Texture))
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
                        if (Loaded(value.Texture))
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
