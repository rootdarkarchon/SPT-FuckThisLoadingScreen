using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

internal static class MipStreamingValidation
{
    private static int _itemRequests, _playerRequests;
    private static float _itemSentinel, _playerSentinel;
    private static float _baseBudget = 1000;
    private sealed class CameraFixture { public StreamingController _streamingController; }
    private sealed class ContainerFixture
    {
        internal bool Busy = true;
        internal bool FailDispose = true;
        internal int Calls;
        internal void Complete()
        {
            Calls++;
            if (FailDispose) throw new InvalidOperationException("body disposal failed");
            Busy = false; IconMipLifetime.RestorePlayer(); IconMipLifetime.ContainerReleased();
        }
        internal async Task Wait()
        { while (Busy) await Task.Yield(); Busy = true; }
    }
    private sealed class ThreadResults
    {
        public int updatedDuringFrame, requestedMipMapLevel;
        public float pixelsHBuf, pixelsVBuf, closestDist;
    }
    private static void SetPrivate(string name, object value) => AccessTools.Field(typeof(IconMipLifetime), name).SetValue(null, value);
    private static readonly Dictionary<int, ThreadResults> Results = new Dictionary<int, ThreadResults>();
    private static ManualResetEventSlim _readerEntered, _readerRelease;
    [MethodImpl(MethodImplOptions.NoInlining)] private static void Reader(bool fail)
    {
        _readerEntered.Set(); _readerRelease.Wait(5000);
        foreach (var value in Results) { if (fail) throw new InvalidOperationException("reader"); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] private static async Task<int> Render(Func<Task<int>> render) => await render();
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task<int> ThrowLaunch() => throw new InvalidOperationException("launch");

    internal static void Run(Action<bool, string> check, string project)
    {
        TestResults(check);
        var oldContext = SynchronizationContext.Current;
        var context = new PumpContext(); SynchronizationContext.SetSynchronizationContext(context);
        float oldBudget = QualitySettings.streamingMipmapsMemoryBudget;
        bool oldStreaming = QualitySettings.streamingMipmapsActive;
        int oldReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
        var harmony = new Harmony("ftls.mip.lifetime.fixture");
        GameObject cameraObject = null;
        try
        {
            SetPrivate("itemCount", AccessTools.Field(typeof(MipStreamingValidation), nameof(_itemRequests)));
            SetPrivate("playerCount", AccessTools.Field(typeof(MipStreamingValidation), nameof(_playerRequests)));
            SetPrivate("itemBudget", AccessTools.Field(typeof(MipStreamingValidation), nameof(_itemSentinel)));
            SetPrivate("playerBudget", AccessTools.Field(typeof(MipStreamingValidation), nameof(_playerSentinel)));
            SetPrivate("playerBusy", AccessTools.Field(typeof(ContainerFixture), nameof(ContainerFixture.Busy)));
            SetPrivate("playerComplete", AccessTools.DeclaredMethod(typeof(ContainerFixture), nameof(ContainerFixture.Complete)));
            SetPrivate("controller", AccessTools.Field(typeof(CameraFixture), "_streamingController"));
            SetPrivate("nativeIncrease", (Func<float>)(() => { QualitySettings.streamingMipmapsMemoryBudget = _baseBudget + 100; QualitySettings.streamingMipmapsMaxLevelReduction = 4; return _baseBudget; }));
            SetPrivate("nativeRestore", (Action)(() => { QualitySettings.streamingMipmapsMemoryBudget = 1000; QualitySettings.streamingMipmapsMaxLevelReduction = 2; }));
            QualitySettings.streamingMipmapsMemoryBudget = 1000; QualitySettings.streamingMipmapsMaxLevelReduction = 2;
            QualitySettings.streamingMipmapsActive = true;
            var prefix = new HarmonyMethod(typeof(IconMipLifetime), nameof(IconMipLifetime.Begin));
            var postfix = new HarmonyMethod(AccessTools.DeclaredMethod(typeof(IconMipLifetime), nameof(IconMipLifetime.End)).MakeGenericMethod(typeof(int)));
            var finalizer = new HarmonyMethod(typeof(IconMipLifetime), nameof(IconMipLifetime.LaunchFailed));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MipStreamingValidation), nameof(Render)), prefix: prefix, postfix: postfix, finalizer: finalizer);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MipStreamingValidation), nameof(ThrowLaunch)), prefix: prefix, postfix: postfix, finalizer: finalizer);
            Texture2D texture = CreateStreamingTexture();
            check(texture.streamingMipmaps, "Texture lifetime test uses a real imported streaming texture");
            texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel();
            int clearedRequested = texture.requestedMipmapLevel, clearedMinimum = texture.minimumMipmapLevel;
            check(clearedRequested < 0 && clearedMinimum >= texture.mipmapCount, "Enabled Unity streaming exposes distinct cleared request/minimum sentinels");
            texture.requestedMipmapLevel = 0; texture.minimumMipmapLevel = 0;
            check(texture.requestedMipmapLevel == 0 && texture.minimumMipmapLevel == 0, "Editor supports explicit streaming mip writes: requested=" + texture.requestedMipmapLevel + " minimum=" + texture.minimumMipmapLevel + " active=" + QualitySettings.streamingMipmapsActive);
            texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel();
            cameraObject = new GameObject("FTLS mip test camera");
            var camera = new CameraFixture { _streamingController = cameraObject.AddComponent<StreamingController>() };
            camera._streamingController.streamingMipmapBias = -1.5f;
            var first = new TaskCompletionSource<int>(); var second = new TaskCompletionSource<int>();
            Task<int> Item(Task<int> pending) => Render(async () =>
            {
                check(IconMipLifetime.Current.Value != null, "Native render kickoff receives an isolated ambient lease");
                Interlocked.Increment(ref _itemRequests); IconMipLifetime.Current.Value.ItemRender = true; IconMipLifetime.Current.Value.ItemCount = true;
                IconMipLifetime.Increase(); IconMipLifetime.Bias(camera, 2);
                IconMipLifetime.SetRequested(texture, 0); IconMipLifetime.SetMinimum(texture, 0);
                await Task.Yield();
                check(IconMipLifetime.Current.Value != null, "Lease follows native Unity/Mono awaits after caller scope restoration");
                int value = await pending;
                Interlocked.Decrement(ref _itemRequests); IconMipLifetime.Current.Value.ItemCount = false; IconMipLifetime.Restore(); IconMipLifetime.Bias(camera, -2);
                return value;
            });
            Task<int> a = Item(first.Task), b = Render(async () =>
            {
                Interlocked.Increment(ref _playerRequests); IconMipLifetime.Current.Value.PlayerCount = true; IconMipLifetime.Increase(); IconMipLifetime.Bias(camera, 2);
                IconMipLifetime.SetRequested(texture, 0); IconMipLifetime.SetMinimum(texture, 0);
                int value = await second.Task;
                IconMipLifetime.ClearRequested(texture); IconMipLifetime.ClearMinimum(texture);
                Interlocked.Decrement(ref _playerRequests); IconMipLifetime.Current.Value.PlayerCount = false; IconMipLifetime.Restore(); IconMipLifetime.Bias(camera, -2);
                return value;
            });
            check(IconMipLifetime.Current.Value == null, "Async kickoff restores the caller's ambient scope");
            check(_itemRequests == 1 && _playerRequests == 1 && IconMipLifetime.ActiveBudgets == 2 && IconMipLifetime.ActiveTextures == 1,
                "Item/player renders share one texture while retaining separate render ownership");
            check(QualitySettings.streamingMipmapsMemoryBudget == 1100 && camera._streamingController.streamingMipmapBias == 0.5f,
                "Overlapping creator types apply one budget boost and one camera adjustment");
            second.SetResult(22); context.Until(b);
            check(b.Result == 22 && texture.requestedMipmapLevel == 0 && texture.minimumMipmapLevel == 0 && IconMipLifetime.ActiveBudgets == 1,
                "Player cleanup cannot clear an overlapping item's texture request or memory policy: result=" + b.Result + " requested=" + texture.requestedMipmapLevel + " minimum=" + texture.minimumMipmapLevel + " owners=" + IconMipLifetime.ActiveBudgets + " textures=" + IconMipLifetime.ActiveTextures + " itemCount=" + _itemRequests);
            check(camera._streamingController.streamingMipmapBias == 0.5f, "First render completion retains camera adjustment for the remaining owner");
            first.SetResult(11); context.Until(a);
            check(a.Result == 11 && _itemRequests == 0 && _playerRequests == 0 && IconMipLifetime.ActiveTextures == 0 && IconMipLifetime.ActiveBudgets == 0,
                "Last successful render releases all counters and texture/budget ownership");
            check(texture.requestedMipmapLevel == clearedRequested && texture.minimumMipmapLevel == clearedMinimum && QualitySettings.streamingMipmapsMemoryBudget == 1000 &&
                QualitySettings.streamingMipmapsMaxLevelReduction == 2 && camera._streamingController.streamingMipmapBias == -1.5f,
                "Last owner restores automatic mip selection and exact pre-render policy/bias");
            foreach (bool cancel in new[] { false, true })
            {
                var terminal = new TaskCompletionSource<int>();
                Task<int> task = Item(terminal.Task);
                if (cancel) terminal.SetCanceled(); else terminal.SetException(new InvalidOperationException("render failed"));
                context.Until(task);
                check(cancel ? task.IsCanceled : task.IsFaulted, "Wrapped native render preserves terminal Task state");
                try { task.GetAwaiter().GetResult(); check(false, "Failure must propagate"); }
                catch (OperationCanceledException) { check(cancel, "Native cancellation remains cancellation"); }
                catch (InvalidOperationException e) { check(!cancel && e.Message == "render failed", "Original rendering failure reaches the caller"); }
                check(_itemRequests == 0 && IconMipLifetime.ActiveTextures == 0 && IconMipLifetime.ActiveBudgets == 0 &&
                    texture.requestedMipmapLevel == clearedRequested && texture.minimumMipmapLevel == clearedMinimum && camera._streamingController.streamingMipmapBias == -1.5f,
                    "Failure/cancellation balances counters and restores texture/policy/camera state");
            }
            try { ThrowLaunch(); check(false, "Synchronous launch must throw"); }
            catch (InvalidOperationException e) { check(e.Message == "launch" && IconMipLifetime.Current.Value == null, "Synchronous failure restores ambient scope without replacing the error"); }
            texture.requestedMipmapLevel = 2; texture.minimumMipmapLevel = 1;
            Task<int> previous = Item(Task.FromResult(33)); context.Until(previous);
            check(texture.requestedMipmapLevel == 2 && texture.minimumMipmapLevel == 1, "Preexisting explicit mip requests survive an icon render");
            Task<int> untouched = Render(() => { IconMipLifetime.ClearRequested(texture); IconMipLifetime.ClearMinimum(texture); return Task.FromResult(34); });
            context.Until(untouched);
            check(texture.requestedMipmapLevel == 2 && texture.minimumMipmapLevel == 1, "Native reset cannot clear a texture that this render never requested");
            var changed = new TaskCompletionSource<int>(); Task<int> changedTask = Item(changed.Task);
            texture.requestedMipmapLevel = 3; texture.minimumMipmapLevel = 2;
            QualitySettings.streamingMipmapsMemoryBudget = 1400; QualitySettings.streamingMipmapsMaxLevelReduction = 1;
            camera._streamingController.streamingMipmapBias = -0.25f;
            changed.SetResult(44); context.Until(changedTask);
            check(texture.requestedMipmapLevel == 3 && texture.minimumMipmapLevel == 2 && QualitySettings.streamingMipmapsMemoryBudget == 1400 &&
                QualitySettings.streamingMipmapsMaxLevelReduction == 1 && camera._streamingController.streamingMipmapBias == -0.25f,
                "Cleanup preserves newer external texture, budget, reduction and bias writes");
            check(IconMipLifetime.Completed >= 6 && IconMipLifetime.Failed >= 3 && IconMipLifetime.ActiveTextures == 0, "Research counters expose completed and failed cleanup with no retained textures");
            var refresh = new TaskCompletionSource<int>(); Task<int> refreshTask = Item(refresh.Task);
            _baseBudget = 1600; QualitySettings.streamingMipmapsMemoryBudget = 1600; QualitySettings.streamingMipmapsMaxLevelReduction = 3;
            IconMipLifetime.PolicyRefreshed();
            check(QualitySettings.streamingMipmapsMemoryBudget == 1700 && QualitySettings.streamingMipmapsMaxLevelReduction == 4, "Graphics refresh reapplies one icon boost over the newly selected policy");
            bool drained = false; IconMipLifetime.WhenDrained(() => drained = true);
            check(!drained, "Shutdown keeps lifetime hooks installed while a native icon Task is pending");
            refresh.SetResult(55); context.Until(refreshTask);
            check(drained && IconMipLifetime.ActiveScopes == 0 && QualitySettings.streamingMipmapsMemoryBudget == 1600 && QualitySettings.streamingMipmapsMaxLevelReduction == 3,
                "Last render restores newly selected settings and permits deferred unpatching");
            _baseBudget = 1000;
            var container = new ContainerFixture();
            IconMipLifetime.Begin(out var failedPlayer); failedPlayer.PlayerRender = true; failedPlayer.Creator = container;
            failedPlayer.PlayerCount = true; _playerRequests++; _playerSentinel = 1; IconMipLifetime.Increase();
            IconMipLifetime.TrackContainer(Task.CompletedTask, failedPlayer).GetAwaiter().GetResult();
            IconMipLifetime.Begin(out var queuedPlayer); queuedPlayer.PlayerRender = true; queuedPlayer.Creator = container;
            queuedPlayer.PlayerCount = true; _playerRequests++; IconMipLifetime.Increase();
            Task queued = IconMipLifetime.TrackContainer(container.Wait(), queuedPlayer);
            IconMipLifetime.Current.Value = null;
            IconMipLifetime.Release(failedPlayer, true);
            check(!container.Busy && container.Calls == 1 && _playerRequests == 1 && IconMipLifetime.ActiveBudgets == 1,
                "Failed body disposal releases only the failed owner's queue slot/count/policy and preserves queued ownership");
            context.Until(queued);
            check(queuedPlayer.OwnsPlayerContainer && container.Busy, "Queued native wait can acquire the container after failed-owner cleanup");
            container.FailDispose = false; IconMipLifetime.Current.Value = queuedPlayer; container.Complete();
            IconMipLifetime.Current.Value = null; IconMipLifetime.Release(queuedPlayer, false);
            check(_playerRequests == 0 && _playerSentinel == 0 && IconMipLifetime.ActiveBudgets == 0 && IconMipLifetime.ActiveScopes == 0,
                "Final queued render restores all counts and policy after the preceding render failed");
            texture.ClearRequestedMipmapLevel(); texture.ClearMinimumMipmapLevel();
        }
        finally
        {
            harmony.UnpatchSelf(); IconMipLifetime.Current.Value = null;
            if (cameraObject) UnityEngine.Object.DestroyImmediate(cameraObject);
            QualitySettings.streamingMipmapsMemoryBudget = oldBudget; QualitySettings.streamingMipmapsMaxLevelReduction = oldReduction;
            QualitySettings.streamingMipmapsActive = oldStreaming;
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    private static Texture2D CreateStreamingTexture(string path = "Assets/ftls-streaming-lifetime.png", int size = 64)
    {
        var source = new Texture2D(size, size, TextureFormat.RGBA32, true);
        try { File.WriteAllBytes(path, source.EncodeToPNG()); } finally { UnityEngine.Object.DestroyImmediate(source); }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Default; importer.mipmapEnabled = true; importer.streamingMipmaps = true;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
    private static void TestResults(Action<bool, string> check)
    {
        var harmony = new Harmony("ftls.decal.results.fixture");
        _readerEntered = new ManualResetEventSlim(); _readerRelease = new ManualResetEventSlim();
        try
        {
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MipStreamingValidation), nameof(Reader)),
                prefix: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(DecalResultSynchronization.Enter)),
                finalizer: new HarmonyMethod(typeof(DecalResultSynchronization), nameof(DecalResultSynchronization.Exit)));
            var value = new ThreadResults();
            DecalResultSynchronization.Add(Results, 1, value);
            var reader = new Thread(() => Reader(false)) { IsBackground = true }; reader.Start();
            check(_readerEntered.Wait(5000), "Reader entered its protected enumeration");
            using (var done = new ManualResetEventSlim())
            {
                var writer = new Thread(() => { DecalResultSynchronization.Add(Results, 2, new ThreadResults()); DecalResultSynchronization.Mip(value, 3); done.Set(); }) { IsBackground = true };
                writer.Start();
                check(!done.Wait(40), "Dictionary insertion and result publication cannot mutate active reader state");
                _readerRelease.Set(); check(reader.Join(5000) && writer.Join(5000), "Reader and writer both finish after short gate handoff");
            }
            check(Results.Count == 2 && value.requestedMipMapLevel == 3, "Serialized writer retains native dictionary and mip values");
            try { Reader(true); check(false, "Reader should fail"); }
            catch (InvalidOperationException e) { check(e.Message == "reader", "Reader exception propagates unchanged"); }
            bool entered = false;
            var afterError = new Thread(() => { entered = Monitor.TryEnter(DecalResultSynchronization.Gate); if (entered) Monitor.Exit(DecalResultSynchronization.Gate); });
            afterError.Start(); check(afterError.Join(5000) && entered, "Reader finalizer releases its gate after an exception");
            var errors = new List<Exception>();
            var stress = new Thread(() =>
            {
                try { for (int i = 3; i < 2003; i++) { DecalResultSynchronization.Add(Results, i, new ThreadResults()); DecalResultSynchronization.Frame(value, i); DecalResultSynchronization.H(value, i); } }
                catch (Exception e) { errors.Add(e); }
            });
            stress.Start(); for (int i = 0; i < 2000; i++) Reader(false);
            check(stress.Join(5000) && errors.Count == 0 && Results.Count == 2002, "Concurrent insertion/enumeration stress completes without dictionary invalidation or lost entries");
        }
        finally { _readerRelease.Set(); harmony.UnpatchSelf(); Results.Clear(); _readerEntered.Dispose(); _readerRelease.Dispose(); }
    }
    internal static void Installed(Action<bool, string> check, Func<string, Type> find)
    {
        GameMipStreaming settings = GameMipStreaming.Bind(find);
        check(!settings.Loaded(), "EFT mip streaming setting binds to the installed SettingsManager and reports no loaded settings outside the game");
        var harmony = new Harmony("ftls.mip.installed");
        try
        {
            Guid module = find("TextureQuadTree").Module.ModuleVersionId;
            DecalResultSynchronization.Install(harmony, find, module);
            Type resultType = find("TextureQuadTree").GetNestedType("ThreadResults");
            object nativeResult = Activator.CreateInstance(resultType);
            foreach (var pair in new[] { ("Frame", "updatedDuringFrame"), ("Mip", "requestedMipMapLevel"), ("H", "pixelsHBuf"), ("V", "pixelsVBuf"), ("Distance", "closestDist") })
            {
                FieldInfo field = AccessTools.Field(resultType, pair.Item2);
                object value = field.FieldType == typeof(int) ? (object)7 : 0.75f;
                AccessTools.DeclaredMethod(typeof(DecalResultSynchronization), pair.Item1).MakeGenericMethod(resultType).Invoke(null, new[] { nativeResult, value });
                check(Equals(field.GetValue(nativeResult), value), "Guarded setter executes on the exact installed result field: " + pair.Item2);
            }
            TestNativeRequests(check, find, false);
            IconMipLifetime.Install(harmony, find, module);
            TestNativeRequests(check, find, true);
            TestNativePolicy(check, find);
            foreach (var pair in new[] { ("ItemIconCreator", "RequestMipZero"), ("ItemIconCreator", "RenderModel"), ("IconCreatorGraphicsSettings", "Prepare"), ("IconCreatorGraphicsSettings", "ResetRequestedMip") })
            {
                var method = AccessTools.DeclaredMethod(find(pair.Item1), pair.Item2);
                Type machine = method.GetCustomAttribute<AsyncStateMachineAttribute>().StateMachineType;
                var patches = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(machine, "MoveNext"));
                check(patches == null || !patches.Owners.Contains(harmony.Id), "Icon adapters leave native compiler-generated state machines undetoured: " + pair.Item1 + "." + pair.Item2);
            }
            bool oldDiscard = Texture.streamingTextureDiscardUnusedMips;
            float oldBudget = QualitySettings.streamingMipmapsMemoryBudget;
            try
            {
                Texture.streamingTextureDiscardUnusedMips = true;
                MipResidencyPolicy.Active = MipResidencyPolicy.Install(harmony, find, module);
                check(!Texture.streamingTextureDiscardUnusedMips && QualitySettings.streamingMipmapsMemoryBudget == oldBudget, "Opt-in residency policy disables forced discard without changing the memory budget");
                Texture.streamingTextureDiscardUnusedMips = true; MipResidencyPolicy.Refreshed();
                check(!Texture.streamingTextureDiscardUnusedMips, "Graphics refresh retains the selected caching comparison policy");
                MipResidencyPolicy.Active.Stop();
                check(Texture.streamingTextureDiscardUnusedMips, "Residency policy restores native discard on cleanup");
            }
            finally { MipResidencyPolicy.Active = null; Texture.streamingTextureDiscardUnusedMips = oldDiscard; }
            check(true, "All mip patches bind the exact installed decal, item/player request and async policy IL");
            var specs = HideoutResearch.Resolve(find); HideoutResearch.Install(harmony, specs);
            check(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(find("TextureQuadTree"), "WorkThread")).Transpilers.Count == 2,
                "Result synchronization composes with installed worker lock diagnostics");
            foreach (string name in new[] { "ItemIconCreator", "EFT.PlayerIcons.PlayerIconCreator" })
            {
                var render = AccessTools.GetDeclaredMethods(find(name)).Single(m => m.Name == "RenderModel");
                check(Harmony.GetPatchInfo(render).Postfixes.Any(p => p.PatchMethod.DeclaringType == typeof(IconMipLifetime)), "Generic native render Task accepts lifetime completion hook: " + name);
            }
            try { DecalResultSynchronization.Install(harmony, find, Guid.Empty); check(false, "Wrong decal module must fail"); }
            catch (NotSupportedException) { check(true, "Wrong decal module refuses synchronization"); }
            try { IconMipLifetime.Install(harmony, find, Guid.Empty); check(false, "Wrong icon module must fail"); }
            catch (NotSupportedException) { check(true, "Wrong icon module refuses lifetime patching"); }
        }
        finally { harmony.UnpatchSelf(); }
    }
    private static void TestNativePolicy(Action<bool, string> check, Func<string, Type> find)
    {
        Type graphics = find("IconCreatorGraphicsSettings"), item = find("ItemIconCreator"), policy = find("EFT.Settings.Graphics.GraphicsSettingsController");
        FieldInfo count = AccessTools.Field(graphics, "_requests"), sentinel = AccessTools.Field(graphics, "_mipBudget"),
            itemCountField = AccessTools.Field(item, "requests"), itemBudgetField = AccessTools.Field(item, "mipBudget"),
            baseBudgetField = AccessTools.Field(policy, "MipStreamingMemoryBudget");
        int beforeCount = (int)count.GetValue(null), beforeItemCount = (int)itemCountField.GetValue(null);
        float beforeSentinel = (float)sentinel.GetValue(null), beforeItemSentinel = (float)itemBudgetField.GetValue(null), beforeBase = (float)baseBudgetField.GetValue(null);
        float beforeBudget = QualitySettings.streamingMipmapsMemoryBudget; int beforeReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
        object instance = RuntimeHelpers.GetUninitializedObject(graphics);
        try
        {
            count.SetValue(null, 0); baseBudgetField.SetValue(null, 0f); QualitySettings.streamingMipmapsMemoryBudget = 0;
            var prepare = AccessTools.DeclaredMethod(graphics, "Prepare"); var restore = AccessTools.DeclaredMethod(graphics, "Restore");
            IconMipLifetime.Begin(out var first); first.PlayerRender = true;
            ((Task)prepare.Invoke(instance, null)).GetAwaiter().GetResult();
            IconMipLifetime.Begin(out var second); second.PlayerRender = true;
            ((Task)prepare.Invoke(instance, null)).GetAwaiter().GetResult();
            check((int)count.GetValue(null) == 2 && (float)sentinel.GetValue(null) != 0 && QualitySettings.streamingMipmapsMemoryBudget == 100,
                "Exact native Prepare entry handles overlapping owners even when the base budget is zero");
            restore.Invoke(instance, null); IconMipLifetime.Current.Value = first; IconMipLifetime.Release(second, false);
            check((int)count.GetValue(null) == 1 && QualitySettings.streamingMipmapsMemoryBudget == 100, "Exact native Restore entry preserves the other player's policy");
            restore.Invoke(instance, null); IconMipLifetime.Current.Value = null; IconMipLifetime.Release(first, false);
            check((int)count.GetValue(null) == 0 && (float)sentinel.GetValue(null) == 0 && QualitySettings.streamingMipmapsMemoryBudget == 0,
                "Last native player restore balances count/sentinel and restores the prior budget");
            IconMipLifetime.Begin(out var failure); failure.PlayerRender = true;
            ((Task)prepare.Invoke(instance, null)).GetAwaiter().GetResult();
            IconMipLifetime.Current.Value = null; IconMipLifetime.Release(failure, true);
            check((int)count.GetValue(null) == 0 && (float)sentinel.GetValue(null) == 0, "Failure before native player Restore repairs its exact count and sentinel fields");
            IconMipLifetime.Begin(out var itemFailure); itemFailure.ItemRender = true;
            itemCountField.SetValue(null, 2); itemBudgetField.SetValue(null, 0f);
            AccessTools.DeclaredMethod(policy, "IncreaseMemoryBudgetForIcon").Invoke(null, null);
            IconMipLifetime.Current.Value = null; IconMipLifetime.Release(itemFailure, true);
            check((int)itemCountField.GetValue(null) == 1 && (float)itemBudgetField.GetValue(null) != 0,
                "Failed first item owner repairs its count and unblocks another native sentinel waiter");
            var model = new GameObject("FTLS pool marker fixture");
            try
            {
                IconMipLifetime.Begin(out var afterDecrement); afterDecrement.ItemRender = true; afterDecrement.ItemCount = true; afterDecrement.Model = model;
                IconMipLifetime.ModelReturned(model); IconMipLifetime.Current.Value = null; IconMipLifetime.Release(afterDecrement, true);
                check((int)itemCountField.GetValue(null) == 1, "Fault after exact-model pool return does not decrement another native item's count");
            }
            finally { UnityEngine.Object.DestroyImmediate(model); }
        }
        finally
        {
            IconMipLifetime.Current.Value = null;
            count.SetValue(null, beforeCount); sentinel.SetValue(null, beforeSentinel); itemCountField.SetValue(null, beforeItemCount); itemBudgetField.SetValue(null, beforeItemSentinel);
            baseBudgetField.SetValue(null, beforeBase); QualitySettings.streamingMipmapsMemoryBudget = beforeBudget; QualitySettings.streamingMipmapsMaxLevelReduction = beforeReduction;
        }
    }
    private static void TestNativeRequests(Action<bool, string> check, Func<string, Type> find, bool patched)
    {
        SynchronizationContext oldContext = SynchronizationContext.Current;
        var context = new PumpContext(); SynchronizationContext.SetSynchronizationContext(context);
        bool oldStreaming = QualitySettings.streamingMipmapsActive;
        var model = new GameObject("FTLS native request fixture");
        Material material = null;
        Texture2D first = null, second = null;
        string firstName = null, secondName = null;
        try
        {
            QualitySettings.streamingMipmapsActive = true;
            first = CreateStreamingTexture(); second = CreateStreamingTexture("Assets/ftls-streaming-lifetime-second.png");
            var large = CreateStreamingTexture("Assets/ftls-streaming-lifetime-large.png", 1024);
            large.ClearMinimumMipmapLevel();
            check(large.mipmapCount > IconMipLifetime.ClearedMinimumMip && large.minimumMipmapLevel == IconMipLifetime.ClearedMinimumMip,
                "Cleared native minimum-mip sentinel is independent of a large texture's mip count");
            if (patched)
            {
                IconMipLifetime.Begin(out var largeScope);
                IconMipLifetime.SetRequested(large, 0); IconMipLifetime.SetMinimum(large, 0);
                Task<bool> completed = Task.FromResult(true); IconMipLifetime.End(ref completed, largeScope);
                check(completed.Result && large.requestedMipmapLevel < 0 && large.minimumMipmapLevel == IconMipLifetime.ClearedMinimumMip,
                    "Large streaming texture cleanup restores automatic requested and minimum mip state");
            }
            firstName = first.name; secondName = second.name;
            first.name = second.name = "same-native-texture-name";
            first.ClearRequestedMipmapLevel(); first.ClearMinimumMipmapLevel();
            second.ClearRequestedMipmapLevel(); second.ClearMinimumMipmapLevel();
            int firstMinimum = first.minimumMipmapLevel, secondMinimum = second.minimumMipmapLevel;
            string shaderPath = "Assets/ftls-mip-request-test.shader";
            File.WriteAllText(shaderPath, "Shader \"Hidden/FTLS/MipRequestTest\" { Properties { _First(\"First\", 2D) = \"white\" {} _Second(\"Second\", 2D) = \"white\" {} } SubShader { Pass { Color (1,1,1,1) } } }");
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            check(shader != null, "Native request fixture imports a shader with two independent texture properties");
            material = new Material(shader); material.SetTexture("_First", first); material.SetTexture("_Second", second);
            model.AddComponent<MeshRenderer>().sharedMaterial = material;
            foreach (bool player in new[] { false, true })
            {
                Type creatorType = find(player ? "IconCreatorGraphicsSettings" : "ItemIconCreator");
                object creator = RuntimeHelpers.GetUninitializedObject(creatorType);
                var method = AccessTools.DeclaredMethod(creatorType, player ? "RequestMipZeroFast" : "RequestMipZero");
                IconMipLifetime.Begin(out IconMipLifetime.Scope scope);
                Task<bool> requested;
                try { requested = (Task<bool>)method.Invoke(creator, new object[] { model }); }
                catch (Exception error) { IconMipLifetime.LaunchFailed(error, scope); throw; }
                if (patched)
                    check(IconMipLifetime.ActiveTextures == 2 && first.requestedMipmapLevel == 0 && second.requestedMipmapLevel == 0 &&
                        first.minimumMipmapLevel == 0 && second.minimumMipmapLevel == 0,
                        "Exact native " + (player ? "player" : "item") + " request path pins both distinct textures even when asset names match");
                else
                    check((first.requestedMipmapLevel == 0 ? 1 : 0) + (second.requestedMipmapLevel == 0 ? 1 : 0) == 1,
                        "Unpatched native " + (player ? "player" : "item") + " code reproduces one missing mip request for duplicate texture names");
                IconMipLifetime.End(ref requested, scope);
                context.Until(requested); requested.GetAwaiter().GetResult();
                if (patched)
                {
                    check(first.requestedMipmapLevel < 0 && second.requestedMipmapLevel < 0 &&
                        first.minimumMipmapLevel == firstMinimum && second.minimumMipmapLevel == secondMinimum && IconMipLifetime.ActiveTextures == 0,
                        "Exact native async texture requests release both owners and restore automatic selection");
                    check(scope.Textures.Count == 0 && scope.Cameras.Count == 0 && scope.Model == null && scope.Creator == null,
                        "Terminal icon scopes drop texture, camera and model references even if their Task is retained");
                    IconMipLifetime.Current.Value = scope;
                    float ignored = 0;
                    check(IconMipLifetime.IncreasePolicy(ref ignored) && IconMipLifetime.ActiveBudgets == 0,
                        "Escaped completed contexts cannot reacquire an unreleaseable streaming policy lease");
                    IconMipLifetime.Current.Value = null;
                }
                check(first.name == "same-native-texture-name" && second.name == first.name, "Identity deduplication preserves actual texture names");
                first.ClearRequestedMipmapLevel(); first.ClearMinimumMipmapLevel();
                second.ClearRequestedMipmapLevel(); second.ClearMinimumMipmapLevel();
            }
        }
        finally
        {
            if (first) { first.name = firstName; first.ClearRequestedMipmapLevel(); first.ClearMinimumMipmapLevel(); }
            if (second) { second.name = secondName; second.ClearRequestedMipmapLevel(); second.ClearMinimumMipmapLevel(); }
            if (material) UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(model);
            QualitySettings.streamingMipmapsActive = oldStreaming;
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    private sealed class PumpContext : SynchronizationContext
    {
        private readonly Queue<Action> _work = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state) { lock (_work) _work.Enqueue(() => callback(state)); }
        internal void Until(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                Action action = null; lock (_work) if (_work.Count > 0) action = _work.Dequeue();
                if (action != null) action(); else Thread.Sleep(1);
                if (DateTime.UtcNow > until) throw new TimeoutException("Icon Task fixture did not finish");
            }
        }
    }
}
