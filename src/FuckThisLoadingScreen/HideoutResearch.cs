using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace FuckThisLoadingScreen
{
    internal sealed class HideoutResearchHook
    {
        internal MethodInfo Method;
        internal string Stage;
        internal bool Start, Entry, HideoutGameOnly, Scene;
    }

    internal sealed class HideoutResearch
    {
        internal static volatile HideoutResearch Active;
        internal readonly HideoutDecalState Decals;
        internal readonly DecalMipProbe DecalMips;
        private readonly Dictionary<MethodBase, HideoutResearchHook> hooks;
        private readonly List<SceneWatch> scenes = new List<SceneWatch>();
        private readonly string output, metadata;
        private readonly Action<string> log;
        private readonly FieldInfo cameraInstance, cameraController, itemRequests, itemBudget, playerRequests, playerBudget, playerTextures;
        private readonly FieldInfo inRaid;
        internal bool InRaid => (bool)inRaid.GetValue(null);
        internal float? CameraBias
        {
            get
            {
                object camera = cameraInstance.GetValue(null);
                var streaming = camera == null ? null : cameraController.GetValue(camera) as StreamingController;
                return streaming == null ? (float?)null : streaming.streamingMipmapBias;
            }
        }
        private readonly int mainThread = Environment.CurrentManagedThreadId;
        private volatile HideoutResearchCapture capture;
        private double lastPump, nextSample, maxGap;
        private int captures;
        private bool sampleFailed;
        private sealed class SceneWatch { internal AsyncOperation Operation; internal HideoutResearchSpan Span; internal float Progress = -1; }

        internal const string SampleHeader = "elapsed_ms,unity_frame,window_max_main_gap_ms,mip_enabled,budget_mb,discard_unused,max_reduction,add_all_cameras,max_file_io,async_upload_ms,async_buffer_mb,current_texture_bytes,desired_texture_bytes,target_texture_bytes,total_texture_bytes,nonstreaming_texture_bytes,pending_loads,loading_textures,upload_count,streaming_textures,managed_bytes,target_fps,vsync,background_priority,camera_mip_bias,item_icon_requests,item_icon_budget_mb,player_icon_requests,player_icon_budget_mb,player_icon_tracked_textures,streaming_manager,icon_mip_owned_textures,icon_policy_owners,icon_renders_completed,icon_renders_failed,icon_textures_released," + MipBudgetAccounting.Header;

        internal HideoutResearch(string output, string options, Func<string, Type> find, Action<string> log, List<HideoutResearchHook> specs)
        {
            this.output = output; this.log = log; hooks = specs.ToDictionary(s => (MethodBase)s.Method);
            Decals = HideoutDecalProbes.Bind(find, () => capture);
            DecalMips = new DecalMipProbe(find, DecalCapture, () => InRaid);
            Type camera = find("EFT.CameraControl.CameraManager"), item = find("ItemIconCreator"), player = find("IconCreatorGraphicsSettings");
            cameraInstance = Field(camera, "instance"); cameraController = Field(camera, "_streamingController");
            inRaid = Field(find("EFT.InGameStatus"), "InRaid");
            if (inRaid.FieldType != typeof(bool) || !inRaid.IsStatic) throw new MissingFieldException("InGameStatus.InRaid ABI");
            itemRequests = Field(item, "requests"); itemBudget = Field(item, "mipBudget");
            playerRequests = Field(player, "_requests"); playerBudget = Field(player, "_mipBudget"); playerTextures = Field(player, "_textures2d");
            metadata = "FTLS hideout research schema 6\nUTC armed: " + DateTime.UtcNow.ToString("O") +
                "\nFTLS: " + typeof(HideoutResearch).Assembly.GetName().Version + " | module MVID: " + typeof(HideoutResearch).Module.ModuleVersionId +
                "\nUnity: " + Application.unityVersion + "\nEFT MVID: " + find("EFT.TarkovApplication").Module.ModuleVersionId +
                "\nFTLS options: " + options + "\n" +
                "Observation only. Inclusive overlapping spans are not additive CPU costs. launch-return measures synchronous kickoff.\n" +
                "Main-thread snapshots every 500ms; heartbeat every 1s on a separate writer thread. main_silence_ms is time since the last FTLS Update/prefix, not proof of a specific native wait.\n" +
                "Counters are Unity texture statistics, not total GPU VRAM. upload_count is cumulative; compare deltas. Blank camera bias means no existing controller.\n" +
                "streaming_manager: native/replaced/absent from current player loop. Scene progress is Unity's raw value (0.9 is not 90% wall time).\n" +
                "icon_mip_* and icon_policy_owners describe the separate lifetime fix. Render/texture cleanup counters are cumulative per process; compare deltas. Zero owners after icons finish is expected.\n" +
                "Budget accounting (when armed) adds ceil(nonStreamingTextureMemory / 1048576) MiB to EFT's selected streaming allowance, plus one 100 MiB icon allowance while any render owns policy. Recomputed every 500ms and on native policy changes. EFT base fields remain unmodified. mip_budget_* columns show components and accounting state; budget_mb is the actual Unity total. External budget writes yield ownership until a native scene/settings/icon policy write. No hardware VRAM limit or mip-0 guarantee is inferred from this accounting.\n" +
                "icon-mips.csv: bounded main-thread texture observations from the enabled icon lifetime adapter; request, timeout, request-texture-end (before cleanup), request-end summary and render-end/cleanup phases. IDs are process-local. Timeout decision and subsequent loaded-state observation can differ at a completion boundary. Material/property is the first reference to each texture in a request.\n" +
                "Icon probes also start captures after idle closure, including in-raid rendering. Same 6 captures/process and 600s/capture bounds apply. At most 8192 icon rows/capture and 1024 retained texture probe records/render; overflow is reported. Texture getters are read only; player-loop/active-scene context is cached once per Unity frame. No writer thread touches Unity objects.\n" +
                "Capture includes background hideout preloads and concurrent work. Stops 10s after entry and pending icon work complete, 30s after idle preloading/icon work, or at 600s. Later icon renders can start a new capture. Maximum 6 captures/process.\n" +
                "Bounds: 2048 spans, 1400 samples, 8192 queued rows, 32 tracked scenes, 50 streaming log messages. Dropped rows are counted.\n" +
                "worker.csv samples managed worker state every 1s without taking EFT locks. Observations are not an atomic call-stack snapshot; trees_at_acquisition is the count when the worker last acquired its tree lock.\n" +
                "decal-mips.csv: native texture-request observations after UpdateTextureRequestedMipMapLevel, at most once per tree/camera/second, 16 samples/frame, 256 trees and 8 cameras/tree. Result fields are copied only if the separate result synchronization patches are installed, using a nonblocking result gate; no native tree lock is taken. Rows for other camera results explain competing/stale visibility. Result age is frames, using native DropResultsAfterFramesCount; raw result_mip is before native quality bias/offset. Field writes can span separate worker critical sections: snapshots are not atomic worker iterations.\n" +
                "decal-bindings.csv: links draw material texture IDs to nearby native decal objects, original material ID, scene/path, atlas pixel rectangle and tint. At most 512 decal objects scanned per 250ms step with a 1ms time budget; only objects within 50m of the rendering camera are recorded. Bindings are deduplicated per object/material/property/texture ID, not a full scene inventory. Join files by texture_id and use positions/paths to locate ground patches. Missing/non-Texture2D bindings are explicit. 65536 mip rows and 4096 binding rows/capture; limits/queue loss are reported.\n" +
                "Raid decal rendering starts/keeps captures alive without inventory activity, until the same absolute 600s cap (maximum 6 captures/process). Texture/mip/material getters only; no GPU pointer acquisition, readback or pixel access.\n" +
                "Research wrappers forward original operations unchanged. The separate optional StopTextureWorkerBeforeTeardown fix cancels/joins before owned tree teardown; its quiesce span and result are recorded.\n" +
                "Additional guarded worker method: " + find("TextureQuadTree").FullName + ".WorkThread(object) | token=" +
                AccessTools.DeclaredMethod(find("TextureQuadTree"), "WorkThread", new[] { typeof(object) }).MetadataToken.ToString("X8") + "\n" +
                "Research observes textures already used by icon/decal rendering; no global texture scans, forced collection, extra mip requests, graphics writes or loading-policy changes.\n\nHooks:\n" +
                string.Join("\n", specs.Select(s => s.Stage + " | " + s.Method.DeclaringType.FullName + "." + s.Method.Name + " | " + s.Method + " | token=" + s.Method.MetadataToken.ToString("X8"))) + "\n";
        }
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new MissingFieldException(type.FullName, name);

        internal static List<HideoutResearchHook> Resolve(Func<string, Type> find)
        {
            var result = new List<HideoutResearchHook>();
            void Add(Type type, string name, int arguments, string stage, bool start = false, bool entry = false, bool gameOnly = false, string firstParameterType = null)
            {
                MethodInfo method = AccessTools.GetDeclaredMethods(type).Single(m => m.Name == name && m.GetParameters().Length == arguments &&
                    (firstParameterType == null || m.GetParameters()[0].ParameterType.Name == firstParameterType));
                if (!typeof(Task).IsAssignableFrom(method.ReturnType) && method.ReturnType != typeof(void)) throw new MissingMethodException("Hideout research Task/void ABI: " + method);
                result.Add(new HideoutResearchHook { Method = method, Stage = stage, Start = start, Entry = entry, HideoutGameOnly = gameOnly });
            }
            Type controller = find("EFT.TarkovApplication").GetNestedType("HideoutController", BindingFlags.Public | BindingFlags.NonPublic) ?? throw new TypeLoadException("HideoutController");
            Add(controller, "HideoutSelectedHandler", 2, "hideout.entry", true, true);
            Add(controller, "method_0", 0, "hideout.boot");
            Add(controller, "method_1", 0, "hideout.map-preload", true);
            Add(controller, "method_2", 0, "hideout.await-resources");
            Add(controller, "method_3", 0, "hideout.world-create");
            Add(controller, "method_4", 0, "hideout.production-bundles");
            Add(controller, "method_5", 1, "hideout.profile-bundles-pools", true);
            Add(controller, "method_7", 3, "hideout.game-create-run");
            Add(controller, "method_8", 0, "hideout.awake-load-complete");
            Add(find("EFT.Hideout.HideoutRepresentation"), "SetHideoutData", 2, "hideout.apply-data-customization");
            Add(find("EFT.ClientBackendSession"), "FlushOperationQueue", 0, "backend.flush-operations");
            Type modern = find("EFT.ModernLoadScenesFromPreset");
            Add(modern, "Load", 1, "scene.preset"); Add(modern, "LoadScene", 3, "scene.await");
            Type game = find("EFT.BaseLocalGame`1").MakeGenericType(find("EFT.HideoutPlayerOwner"));
            Add(game, "Run", 3, "hideout.game-run", gameOnly: true);
            Add(game, "SpawnLoot", 1, "hideout.spawn-loot", gameOnly: true);
            Add(game, "PrepareSession", 0, "hideout.prepare-session", gameOnly: true);
            Add(find("EFT.HideoutGame"), "vmethod_3", 18, "hideout.player-create");
            Add(find("EFT.ObjectsFactory"), "LoadBundlesAndCreatePools", 6, "resources.bundles-pools", firstParameterType: "PoolsCategory");
            Add(find("ItemIconCreator"), "RequestMipZero", 1, "icons.item-mip-zero");
            Add(find("ItemIconCreator"), "RenderModel", 2, "icons.item-render", start: true);
            Add(find("EFT.PlayerIcons.PlayerIconCreator"), "RenderModel", 2, "icons.player-render", start: true);
            Add(find("IconCreatorGraphicsSettings"), "RequestMipZero", 1, "icons.player-mip-zero");
            Add(find("IconCreatorGraphicsSettings"), "RequestMipZeroFast", 1, "icons.player-mip-zero-fast");
            Type customization = find("EFT.Hideout.HideoutCustomizationController");
            Add(customization, "InstallCustomization", 2, "customization.install-pack", firstParameterType: "Dictionary`2");
            Add(customization, "InstallSelectedCustomization", 0, "customization.install-selected");
            Add(customization, "LoadBundles", 1, "customization.load-bundles");
            Add(customization, "InstallCustomization", 2, "customization.install-item", firstParameterType: "MongoID");
            Add(customization, "InstallCustomization", 2, "customization.install-prefab", firstParameterType: "ResourceKey");
            Add(find("EFT.Hideout.HideoutAreaStashController"), "UpdateStash", 1, "customization.update-stash");
            Type installer = find("EFT.Hideout.HideoutCustomizationItemsInstaller");
            Add(installer, "SetItem", 2, "customization.set-item");
            Add(installer, "SetPaperTargetTexture", 1, "customization.set-target-texture");
            Type decals = find("StaticDeferredDecalRenderer");
            Add(decals, "UpdateInstancesBuffers", 0, "decals.rebuild-buffers");
            Add(decals, "ConstructQuadTree", 0, "decals.construct-trees");
            Add(decals, "ClearTree", 0, "decals.clear-trees");
            Add(find("TextureQuadTree"), "Dispose", 0, "decals.texture-tree.dispose");
            Add(find("StaticDeferredDecalQuadTree"), "Dispose", 0, "decals.render-tree.dispose");
            Type draw = find("StaticDeferredDecalDrawInstance");
            Add(draw, "InitGlobalBuffers", 1, "decals.init-global-buffers");
            Add(draw, "UpdateBuffers", 3, "decals.update-draw-buffers");
            Add(draw, "InitInstance", 4, "decals.init-draw-instance");
            MethodInfo scene = typeof(SceneManager).GetMethod("LoadSceneAsync", new[] { typeof(string), typeof(LoadSceneMode) });
            if (scene?.ReturnType != typeof(AsyncOperation) || scene.GetMethodBody() == null) throw new MissingMethodException("Managed SceneManager.LoadSceneAsync wrapper");
            result.Add(new HideoutResearchHook { Method = scene, Stage = "scene.native-operation", Scene = true });
            return result;
        }

        internal static void Install(Harmony harmony, List<HideoutResearchHook> specs)
        {
            foreach (var spec in specs)
                harmony.Patch(spec.Method, Hook(nameof(Prefix), Priority.First),
                    Hook(spec.Scene ? nameof(ScenePostfix) : typeof(Task).IsAssignableFrom(spec.Method.ReturnType) ? nameof(TaskPostfix) : nameof(SyncPostfix), Priority.Last),
                    finalizer: Hook(nameof(Finalizer), Priority.Last));
            var disposal = specs.SingleOrDefault(s => s.Stage == "decals.texture-tree.dispose");
            if (disposal != null) HideoutDecalProbes.Install(harmony, disposal.Method.DeclaringType);
        }
        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(typeof(HideoutResearch), name) { priority = priority };

        private HideoutResearchSpan Begin(MethodBase method, object instance, object[] args)
        {
            if (!hooks.TryGetValue(method, out var hook) || (hook.HideoutGameOnly && instance?.GetType().FullName != "EFT.HideoutGame")) return null;
            if (capture?.Closed == true) Retire();
            if (hook.Start) EnsureCapture(hook.Stage);
            if (capture == null) return null;
            string detail = hook.Stage.StartsWith("scene.") && args.Length > 0 && args[0] is string name ? name : "";
            int resources = Array.FindIndex(method.GetParameters(), p => p.Name == "resources");
            if (resources >= 0 && args[resources] is ICollection collection) detail = "resources=" + collection.Count;
            if (hook.Stage.StartsWith("customization."))
                for (int i = 0; i < args.Length; i++)
                    if (args[i] is Enum) detail = "customization_type=" + args[i];
            if (hook.Stage == "decals.texture-tree.dispose" && instance != null)
                detail = "tree_hash=" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);
            var span = capture.Begin(hook.Stage, hook.Entry, detail);
            if (Environment.CurrentManagedThreadId == mainThread && nextSample == 0) Pump();
            return span;
        }
        private void EnsureCapture(string trigger)
        {
            if (capture?.Closed == true) Retire();
            if (capture == null && captures < 6 && Environment.CurrentManagedThreadId == mainThread)
            {
                string directory = Path.Combine(output, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                capture = new HideoutResearchCapture(directory, metadata, SampleHeader, workerSnapshot: Decals.Snapshot); captures++;
                lastPump = HideoutResearchCapture.Now; nextSample = 0; maxGap = 0; sampleFailed = false;
                capture.Note("capture-trigger", trigger);
                log("Hideout research recording: " + directory);
            }
        }
        internal HideoutResearchCapture IconCapture()
        {
            if (Environment.CurrentManagedThreadId != mainThread) return null;
            EnsureCapture("icons.mip-probe"); return capture;
        }
        internal HideoutResearchCapture DecalCapture()
        {
            if (Environment.CurrentManagedThreadId != mainThread) return null;
            EnsureCapture("decals.mip-probe"); return capture;
        }

        internal void Pump()
        {
            if (capture == null) return;
            if (capture.Closed) { Retire(); return; }
            double now = HideoutResearchCapture.Now;
            maxGap = Math.Max(maxGap, now - lastPump); lastPump = now; capture.Pulse();
            if (now < nextSample) return;
            nextSample = now + 500;
            try { if (!sampleFailed) capture.Sample(Snapshot(maxGap)); }
            catch (Exception error) { sampleFailed = true; capture.Note("sample-error", error.GetType().Name + ": " + error.Message); }
            maxGap = 0;
            for (int i = scenes.Count - 1; i >= 0; i--)
            {
                var watch = scenes[i];
                if (watch.Span.Owner.Closed) { scenes.RemoveAt(i); continue; }
                float progress = watch.Operation.progress;
                if (progress != watch.Progress || watch.Operation.isDone)
                {
                    watch.Progress = progress;
                    watch.Span.Owner.Note("scene-progress", "span=" + watch.Span.Id + " progress=" + HideoutResearchCapture.Number(progress) + " done=" + watch.Operation.isDone + " allow_activation=" + watch.Operation.allowSceneActivation);
                }
                if (watch.Operation.isDone) { watch.Span.Finish("completed"); scenes.RemoveAt(i); }
            }
        }
        private string Snapshot(double gap)
        {
            object camera = cameraInstance.GetValue(null);
            var streaming = camera == null ? null : cameraController.GetValue(camera) as StreamingController;
            return string.Join(",", new[] {
                Time.frameCount.ToString(), HideoutResearchCapture.Number(gap), B(QualitySettings.streamingMipmapsActive), N(QualitySettings.streamingMipmapsMemoryBudget),
                B(Texture.streamingTextureDiscardUnusedMips), N(QualitySettings.streamingMipmapsMaxLevelReduction), B(QualitySettings.streamingMipmapsAddAllCameras),
                N(QualitySettings.streamingMipmapsMaxFileIORequests), N(QualitySettings.asyncUploadTimeSlice), N(QualitySettings.asyncUploadBufferSize),
                N(Texture.currentTextureMemory), N(Texture.desiredTextureMemory), N(Texture.targetTextureMemory), N(Texture.totalTextureMemory), N(Texture.nonStreamingTextureMemory),
                N(Texture.streamingTexturePendingLoadCount), N(Texture.streamingTextureLoadingCount), N(Texture.streamingMipmapUploadCount), N(Texture.streamingTextureCount),
                N(GC.GetTotalMemory(false)), N(Application.targetFrameRate), N(QualitySettings.vSyncCount), N((int)Application.backgroundLoadingPriority),
                streaming == null ? "" : N(streaming.streamingMipmapBias), N(itemRequests.GetValue(null)), N(itemBudget.GetValue(null)), N(playerRequests.GetValue(null)), N(playerBudget.GetValue(null)),
                N((playerTextures.GetValue(null) as ICollection)?.Count ?? -1), HideoutResearchCapture.Quote(StreamingManager(PlayerLoop.GetCurrentPlayerLoop())),
                N(IconMipLifetime.ActiveTextures), N(IconMipLifetime.ActiveBudgets), N(IconMipLifetime.Completed), N(IconMipLifetime.Failed), N(IconMipLifetime.ReleasedTextures) }) + "," + MipBudgetAccounting.Snapshot();
        }
        private static string B(bool value) => value ? "true" : "false";
        private static string N(object value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        internal static string StreamingManager(PlayerLoopSystem loop)
        {
            if (loop.type == typeof(UnityEngine.PlayerLoop.EarlyUpdate.UpdateTextureStreamingManager)) return "native";
            if (loop.type?.Name == "CustomUpdateTextureStreamingManager") return "replaced";
            if (loop.subSystemList != null) foreach (var child in loop.subSystemList)
            { string found = StreamingManager(child); if (found != "absent") return found; }
            return "absent";
        }
        internal void OnLog(string message, string stack, LogType type)
        {
            if (message == null) return;
            if (message.IndexOf("Mip 0 waiting timeout", StringComparison.OrdinalIgnoreCase) >= 0 || message.IndexOf("texture streaming", StringComparison.OrdinalIgnoreCase) >= 0)
                capture?.StreamingLog(message);
        }
        internal HideoutResearchSpan BeginDetail(string stage, string detail) => capture?.Begin(stage, detail: detail);
        internal void NoteDetail(string stage, string detail) => capture?.Note(stage, detail);
        private void Retire()
        {
            var old = capture; capture = null; scenes.Clear();
            log("Hideout research capture closed: " + old.DirectoryPath + (old.Error == null ? "" : " | writer failed: " + old.Error));
        }
        internal void Stop()
        {
            var old = capture; capture = null; scenes.Clear();
            old?.Close("plugin-stopped"); old?.Join(250);
        }
        internal static void Prefix(MethodBase __originalMethod, object __instance, object[] __args, out HideoutResearchSpan __state)
        { __state = null; try { __state = Active?.Begin(__originalMethod, __instance, __args); } catch { } }
        internal static void TaskPostfix(Task __result, HideoutResearchSpan __state)
        { try { __state?.Observe(__result); } catch { } }
        internal static void SyncPostfix(HideoutResearchSpan __state)
        { try { __state?.Finish("completed"); } catch { } }
        internal static void ScenePostfix(AsyncOperation __result, HideoutResearchSpan __state)
        {
            if (__state == null) return;
            try
            {
                __state.Owner.LaunchReturned(__state);
                if (__result == null) __state.Finish("null-operation");
                else if (Active != null && Active.scenes.Count < 32) Active.scenes.Add(new SceneWatch { Operation = __result, Span = __state });
                else __state.Finish("scene-watch-cap");
            }
            catch { __state.Finish("observer-failed"); }
        }
        internal static void Finalizer(Exception __exception, HideoutResearchSpan __state)
        { if (__exception != null) { try { __state?.Finish("threw:" + __exception.GetType().Name); } catch { } } }
    }
}
