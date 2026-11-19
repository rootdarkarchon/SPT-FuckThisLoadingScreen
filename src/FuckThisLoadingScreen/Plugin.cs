using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    [BepInPlugin("com.rootdarkarchon.fuckthisloadingscreen", "FuckThisLoadingScreen", "1.1.0")]
    [BepInDependency("com.SPT.custom", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.rootdarkarchon.ftls.levelstagetimer", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string SupportedModuleId = "cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7";
        private Harmony harmony;
        private Harmony botPreloadHarmony;
        private LoadingPolicy policy;
        private HideoutResearch hideoutResearch;
        private Harmony hideoutHarmony;
        private Harmony textureWorkerHarmony;
        private ConfigEntry<bool> textureWorkerTeardown;
        private Harmony decalResultHarmony, iconMipHarmony;
        private bool decalResultsArmed, iconMipsArmed;
        private Harmony mipResidencyHarmony;
        private Harmony mipBudgetHarmony, decalArrayHarmony, textureQualityHarmony, bundlePatchHarmony;
        private bool mipBudgetPumpFailed;
        private sealed class RunReport { internal string Summary; internal LoadingRun Run; }
        private readonly Queue<RunReport> reports = new Queue<RunReport>();
        private ConfigEntry<bool> master, audio, measurements, parallel, spawnZones, sceneProbes, lootPriority, frameOverride, boundedScenes, debugReports, reduceBotPreloadDelay, dumpTextures, applyTextureQuality;
        private ConfigEntry<int> batch, frameLimit, sceneWindow;
        private ConfigEntry<LoadingPriority> priority;

        private void Awake()
        {
            master = Config.Bind("General", "Enabled", true, "Master switch. Options are frozen at the start of each loading attempt; changes apply to the next attempt.");
            debugReports = Config.Bind("Debug", "Write Loading Reports", false, "Write per-load option/counter summaries to the BepInEx log and TXT/CSV reports to BepInEx/FuckThisLoadingScreen. Off by default. Options freeze per attempt; changes apply to the next loading attempt.");
            dumpTextures = Config.Bind("Debug", "Dump Loaded Textures", false, "RESEARCH ACTION: set to true to write one inventory of every loaded texture (type, size, format, streaming state, mipmap limit, estimated memory) plus a grouped summary to BepInEx/FuckThisLoadingScreen/TextureInventory. Resets itself to false. Scans all textures once; expect a short hitch.");
            dumpTextures.Value = false;
            bool researchEnabled = Config.Bind("Debug", "Write Hideout Research Reports", false, "RESEARCH: record hideout entry/preloads, scene progress, streaming counters, per-texture icon mip probes (with Release Icon Mip Requests enabled) and a background heartbeat under BepInEx/FuckThisLoadingScreen/HideoutResearch. Observation only; independent of General/Enabled. Default off; restart after changing. Later raid icons can start a capture. Up to 6 captures per process, 10 minutes each, including 10 seconds after entry and pending icon work finish. Files flush during loading and remain usable after a freeze.").Value;
            textureWorkerTeardown = Config.Bind("Loading", "Stop Texture Worker Before Teardown", true, "Stop the native decal texture worker once before its owning renderer clears all registered texture trees. Avoids repeated lock starvation with mip streaming. Native disposal and restart remain in charge. Checked once per ClearTree call; General/Enabled also applies. Off is the native comparison.");
            bool fixDecalResults = Config.Bind("Mip Streaming", "Synchronize Decal Results", true, "Synchronize decal result enumeration, insertion and field writes using a short independent lock. Restart required; General/Enabled is read at startup for this safety fix.").Value;
            bool fixDecalArrays = Config.Bind("Mip Streaming", "Resync Decal Texture Arrays", true, "When EFT copies a mip-streamed decal texture into its shared decal texture array before mip 0 is resident, request mip 0 and recopy that array slice once loaded. Fixes decals drawn as solid rectangles with mip streaming on. The native array build is unchanged. Restart required; General/Enabled is read at startup.").Value;
            bool fixIconMips = Config.Bind("Mip Streaming", "Release Icon Mip Requests", true, "Scope item/player icon texture requests, temporary memory policy and camera bias to the native render Task, including failure/cancellation. Shared textures/policy remain owned until the last render finishes. Restart required; General/Enabled is read at startup.").Value;
            bool fixMipBudget = Config.Bind("Mip Streaming", "Account For Nonstreaming Textures", true, "Treat EFT's selected map/quality budget as the streamed-texture allowance. Add measured resident nonstreaming texture memory to Unity's total budget; refresh every 500ms and on scene/settings/icon policy changes. Keeps one shared +100 MiB icon allowance and native mip limits/bias. Requires Release Icon Mip Requests. Off provides the previous budget comparison. Restart required; General/Enabled is read at startup.").Value;
            applyTextureQuality = Config.Bind("Mip Streaming", "Apply Texture Quality With Streaming", false, "EXPERIMENTAL: keep EFT's texture-quality mipmap limit (Medium drops one mip, Low two) when mip streaming is on. EFT normally forces the limit to 0 with streaming, so textures that cannot stream always load at full size. Applied when EFT applies texture quality; restart after changing.");
            bool markStreamable = Config.Bind("Mip Streaming", "Mark Item Textures Streamable", false, "EXPERIMENTAL: while EFT loads its item and weapon bundles, present eligible textures (mipmapped, not CPU-readable, not already streaming) as mip-streamable so they stream instead of loading at full size. The bundle files are read through a patching stream; nothing is written to disk. Restart required; General/Enabled is read at startup.").Value;
            bool retainMips = Config.Bind("Experimental", "Retain Unused Streamed Mips", false, "A/B candidate: allow Unity to cache unused texture mips within the selected streaming memory budget instead of EFT's forced immediate discard. Live benefit is unmeasured. Restart required; General/Enabled is read at startup.").Value;
            audio = Config.Bind("Audio", "Larger Reader Batches", true, "Use the configured audio bake reader batch during raid map initialization. Off retains native batches of 300.");
            batch = Config.Bind("Audio", "Reader Batch Size", 1200,
                new ConfigDescription("Audio records per coroutine batch. 300 is native; 1200 is the first test setting. Larger batches can increase main-thread slices.", new AcceptableValueRange<int>(300, 4800)));
            measurements = Config.Bind("Memory", "Non-Collecting Diagnostic Measurements", true, "During raid loading only, use non-collecting memory queries at the two diagnostic call sites inside cleanup. Explicit collections and GC policy remain native.");
            priority = Config.Bind("Loading", "Integration Priority", LoadingPriority.High, "Native preserves the existing priority. Normal/High apply during LoadMapAndData and optionally the awaited loot bundle/pool phase; restore the previous priority on completion, cancellation, failure or shutdown.");
            lootPriority = Config.Bind("Loading", "Extend Priority Through Loot Bundles", true, "Apply the configured integration priority to the awaited bundle/pool Task launched by SpawnLoot. Does not extend map-only audio or scene controls. Off retains the previous map-only priority scope. Retention/yield budgets remain native.");
            reduceBotPreloadDelay = Config.Bind("Loading", "Reduce Bot Profile Preload Delay", true, "During raid startup, reduce the fixed initial bot-profile batch delay from 500 ms to 1 ms. Retains a nonzero asynchronous delay, sequential backend requests and native profile handling. Off restores 500 ms. Options freeze per attempt; backup profile requests remain native.");
            parallel = Config.Bind("Experimental", "Parallel Scene Loading", false, "TEST ONLY: use native parallel scheduling within each preset, with as-ready scene activation so Unity's async queue can progress. Child presets retain native order. Changes activation ordering; default is sequential.");
            frameOverride = Config.Bind("Loading", "Loading Frame Cap Override", true, "During LoadMapAndData only, use Loading Frame Limit with VSync disabled. Restore prior values on completion/cancellation/failure. Job-scheduler frame budget is unchanged. Options freeze per attempt. Off provides the native frame-cap comparison.");
            frameLimit = Config.Bind("Loading", "Loading Frame Limit", 240, new ConfigDescription("Render frame limit for Loading Frame Cap Override; applies only to map loading.", new AcceptableValueRange<int>(60, 360)));
            boundedScenes = Config.Bind("Scenes", "Bounded As-Ready Scene Activation", true, "Keep at most Scene In-Flight Limit additional scenes loading with activation allowed as ready. First scene and child-preset request order stay native; callbacks may happen earlier. Takes precedence over the older Parallel Scene Loading toggle. Off provides the native scheduling comparison when Parallel Scene Loading is also off.");
            sceneWindow = Config.Bind("Scenes", "Scene In-Flight Limit", 2, new ConfigDescription("Maximum additional scenes in flight per preset in bounded as-ready mode.", new AcceptableValueRange<int>(1, 4)));
            spawnZones = Config.Bind("Loading", "Scoped Spawn Zone Lookup", true, "During loading, reuse the native ordered BotZone array only inside CreateFromScene/FixSpawns. Marker creation outside that scope keeps native discovery. Off is the native comparison mode.");
            sceneProbes = Config.Bind("Loading", "Skip Built-In Scene Bundle Probes", true, "Skip only the preliminary bundle probe for an exact built-in .unity scene with no manifest/mod/cache/pending entry and no local bundle file. Unknown paths and actual bundle loading retain native behavior. Off provides the native A/B comparison.");
            try
            {
                Type app = Find("EFT.TarkovApplication");
                if (app.Module.ModuleVersionId != new Guid(SupportedModuleId))
                    throw new NotSupportedException("Unsupported EFT module MVID: " + app.Module.ModuleVersionId);
                policy = new LoadingPolicy(ReadOptions, () => (int)Application.backgroundLoadingPriority,
                    value => Application.backgroundLoadingPriority = (UnityEngine.ThreadPriority)value,
                    mode => (int)(mode == LoadingPriority.High ? UnityEngine.ThreadPriority.High : UnityEngine.ThreadPriority.Normal),
                    (text, run) => { if (reports.Count < 8) reports.Enqueue(new RunReport { Summary = text, Run = run }); },
                    frameCap: new LoadingFrameCap(() => Application.targetFrameRate, value => Application.targetFrameRate = value,
                        () => QualitySettings.vSyncCount, value => QualitySettings.vSyncCount = value));
                harmony = new Harmony(Info.Metadata.GUID);
                foreach (var entry in new[] { ("LocalGameMatching", 2), ("NetworkGameMatching", 2), ("GameReconnect", 5) })
                    Patch(One(app, entry.Item1, entry.Item2), nameof(PolicyHooks.RootPrefix), nameof(PolicyHooks.RootPostfix), nameof(PolicyHooks.RootFinalizer));
                Patch(One(app, "LoadMapAndData", 4), nameof(PolicyHooks.MapPrefix), nameof(PolicyHooks.MapPostfix), nameof(PolicyHooks.MapFinalizer));
                InstallLootBundlePriority(harmony, Find);
                Type metrics = Find("EFT.Utilities.ClientMetricsEvents");
                harmony.Patch(metrics.GetConstructors().Single(), postfix: Hook(nameof(PolicyHooks.MetricsCreated), Priority.Last));
                harmony.Patch(One(metrics, "SetGameStarted", 0), postfix: Hook(nameof(PolicyHooks.GameStarted), Priority.Last));

                MethodInfo audioGetter = One(Find("Audio.SpatialSystem.AudioBakeDataReader"), "get_HasReachedMaxIterationsPerFrame", 0);
                harmony.Patch(audioGetter, transpiler: Hook(nameof(PolicyHooks.AudioTranspiler), Priority.Normal));
                Type memory = Find("InGameMemoryManagement");
                PolicyHooks.NativeMeasureMethod = One(memory, "GetTotalAllocatedMemoryGB", 0);
                PolicyHooks.NativeMeasure = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), PolicyHooks.NativeMeasureMethod);
                harmony.Patch(One(memory, "Collect", 5), transpiler: Hook(nameof(PolicyHooks.MemoryTranspiler), Priority.Normal));

                Type presets = Find("EFT.LoadScenesFromPresetOperation");
                MethodInfo[] execute = AccessTools.GetDeclaredMethods(presets).Where(m => m.Name == "Execute" && m.GetParameters().Length == 7).ToArray();
                if (execute.Length != 2 || execute.Any(m => m.GetParameters()[3].Name != "loadInParallelMode" || m.GetParameters()[4].Name != "allowSceneActivation"))
                    throw new MissingMethodException("Scene preset parallel flag ABI mismatch");
                foreach (MethodInfo method in execute) harmony.Patch(method, prefix: Hook(nameof(PolicyHooks.PresetPrefix), Priority.Last));
                Patch(One(presets, "ParallelLoadCoroutine", 2), nameof(PolicyHooks.SchedulingPrefix), nameof(PolicyHooks.SchedulingPostfix), nameof(PolicyHooks.SchedulingFinalizer));
                MethodInfo loadScene = One(Find("EFT.AssetsManager.AssetsManager"), "LoadScene", 5);
                if (loadScene.GetParameters()[3].Name != "allowSceneActivation") throw new MissingMethodException("Scene activation ABI mismatch");
                harmony.Patch(loadScene, prefix: Hook(nameof(PolicyHooks.ScenePrefix), Priority.Last));
                InstallSpawnZoneLookup();
                SceneBundleHooks.Install(harmony, Find);
                BoundedScenes.Enabled = () => policy?.BoundedSceneRun != null;
                BoundedScenes.Window = () => policy?.BoundedSceneRun?.Options.SceneWindow ?? 2;
                BoundedScenes.CaptureActive = () => policy?.CaptureSceneActivity();
                BoundedScenes.BeginReport = () => policy?.BeginSceneReport();
                BoundedScenes.Install(harmony, Find);
                PolicyHooks.Policy = policy; // Arm only once every guarded patch has succeeded.
                StartBotPreloadDelayFix();
                Logger.LogInfo("FTLS armed; native comparison modes retained. Options are frozen per attempt. " + ReadOptions());
                StartTextureWorkerFix();
                if (master.Value && fixDecalResults) StartDecalResultsFix();
                if (master.Value && fixDecalArrays) StartDecalArrayResync();
                if (master.Value && fixIconMips) StartIconMipFix();
                if (master.Value && fixMipBudget && iconMipsArmed) StartMipBudgetAccounting();
                else if (master.Value && fixMipBudget) Logger.LogWarning("Mip budget accounting requires the icon lifetime adapter; accounting remains off.");
                if (master.Value && retainMips) StartMipResidencyPolicy();
                if (master.Value) StartTextureQualityWithStreaming();
                if (master.Value && markStreamable) StartStreamingBundlePatch();
                if (researchEnabled) StartHideoutResearch();
            }
            catch (Exception error)
            {
                Logger.LogError("FTLS refused incomplete/unsupported patches: " + error);
                Cleanup(); enabled = false;
            }
        }

        private Options ReadOptions() => new Options { Enabled = master.Value, AudioBatching = audio.Value,
            AudioBatchSize = Math.Max(300, Math.Min(4800, batch.Value)), NonCollectingMeasurements = measurements.Value,
            Priority = priority.Value, ParallelScenes = parallel.Value, ScopedSpawnZones = spawnZones.Value, SkipBuiltInSceneProbes = sceneProbes.Value, LootBundlePriority = lootPriority.Value,
            FrameCapOverride = frameOverride.Value, LoadingFrameLimit = frameLimit.Value, BoundedScenes = boundedScenes.Value, SceneWindow = sceneWindow.Value, DebugReports = debugReports.Value,
            ReduceBotProfilePreloadDelay = reduceBotPreloadDelay.Value };
        private void StartBotPreloadDelayFix()
        {
            try
            {
                botPreloadHarmony = new Harmony(Info.Metadata.GUID + ".bot-preload-delay");
                BotProfilePreloadDelay.Install(botPreloadHarmony, Find);
                Logger.LogInfo("Initial bot profile preload delay adapter armed; comparison toggle retained.");
            }
            catch (Exception error)
            {
                botPreloadHarmony?.UnpatchSelf(); botPreloadHarmony = null;
                Logger.LogWarning("Bot profile preload delay remains native: " + error.Message);
            }
        }
        internal static void InstallLootBundlePriority(Harmony target, Func<string, Type> find)
        {
            Type game = find("EFT.BaseLocalGame`1").MakeGenericType(find("EFT.EftGamePlayerOwner"));
            MethodInfo spawn = One(game, "SpawnLoot", 1);
            MethodInfo load = AccessTools.GetDeclaredMethods(find("EFT.ObjectsFactory")).Single(m => m.Name == "LoadBundlesAndCreatePools" &&
                m.GetParameters().Length == 6 && m.GetParameters()[0].Name == "pools");
            if (spawn.ReturnType != typeof(Task) || load.ReturnType != typeof(Task) || spawn.ContainsGenericParameters || load.ContainsGenericParameters)
                throw new MissingMethodException("Loot bundle priority Task ABI");
            target.Patch(spawn, Hook(nameof(PolicyHooks.LootLaunchPrefix), Priority.First), Hook(nameof(PolicyHooks.LootLaunchPostfix), Priority.Last),
                finalizer: Hook(nameof(PolicyHooks.LootLaunchFinalizer), Priority.Last));
            target.Patch(load, Hook(nameof(PolicyHooks.LootBundlesPrefix), Priority.First), Hook(nameof(PolicyHooks.LootBundlesPostfix), Priority.Last),
                finalizer: Hook(nameof(PolicyHooks.LootBundlesFinalizer), Priority.Last));
        }
        private void InstallSpawnZoneLookup()
        {
            Type collection = Find("EFT.Game.Spawning.SpawnPointsCollection"), marker = Find("EFT.Game.Spawning.SpawnPointMarker"), zone = Find("BotZone");
            MethodInfo fix = One(collection, "FixSpawns", 4), create = One(collection, "CreateFromScene", 2);
            if (fix.GetParameters()[2].Name != "zones" || fix.GetParameters()[2].ParameterType != zone.MakeArrayType())
                throw new MissingMethodException("Spawn reconciliation zone-array ABI mismatch");
            MethodInfo markerCreate = AccessTools.GetDeclaredMethods(marker).Single(m => m.Name == "Create" &&
                m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(GameObject));
            MethodInfo discover = AccessTools.GetDeclaredMethods(Find("FindObjectsProxy")).Single(m => m.Name == "FindUnityObjectsOfType" &&
                m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0).MakeGenericMethod(zone);
            SpawnZoneHooks.NativeFindMethod = discover;
            AccessTools.Method(typeof(SpawnZoneHooks), nameof(SpawnZoneHooks.BindNative)).MakeGenericMethod(zone).Invoke(null, new object[] { discover });
            harmony.Patch(create, SpawnHook(nameof(SpawnZoneHooks.CollectionPrefix), Priority.Last), SpawnHook(nameof(SpawnZoneHooks.CollectionPostfix), Priority.First),
                finalizer: SpawnHook(nameof(SpawnZoneHooks.CollectionFinalizer), Priority.First));
            harmony.Patch(fix, SpawnHook(nameof(SpawnZoneHooks.FixPrefix), Priority.Last), SpawnHook(nameof(SpawnZoneHooks.FixPostfix), Priority.First),
                finalizer: SpawnHook(nameof(SpawnZoneHooks.FixFinalizer), Priority.First));
            harmony.Patch(markerCreate, transpiler: SpawnHook(nameof(SpawnZoneHooks.MarkerTranspiler), Priority.Normal));
        }
        private static HarmonyMethod SpawnHook(string name, int priority) => new HarmonyMethod(typeof(SpawnZoneHooks), name) { priority = priority };
        private static Type Find(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);
        private static MethodInfo One(Type type, string name, int count) => AccessTools.GetDeclaredMethods(type)
            .Single(m => m.Name == name && m.GetParameters().Length == count && !m.IsAbstract);
        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(typeof(PolicyHooks), name) { priority = priority };
        private void Patch(MethodInfo method, string prefix, string postfix, string finalizer)
        {
            if (!typeof(Task).IsAssignableFrom(method.ReturnType)) throw new NotSupportedException(method.ToString());
            harmony.Patch(method, Hook(prefix, Priority.First), Hook(postfix, Priority.Last), finalizer: Hook(finalizer, Priority.Last));
        }
        private void Update()
        {
            policy?.Pump();
            try { if (!mipBudgetPumpFailed) MipBudgetAccounting.Active?.Pump(); }
            catch (Exception error)
            {
                mipBudgetPumpFailed = true;
                Logger.LogError("Mip budget accounting stopped polling; cleanup waits for pending icon renders: " + error);
                IconMipLifetime.WhenDrained(StopMipBudgetAccounting);
            }
            try { DecalTextureArrayResync.Active?.Pump(); }
            catch (Exception error) { Logger.LogError("Decal texture-array resync stopped after an error: " + error); StopDecalArrayResync(); }
            try { hideoutResearch?.Pump(); }
            catch (Exception error) { Logger.LogError("Hideout research stopped after an observer error: " + error); StopHideoutResearch(); }
            if (policy != null && !policy.IsLoading) FlushReports();
            if (dumpTextures.Value) DumpTextures();
            try { StreamingBundlePatch.Active?.Sweep(Time.realtimeSinceStartupAsDouble * 1000); }
            catch (Exception error) { Logger.LogError("Streamable texture patch stream cleanup failed: " + error); }
        }
        private void DumpTextures()
        {
            dumpTextures.Value = false;
            try { Logger.LogInfo(TextureInventory.Dump(Path.Combine(Paths.BepInExRootPath, "FuckThisLoadingScreen", "TextureInventory"), UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)); }
            catch (Exception error) { Logger.LogError("Texture inventory failed: " + error); }
        }
        private void StartTextureQualityWithStreaming()
        {
            try
            {
                textureQualityHarmony = new Harmony(Info.Metadata.GUID + ".texture-quality-streaming");
                TextureQualityWithStreaming.Log = text => Logger.LogInfo(text);
                TextureQualityWithStreaming.Install(textureQualityHarmony, Find, new Guid(SupportedModuleId), () => master.Value && applyTextureQuality.Value);
                if (applyTextureQuality.Value) Logger.LogInfo("Texture quality mipmap limit is kept with mip streaming.");
            }
            catch (Exception error)
            {
                textureQualityHarmony?.UnpatchSelf(); textureQualityHarmony = null; TextureQualityWithStreaming.Enabled = null;
                Logger.LogError("Texture quality with streaming unavailable; native limit retained: " + error);
            }
        }
        private void StartStreamingBundlePatch()
        {
            try
            {
                bundlePatchHarmony = new Harmony(Info.Metadata.GUID + ".streaming-bundle-patch");
                StreamingBundlePatch.Active = new StreamingBundlePatch(Path.Combine(Application.streamingAssetsPath, "Windows"), StreamingBundlePatch.DefaultFolders,
                    Path.Combine(Paths.BepInExRootPath, "FuckThisLoadingScreen", "StreamableTextureIndex.txt"), text => Logger.LogInfo(text));
                StreamingBundlePatch.Install(bundlePatchHarmony);
                StreamingBundlePatch.Active.StartIndexing();
                Logger.LogInfo("Streamable texture patch armed for " + string.Join(", ", StreamingBundlePatch.DefaultFolders) + "; indexing in the background.");
            }
            catch (Exception error)
            {
                StreamingBundlePatch.Active = null;
                bundlePatchHarmony?.UnpatchSelf(); bundlePatchHarmony = null;
                Logger.LogError("Streamable texture patch unavailable; original bundles retained: " + error);
            }
        }
        private void StartHideoutResearch()
        {
            try
            {
                var specs = HideoutResearch.Resolve(Find);
                hideoutResearch = new HideoutResearch(Path.Combine(Paths.BepInExRootPath, "FuckThisLoadingScreen", "HideoutResearch"), ReadOptions() + "; StopTextureWorkerBeforeTeardown=" + textureWorkerTeardown.Value + "; TextureWorkerFixArmed=" + (TextureWorkerTeardown.Active != null) + "; DecalResultsArmed=" + decalResultsArmed + "; DecalTextureArrayResyncArmed=" + (DecalTextureArrayResync.Active != null) + "; IconMipLifetimesArmed=" + iconMipsArmed + "; MipBudgetAccountingArmed=" + (MipBudgetAccounting.Active != null) + "; MipResidencyPolicyArmed=" + (MipResidencyPolicy.Active != null), Find, text => Logger.LogInfo(text), specs);
                hideoutHarmony = new Harmony(Info.Metadata.GUID + ".hideout-research");
                HideoutResearch.Install(hideoutHarmony, specs);
                DecalMipProbe.Install(hideoutHarmony, Find, new Guid(SupportedModuleId));
                HideoutResearch.Active = hideoutResearch;
                Application.logMessageReceivedThreaded += hideoutResearch.OnLog;
                Logger.LogInfo("Hideout research armed: " + specs.Count + " stage hooks, guarded worker probes and bounded icon/decal texture observations. Raid decal rendering starts/keeps captures alive. 500ms snapshots and 1s writer heartbeat/worker state. Probes do not change mip or loading settings.");
            }
            catch (Exception error)
            {
                Logger.LogError("Hideout research unavailable; normal FTLS remains armed: " + error);
                StopHideoutResearch();
            }
        }
        private void StopHideoutResearch()
        {
            HideoutResearch.Active = null;
            if (hideoutResearch != null) Application.logMessageReceivedThreaded -= hideoutResearch.OnLog;
            hideoutResearch?.Stop(); hideoutResearch = null;
            hideoutHarmony?.UnpatchSelf(); hideoutHarmony = null;
        }
        private void StartTextureWorkerFix()
        {
            try
            {
                textureWorkerHarmony = new Harmony(Info.Metadata.GUID + ".texture-worker-teardown");
                TextureWorkerTeardown.Active = TextureWorkerTeardown.Install(textureWorkerHarmony, Find, new Guid(SupportedModuleId),
                    () => master.Value && textureWorkerTeardown.Value, text => Logger.LogWarning(text));
                Logger.LogInfo("Texture-worker teardown fix armed; current toggle=" + textureWorkerTeardown.Value + ". Native cleanup/restart retained; worker stops before owned tree teardown.");
            }
            catch (Exception error)
            {
                TextureWorkerTeardown.Active = null;
                textureWorkerHarmony?.UnpatchSelf(); textureWorkerHarmony = null;
                Logger.LogError("Texture-worker teardown fix unavailable; native cleanup retained: " + error);
            }
        }
        private void FlushReports()
        {
            while (reports.Count > 0)
            {
                RunReport report = reports.Dequeue();
                string text = "FuckThisLoadingScreen 1.1.0 | EFT MVID=" + SupportedModuleId + " | " + report.Summary;
                Logger.LogInfo(text);
                try
                {
                    string directory = Path.Combine(Paths.BepInExRootPath, "FuckThisLoadingScreen");
                    Directory.CreateDirectory(directory);
                    string prefix = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    File.WriteAllText(prefix + ".txt", text);
                    File.WriteAllText(prefix + "-frame-cap.csv", ExperimentReports.FrameCaps(report.Run));
                    File.WriteAllText(prefix + "-scene-scheduling.csv", ExperimentReports.Schedules(report.Run));
                }
                catch (Exception error) { Logger.LogError("FTLS report export failed: " + error.Message); }
            }
        }
        private void Cleanup()
        {
            botPreloadHarmony?.UnpatchSelf(); botPreloadHarmony = null;
            MipResidencyPolicy.Active?.Stop(); MipResidencyPolicy.Active = null;
            mipResidencyHarmony?.UnpatchSelf(); mipResidencyHarmony = null;
            IconMipLifetime.WhenDrained(StopMipBudgetAccounting);
            if (iconMipHarmony != null)
            {
                Harmony finishing = iconMipHarmony; iconMipHarmony = null;
                IconMipLifetime.WhenDrained(() => finishing.UnpatchSelf());
            }
            iconMipsArmed = false;
            decalResultHarmony?.UnpatchSelf(); decalResultHarmony = null; decalResultsArmed = false;
            StopDecalArrayResync();
            textureQualityHarmony?.UnpatchSelf(); textureQualityHarmony = null; TextureQualityWithStreaming.Enabled = null; TextureQualityWithStreaming.Log = null;
            StreamingBundlePatch.Active?.Stop(); StreamingBundlePatch.Active = null;
            bundlePatchHarmony?.UnpatchSelf(); bundlePatchHarmony = null;
            TextureWorkerTeardown.Active = null;
            textureWorkerHarmony?.UnpatchSelf(); textureWorkerHarmony = null;
            StopHideoutResearch();
            PolicyHooks.Policy = null;
            BoundedScenes.Enabled = null; BoundedScenes.Window = null; BoundedScenes.BeginReport = null; BoundedScenes.CaptureActive = null;
            SpawnZoneHooks.Reset();
            policy?.Stop();
            harmony?.UnpatchSelf(); harmony = null;
            FlushReports();
        }
        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();
        private void StartDecalResultsFix()
        {
            try
            {
                decalResultHarmony = new Harmony(Info.Metadata.GUID + ".decal-results");
                DecalResultSynchronization.Install(decalResultHarmony, Find, new Guid(SupportedModuleId));
                decalResultsArmed = true;
                Logger.LogInfo("Decal results synchronized with a short independent gate; worker sleeps and tree disposal retain their native locks.");
            }
            catch (Exception error)
            {
                decalResultHarmony?.UnpatchSelf(); decalResultHarmony = null;
                Logger.LogError("Decal result fix unavailable; native behavior retained: " + error);
            }
        }
        private void StartDecalArrayResync()
        {
            try
            {
                decalArrayHarmony = new Harmony(Info.Metadata.GUID + ".decal-texture-arrays");
                DecalTextureArrayResync.Active = DecalTextureArrayResync.Install(decalArrayHarmony, Find, new Guid(SupportedModuleId), text => Logger.LogInfo(text));
                Logger.LogInfo("Decal texture-array resync armed; streamed sources copied before mip 0 is resident are recopied once loaded.");
            }
            catch (Exception error)
            {
                StopDecalArrayResync();
                Logger.LogError("Decal texture-array resync unavailable; native arrays retained: " + error);
            }
        }
        private void StopDecalArrayResync()
        {
            DecalTextureArrayResync.Active?.Stop(); DecalTextureArrayResync.Active = null;
            decalArrayHarmony?.UnpatchSelf(); decalArrayHarmony = null;
        }
        private void StartIconMipFix()
        {
            try
            {
                iconMipHarmony = new Harmony(Info.Metadata.GUID + ".icon-mip-lifetimes");
                IconMipLifetime.Warning = text => Logger.LogWarning(text);
                IconMipLifetime.Install(iconMipHarmony, Find, new Guid(SupportedModuleId));
                iconMipsArmed = true;
                Logger.LogInfo("Item/player icon mip lifetimes armed; overlapping renders share texture, memory-budget and camera-bias ownership.");
            }
            catch (Exception error)
            {
                iconMipHarmony?.UnpatchSelf(); iconMipHarmony = null;
                Logger.LogError("Icon mip lifetime fix unavailable; native behavior retained: " + error);
            }
        }
        private void StartMipBudgetAccounting()
        {
            try
            {
                mipBudgetHarmony = new Harmony(Info.Metadata.GUID + ".mip-budget-accounting");
                MipBudgetAccounting.Active = MipBudgetAccounting.Install(mipBudgetHarmony, Find, new Guid(SupportedModuleId));
                mipBudgetPumpFailed = false;
                Logger.LogInfo("Mip budget accounting armed: selected streaming allowance + measured nonstreaming residency + one shared icon allowance. Native mip limits and camera bias retained; live mip-0 acceptance pending.");
            }
            catch (Exception error)
            {
                StopMipBudgetAccounting();
                Logger.LogError("Mip budget accounting unavailable; prior budget policy retained: " + error);
            }
        }
        private void StopMipBudgetAccounting()
        {
            MipBudgetAccounting.Active?.Stop(); MipBudgetAccounting.Active = null;
            mipBudgetHarmony?.UnpatchSelf(); mipBudgetHarmony = null;
        }
        private void StartMipResidencyPolicy()
        {
            try
            {
                mipResidencyHarmony = new Harmony(Info.Metadata.GUID + ".mip-residency");
                MipResidencyPolicy.Active = MipResidencyPolicy.Install(mipResidencyHarmony, Find, new Guid(SupportedModuleId));
                Logger.LogInfo("Experimental unused-mip retention armed; selected streaming memory budget is unchanged. Live performance acceptance pending.");
            }
            catch (Exception error)
            {
                MipResidencyPolicy.Active?.Stop(); MipResidencyPolicy.Active = null;
                mipResidencyHarmony?.UnpatchSelf(); mipResidencyHarmony = null;
                Logger.LogError("Mip residency policy unavailable; native policy retained: " + error);
            }
        }
    }
}
