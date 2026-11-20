using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    [BepInDependency("com.SPT.custom", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(FikaHeadlessGuid, BepInDependency.DependencyFlags.SoftDependency)] // Load order only, so the presence check sees it.
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string SupportedModuleId = "cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7";
        /// <summary>Fika headless plugin GUID. Fika.Headless forces EFT's MipStreaming setting off when it loads user settings.</summary>
        private const string FikaHeadlessGuid = "com.fika.headless";
        internal static ManualLogSource Log;
        private static readonly LogRateLimiter Limiter = new LogRateLimiter(2000);
        private Harmony _harmony;
        private Harmony _botPreloadHarmony;
        private LoadingPolicy _policy;
        private HideoutResearch _hideoutResearch;
        private Harmony _hideoutHarmony;
        private Harmony _textureWorkerHarmony;
        private Harmony _decalResultHarmony, _iconMipHarmony;
        private bool _decalResultsArmed, _iconMipsArmed;
        private Harmony _mipResidencyHarmony;
        private Harmony _mipBudgetHarmony, _decalArrayHarmony, _textureQualityHarmony, _bundlePatchHarmony;
        private bool _mipBudgetPumpFailed;
        private GameMipStreaming _streamableGate; // Set while the streamable texture patch waits for EFT's loaded settings.
        private sealed class RunReport { internal string Summary; internal LoadingRun Run; }
        private readonly Queue<RunReport> _reports = new Queue<RunReport>();

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            string migrated = MigrateLegacyData();
            bool researchEnabled = Settings.WriteHideoutResearchReports.Value;
            bool fixDecalResults = Settings.SynchronizeDecalResults.Value;
            bool fixDecalArrays = Settings.ResyncDecalTextureArrays.Value;
            bool fixIconMips = Settings.ReleaseIconMipRequests.Value;
            bool fixMipBudget = Settings.AccountForNonstreamingTextures.Value;
            bool markStreamable = Settings.MarkItemTexturesStreamable.Value;
            bool retainMips = Settings.RetainUnusedStreamedMips.Value;
            try
            {
                Type app = Find("EFT.TarkovApplication");
                if (app.Module.ModuleVersionId != new Guid(SupportedModuleId))
                    throw new NotSupportedException("Unsupported EFT module MVID: " + app.Module.ModuleVersionId);
                _policy = new LoadingPolicy(ReadOptions, () => (int)Application.backgroundLoadingPriority,
                    value => Application.backgroundLoadingPriority = (UnityEngine.ThreadPriority)value,
                    mode => (int)(mode == LoadingPriority.High ? UnityEngine.ThreadPriority.High : UnityEngine.ThreadPriority.Normal),
                    (text, run) => { if (_reports.Count < 8) _reports.Enqueue(new RunReport { Summary = text, Run = run }); },
                    frameCap: new LoadingFrameCap(() => Application.targetFrameRate, value => Application.targetFrameRate = value,
                        () => QualitySettings.vSyncCount, value => QualitySettings.vSyncCount = value));
                _harmony = new Harmony(PluginInfo.Guid);
                foreach (var entry in new[] { ("LocalGameMatching", 2), ("NetworkGameMatching", 2), ("GameReconnect", 5) })
                    Patch(One(app, entry.Item1, entry.Item2), nameof(PolicyHooks.RootPrefix), nameof(PolicyHooks.RootPostfix), nameof(PolicyHooks.RootFinalizer));
                Patch(One(app, "LoadMapAndData", 4), nameof(PolicyHooks.MapPrefix), nameof(PolicyHooks.MapPostfix), nameof(PolicyHooks.MapFinalizer));
                InstallLootBundlePriority(_harmony, Find);
                Type metrics = Find("EFT.Utilities.ClientMetricsEvents");
                _harmony.Patch(metrics.GetConstructors().Single(), postfix: Hook(nameof(PolicyHooks.MetricsCreated), Priority.Last));
                _harmony.Patch(One(metrics, "SetGameStarted", 0), postfix: Hook(nameof(PolicyHooks.GameStarted), Priority.Last));

                MethodInfo audioGetter = One(Find("Audio.SpatialSystem.AudioBakeDataReader"), "get_HasReachedMaxIterationsPerFrame", 0);
                _harmony.Patch(audioGetter, transpiler: Hook(nameof(PolicyHooks.AudioTranspiler), Priority.Normal));
                Type memory = Find("InGameMemoryManagement");
                PolicyHooks.NativeMeasureMethod = One(memory, "GetTotalAllocatedMemoryGB", 0);
                PolicyHooks.NativeMeasure = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), PolicyHooks.NativeMeasureMethod);
                _harmony.Patch(One(memory, "Collect", 5), transpiler: Hook(nameof(PolicyHooks.MemoryTranspiler), Priority.Normal));

                Type presets = Find("EFT.LoadScenesFromPresetOperation");
                MethodInfo[] execute = AccessTools.GetDeclaredMethods(presets).Where(m => m.Name == "Execute" && m.GetParameters().Length == 7).ToArray();
                if (execute.Length != 2 || execute.Any(m => m.GetParameters()[3].Name != "loadInParallelMode" || m.GetParameters()[4].Name != "allowSceneActivation"))
                    throw new MissingMethodException("Scene preset parallel flag ABI mismatch");
                foreach (MethodInfo method in execute)
                    _harmony.Patch(method, prefix: Hook(nameof(PolicyHooks.PresetPrefix), Priority.Last));
                Patch(One(presets, "ParallelLoadCoroutine", 2), nameof(PolicyHooks.SchedulingPrefix), nameof(PolicyHooks.SchedulingPostfix), nameof(PolicyHooks.SchedulingFinalizer));
                MethodInfo loadScene = One(Find("EFT.AssetsManager.AssetsManager"), "LoadScene", 5);
                if (loadScene.GetParameters()[3].Name != "allowSceneActivation")
                    throw new MissingMethodException("Scene activation ABI mismatch");
                _harmony.Patch(loadScene, prefix: Hook(nameof(PolicyHooks.ScenePrefix), Priority.Last));
                InstallSpawnZoneLookup();
                SceneBundleHooks.Install(_harmony, Find);
                BoundedScenes.Enabled = () => _policy?.BoundedSceneRun != null;
                BoundedScenes.Window = () => _policy?.BoundedSceneRun?.Options.SceneWindow ?? 2;
                BoundedScenes.CaptureActive = () => _policy?.CaptureSceneActivity();
                BoundedScenes.BeginReport = () => _policy?.BeginSceneReport();
                BoundedScenes.Install(_harmony, Find);
                PolicyHooks.Policy = _policy; // Arm only once every guarded patch has succeeded.
                StartBotPreloadDelayFix();
                LogDebug("Armed; native comparison modes retained. Options are frozen per attempt. " + ReadOptions());
                TryStart("Texture-worker teardown fix", StartTextureWorkerFix, StopTextureWorkerFix);
                if (Settings.Enabled.Value && fixDecalResults)
                    TryStart("Decal result synchronization", StartDecalResultsFix, StopDecalResultsFix);
                if (Settings.Enabled.Value && fixDecalArrays)
                    TryStart("Decal texture-array resync", StartDecalArrayResync, StopDecalArrayResync);
                if (Settings.Enabled.Value && fixIconMips)
                    TryStart("Icon mip lifetimes", StartIconMipFix, AbandonIconMipFix);
                if (Settings.Enabled.Value && fixMipBudget && _iconMipsArmed)
                    TryStart("Mip budget accounting", StartMipBudgetAccounting, StopMipBudgetAccounting);
                else if (Settings.Enabled.Value && fixMipBudget)
                    Logger.LogWarning("Mip budget accounting requires the icon lifetime adapter; accounting remains off.");
                if (Settings.Enabled.Value && retainMips)
                    TryStart("Unused-mip retention", StartMipResidencyPolicy, StopMipResidencyPolicy);
                if (Settings.Enabled.Value)
                    TryStart("Texture quality with streaming", StartTextureQualityWithStreaming, StopTextureQualityWithStreaming);
                if (Settings.Enabled.Value && markStreamable)
                {
                    if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FikaHeadlessGuid))
                        LogDebug("Streamable texture patch not armed and item bundles not scanned: Fika headless client.");
                    else
                        TryStart("Streamable texture patch gate", () => _streamableGate = GameMipStreaming.Bind(Find), () => _streamableGate = null);
                }
                TryStart("Streaming budget measurement", StartStreamingBudgetMeasurement, StopStreamingBudgetMeasurement);
                if (researchEnabled)
                    TryStart("Hideout research", StartHideoutResearch, StopHideoutResearch);
                Log.LogInfo(PluginInfo.Name + " " + PluginInfo.Version + " loaded." + migrated);
            }
            catch (Exception error)
            {
                Logger.LogError("FTLS refused incomplete/unsupported patches: " + error);
                Cleanup();
                enabled = false;
            }
        }

        /// <summary>Moves data written by 1.0.x/1.1.0 builds into the config/&lt;kind&gt; layout; returns a note for the init line, or an empty string.</summary>
        private static string MigrateLegacyData()
        {
            try
            {
                int moved = LegacyDataMigration.Run(ModPaths.LegacyRoot,
                    ModPaths.Locate(ModPaths.Captures, ModPaths.LoadingReportsFolder),
                    ModPaths.Locate(ModPaths.Captures, ModPaths.HideoutResearchFolder),
                    ModPaths.Locate(ModPaths.Dumps, ModPaths.TextureInventoryFolder),
                    Path.Combine(ModPaths.Locate(ModPaths.Cache), ModPaths.StreamableTextureIndexFile));
                return moved == 0 ? "" : " Moved " + moved + " file(s)/folder(s) from BepInEx/" + PluginInfo.ModName + " to BepInEx/config/" + PluginInfo.ModName + ".";
            }
            catch (Exception error)
            {
                Log.LogWarning("Could not move data from the old BepInEx/" + PluginInfo.ModName + " folder; it stays where it is: " + error.Message);
                return "";
            }
        }

        /// <summary>
        /// Arms the streamable texture patch, and with it the item bundle scan, once EFT has loaded its settings and only when mip
        /// streaming is on: streamable flags have no effect otherwise. Decided once per process, so enabling mip streaming needs a restart.
        /// </summary>
        private void PumpStreamableGate()
        {
            if (_streamableGate == null || !_streamableGate.Loaded())
                return;
            GameMipStreaming gate = _streamableGate;
            _streamableGate = null;
            bool enabled;
            try
            { enabled = gate.Enabled(); }
            catch (Exception error)
            {
                Logger.LogError("Streamable texture patch stays off: EFT's mip streaming setting could not be read: " + error);
                return;
            }
            if (enabled)
                TryStart("Streamable texture patch", StartStreamingBundlePatch, StopStreamingBundlePatch);
            else
                LogDebug("Streamable texture patch not armed and item bundles not scanned: mip streaming is off in EFT's settings. Restart after enabling it.");
        }

        /// <summary>Runs one feature's start-up; on failure runs <paramref name="cleanup"/>, logs one error and leaves the feature off.</summary>
        internal static bool TryStart(string feature, Action start, Action cleanup = null)
        {
            try
            {
                start();
                return true;
            }
            catch (Exception error)
            {
                try
                { cleanup?.Invoke(); }
                catch (Exception cleanupError) { Log.LogError("Feature '" + feature + "' cleanup failed: " + cleanupError); }
                Log.LogError("Feature '" + feature + "' failed to start and is disabled: " + error);
                return false;
            }
        }

        /// <summary>Debug output only, gated on Debug | Logging. Written at Info level because BepInEx's disk log drops Debug.</summary>
        internal static void LogDebug(string message)
        {
            if (Settings.DebugLogging != null && Settings.DebugLogging.Value)
                Log.LogInfo("[debug] " + message);
        }

        /// <summary>Like <see cref="LogDebug"/> for messages that can repeat: at most one per key every two seconds, with a count of the dropped ones.</summary>
        internal static void LogDebugLimited(string key, string message)
        {
            if (Settings.DebugLogging == null || !Settings.DebugLogging.Value)
                return;
            long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            if (!Limiter.TryAcquire(key, nowMs, out int suppressed))
                return;
            Log.LogInfo("[debug] " + message + (suppressed > 0 ? " (+" + suppressed + " similar message(s) suppressed)" : ""));
        }

        /// <summary>Warning that can repeat (for example once per bundle): at most one per key every two seconds, with a count of the dropped ones.</summary>
        internal static void LogWarningLimited(string key, string message)
        {
            long nowMs = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            if (!Limiter.TryAcquire("warn:" + key, nowMs, out int suppressed))
                return;
            Log.LogWarning(message + (suppressed > 0 ? " (+" + suppressed + " similar message(s) suppressed)" : ""));
        }

        private static Options ReadOptions() => new Options
        {
            Enabled = Settings.Enabled.Value,
            AudioBatching = Settings.LargerReaderBatches.Value,
            AudioBatchSize = Math.Max(300, Math.Min(4800, Settings.ReaderBatchSize.Value)),
            NonCollectingMeasurements = Settings.NonCollectingDiagnosticMeasurements.Value,
            Priority = Settings.IntegrationPriority.Value,
            ParallelScenes = Settings.ParallelSceneLoading.Value,
            ScopedSpawnZones = Settings.ScopedSpawnZoneLookup.Value,
            SkipBuiltInSceneProbes = Settings.SkipBuiltInSceneBundleProbes.Value,
            LootBundlePriority = Settings.ExtendPriorityThroughLootBundles.Value,
            FrameCapOverride = Settings.LoadingFrameCapOverride.Value,
            LoadingFrameLimit = Settings.LoadingFrameLimit.Value,
            BoundedScenes = Settings.BoundedAsReadySceneActivation.Value,
            SceneWindow = Settings.SceneInFlightLimit.Value,
            DebugReports = Settings.WriteLoadingReports.Value,
            ReduceBotProfilePreloadDelay = Settings.ReduceBotProfilePreloadDelay.Value
        };
        private void StartBotPreloadDelayFix()
        {
            try
            {
                _botPreloadHarmony = new Harmony(PluginInfo.Guid + ".bot-preload-delay");
                BotProfilePreloadDelay.Install(_botPreloadHarmony, Find);
                LogDebug("Initial bot profile preload delay adapter armed; comparison toggle retained.");
            }
            catch (Exception error)
            {
                _botPreloadHarmony?.UnpatchSelf();
                _botPreloadHarmony = null;
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
            _harmony.Patch(create, SpawnHook(nameof(SpawnZoneHooks.CollectionPrefix), Priority.Last), SpawnHook(nameof(SpawnZoneHooks.CollectionPostfix), Priority.First),
                finalizer: SpawnHook(nameof(SpawnZoneHooks.CollectionFinalizer), Priority.First));
            _harmony.Patch(fix, SpawnHook(nameof(SpawnZoneHooks.FixPrefix), Priority.Last), SpawnHook(nameof(SpawnZoneHooks.FixPostfix), Priority.First),
                finalizer: SpawnHook(nameof(SpawnZoneHooks.FixFinalizer), Priority.First));
            _harmony.Patch(markerCreate, transpiler: SpawnHook(nameof(SpawnZoneHooks.MarkerTranspiler), Priority.Normal));
        }
        private static HarmonyMethod SpawnHook(string name, int priority) => new HarmonyMethod(typeof(SpawnZoneHooks), name) { priority = priority };
        private static Type Find(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);
        private static MethodInfo One(Type type, string name, int count) => AccessTools.GetDeclaredMethods(type)
            .Single(m => m.Name == name && m.GetParameters().Length == count && !m.IsAbstract);
        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(typeof(PolicyHooks), name) { priority = priority };
        private void Patch(MethodInfo method, string prefix, string postfix, string finalizer)
        {
            if (!typeof(Task).IsAssignableFrom(method.ReturnType))
                throw new NotSupportedException(method.ToString());
            _harmony.Patch(method, Hook(prefix, Priority.First), Hook(postfix, Priority.Last), finalizer: Hook(finalizer, Priority.Last));
        }
        private void Update()
        {
            _policy?.Pump();
            try
            { if (!_mipBudgetPumpFailed) MipBudgetAccounting.Active?.Pump(); }
            catch (Exception error)
            {
                _mipBudgetPumpFailed = true;
                Logger.LogError("Mip budget accounting stopped polling; cleanup waits for pending icon renders: " + error);
                IconMipLifetime.WhenDrained(StopMipBudgetAccounting);
            }
            try
            { DecalTextureArrayResync.Active?.Pump(); }
            catch (Exception error) { Logger.LogError("Decal texture-array resync stopped after an error: " + error); StopDecalArrayResync(); }
            try
            { _hideoutResearch?.Pump(); }
            catch (Exception error) { Logger.LogError("Hideout research stopped after an observer error: " + error); StopHideoutResearch(); }
            if (_policy != null && !_policy.IsLoading)
                FlushReports();
            if (Settings.DumpRequested)
                DumpTextures();
            PumpStreamingBudgetMeasurement();
            PumpStreamableGate();
            try
            { StreamingBundlePatch.Active?.Sweep(Time.realtimeSinceStartupAsDouble * 1000); }
            catch (Exception error) { Logger.LogError("Streamable texture patch stream cleanup failed: " + error); }
        }
        private void DumpTextures()
        {
            Settings.DumpRequested = false;
            try
            { LogDebug(TextureInventory.Dump(ModPaths.Locate(ModPaths.Dumps, ModPaths.TextureInventoryFolder), UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)); }
            catch (Exception error) { Logger.LogError("Texture inventory failed: " + error); }
        }
        private void StartStreamingBudgetMeasurement()
        {
            var game = StreamingBudgetGame.Bind(Find, new Guid(SupportedModuleId));
            StreamingBudgetMeasurement.Active = new StreamingBudgetMeasurement(() => Time.realtimeSinceStartupAsDouble * 1000, game.InRaid,
                () => QualitySettings.streamingMipmapsActive, game.TryRead, game.MapBudget, game.MapName);
            LogDebug("Streaming budget measurement ready; press Measure streaming budget in a raid.");
        }
        private void StopStreamingBudgetMeasurement()
        {
            StreamingBudgetMeasurement.Active = null;
            Settings.MeasureRequested = false;
        }
        private void PumpStreamingBudgetMeasurement()
        {
            StreamingBudgetMeasurement measurement = StreamingBudgetMeasurement.Active;
            if (measurement == null)
                return;
            StreamingBudgetResult finished;
            try
            {
                if (Settings.MeasureRequested)
                {
                    Settings.MeasureRequested = false;
                    measurement.Start();
                }
                finished = measurement.Pump();
            }
            catch (Exception error)
            {
                Logger.LogError("Streaming budget measurement stopped after an error: " + error);
                StopStreamingBudgetMeasurement();
                return;
            }
            if (finished == null)
                return;
            try
            {
                string directory = ModPaths.Dir(ModPaths.Dumps, ModPaths.StreamingBudgetFolder);
                string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + string.Join("_", finished.MapName.Split(Path.GetInvalidFileNameChars())) + ".txt";
                string header = PluginInfo.Name + " " + PluginInfo.Version + " | EFT MVID=" + SupportedModuleId + " | " + SystemInfo.graphicsDeviceName +
                    " | graphicsMemorySize=" + SystemInfo.graphicsMemorySize + " MiB | override=" + Settings.StreamingBudgetOverride.Value + " MiB";
                File.WriteAllText(Path.Combine(directory, name), finished.Report(header));
                LogDebug("Streaming budget measured: " + finished.Summary.Replace("\n", " | ") + " | file " + name);
            }
            catch (Exception error) { Logger.LogError("Streaming budget result export failed: " + error.Message); }
        }
        private void StartTextureQualityWithStreaming()
        {
            _textureQualityHarmony = new Harmony(PluginInfo.Guid + ".texture-quality-streaming");
            TextureQualityWithStreaming.Log = text => LogDebugLimited("texture-quality", text);
            TextureQualityWithStreaming.Install(_textureQualityHarmony, Find, new Guid(SupportedModuleId), () => Settings.Enabled.Value && Settings.ApplyTextureQualityWithStreaming.Value);
            if (Settings.ApplyTextureQualityWithStreaming.Value)
                LogDebug("Texture quality mipmap limit is kept with mip streaming.");
        }
        private void StopTextureQualityWithStreaming()
        {
            _textureQualityHarmony?.UnpatchSelf();
            _textureQualityHarmony = null;
            TextureQualityWithStreaming.Enabled = null;
            TextureQualityWithStreaming.Log = null;
        }
        private void StartStreamingBundlePatch()
        {
            _bundlePatchHarmony = new Harmony(PluginInfo.Guid + ".streaming-bundle-patch");
            StreamingBundlePatch.Active = new StreamingBundlePatch(Path.Combine(Application.streamingAssetsPath, "Windows"), StreamingBundlePatch.DefaultFolders,
                Path.Combine(ModPaths.Locate(ModPaths.Cache), ModPaths.StreamableTextureIndexFile), text => LogDebug(text), text => LogWarningLimited("bundle-patch", text));
            StreamingBundlePatch.Install(_bundlePatchHarmony);
            StreamingBundlePatch.Active.StartIndexing();
            LogDebug("Streamable texture patch armed for " + string.Join(", ", StreamingBundlePatch.DefaultFolders) + "; indexing in the background.");
        }
        private void StopStreamingBundlePatch()
        {
            StreamingBundlePatch.Active?.Stop();
            StreamingBundlePatch.Active = null;
            _bundlePatchHarmony?.UnpatchSelf();
            _bundlePatchHarmony = null;
        }
        private void StartHideoutResearch()
        {
            var specs = HideoutResearch.Resolve(Find);
            _hideoutResearch = new HideoutResearch(ModPaths.Locate(ModPaths.Captures, ModPaths.HideoutResearchFolder), ReadOptions() + "; StopTextureWorkerBeforeTeardown=" + Settings.StopTextureWorkerBeforeTeardown.Value + "; TextureWorkerFixArmed=" + (TextureWorkerTeardown.Active != null) + "; DecalResultsArmed=" + _decalResultsArmed + "; DecalTextureArrayResyncArmed=" + (DecalTextureArrayResync.Active != null) + "; IconMipLifetimesArmed=" + _iconMipsArmed + "; MipBudgetAccountingArmed=" + (MipBudgetAccounting.Active != null) + "; MipResidencyPolicyArmed=" + (MipResidencyPolicy.Active != null), Find, text => LogDebug(text), specs, text => Logger.LogWarning(text));
            _hideoutHarmony = new Harmony(PluginInfo.Guid + ".hideout-research");
            HideoutResearch.Install(_hideoutHarmony, specs);
            DecalMipProbe.Install(_hideoutHarmony, Find, new Guid(SupportedModuleId));
            HideoutResearch.Active = _hideoutResearch;
            Application.logMessageReceivedThreaded += _hideoutResearch.OnLog;
            LogDebug("Hideout research armed: " + specs.Count + " stage hooks, guarded worker probes and bounded icon/decal texture observations. Raid decal rendering starts/keeps captures alive. 500ms snapshots and 1s writer heartbeat/worker state. Probes do not change mip or loading settings.");
        }
        private void StopHideoutResearch()
        {
            HideoutResearch.Active = null;
            if (_hideoutResearch != null)
                Application.logMessageReceivedThreaded -= _hideoutResearch.OnLog;
            _hideoutResearch?.Stop();
            _hideoutResearch = null;
            _hideoutHarmony?.UnpatchSelf();
            _hideoutHarmony = null;
        }
        private void StartTextureWorkerFix()
        {
            _textureWorkerHarmony = new Harmony(PluginInfo.Guid + ".texture-worker-teardown");
            TextureWorkerTeardown.Active = TextureWorkerTeardown.Install(_textureWorkerHarmony, Find, new Guid(SupportedModuleId),
                () => Settings.Enabled.Value && Settings.StopTextureWorkerBeforeTeardown.Value, text => Logger.LogWarning(text));
            LogDebug("Texture-worker teardown fix armed; current toggle=" + Settings.StopTextureWorkerBeforeTeardown.Value + ". Native cleanup/restart retained; worker stops before owned tree teardown.");
        }
        private void StopTextureWorkerFix()
        {
            TextureWorkerTeardown.Active = null;
            _textureWorkerHarmony?.UnpatchSelf();
            _textureWorkerHarmony = null;
        }
        private void FlushReports()
        {
            while (_reports.Count > 0)
            {
                RunReport report = _reports.Dequeue();
                string text = PluginInfo.Name + " " + PluginInfo.Version + " | EFT MVID=" + SupportedModuleId + " | " + report.Summary;
                LogDebug(text);
                try
                {
                    string directory = ModPaths.Dir(ModPaths.Captures, ModPaths.LoadingReportsFolder);
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
            _botPreloadHarmony?.UnpatchSelf();
            _botPreloadHarmony = null;
            StopMipResidencyPolicy();
            IconMipLifetime.WhenDrained(StopMipBudgetAccounting);
            if (_iconMipHarmony != null)
            {
                Harmony finishing = _iconMipHarmony;
                _iconMipHarmony = null;
                IconMipLifetime.WhenDrained(() => finishing.UnpatchSelf());
            }
            _iconMipsArmed = false;
            StopDecalResultsFix();
            StopDecalArrayResync();
            StopTextureQualityWithStreaming();
            _streamableGate = null;
            StopStreamingBundlePatch();
            StopStreamingBudgetMeasurement();
            StopTextureWorkerFix();
            StopHideoutResearch();
            PolicyHooks.Policy = null;
            BoundedScenes.Enabled = null;
            BoundedScenes.Window = null;
            BoundedScenes.BeginReport = null;
            BoundedScenes.CaptureActive = null;
            SpawnZoneHooks.Reset();
            _policy?.Stop();
            _harmony?.UnpatchSelf();
            _harmony = null;
            FlushReports();
        }
        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();
        private void StartDecalResultsFix()
        {
            _decalResultHarmony = new Harmony(PluginInfo.Guid + ".decal-results");
            DecalResultSynchronization.Install(_decalResultHarmony, Find, new Guid(SupportedModuleId));
            _decalResultsArmed = true;
            LogDebug("Decal results synchronized with a short independent gate; worker sleeps and tree disposal retain their native locks.");
        }
        private void StopDecalResultsFix()
        {
            _decalResultHarmony?.UnpatchSelf();
            _decalResultHarmony = null;
            _decalResultsArmed = false;
        }
        private void StartDecalArrayResync()
        {
            _decalArrayHarmony = new Harmony(PluginInfo.Guid + ".decal-texture-arrays");
            DecalTextureArrayResync.Active = DecalTextureArrayResync.Install(_decalArrayHarmony, Find, new Guid(SupportedModuleId), text => LogDebugLimited("decal-resync", text));
            LogDebug("Decal texture-array resync armed; streamed sources copied before mip 0 is resident are recopied once loaded.");
        }
        private void StopDecalArrayResync()
        {
            DecalTextureArrayResync.Active?.Stop();
            DecalTextureArrayResync.Active = null;
            _decalArrayHarmony?.UnpatchSelf();
            _decalArrayHarmony = null;
        }
        private void StartIconMipFix()
        {
            _iconMipHarmony = new Harmony(PluginInfo.Guid + ".icon-mip-lifetimes");
            IconMipLifetime.Warning = text => Logger.LogWarning(text);
            IconMipLifetime.Install(_iconMipHarmony, Find, new Guid(SupportedModuleId));
            _iconMipsArmed = true;
            LogDebug("Item/player icon mip lifetimes armed; overlapping renders share texture, memory-budget and camera-bias ownership.");
        }
        private void AbandonIconMipFix()
        {
            // Nothing was armed, so no render can still own the patches and they are removed immediately.
            _iconMipHarmony?.UnpatchSelf();
            _iconMipHarmony = null;
        }
        private void StartMipBudgetAccounting()
        {
            _mipBudgetHarmony = new Harmony(PluginInfo.Guid + ".mip-budget-accounting");
            MipBudgetAccounting.Active = MipBudgetAccounting.Install(_mipBudgetHarmony, Find, new Guid(SupportedModuleId), () => Settings.StreamingBudgetOverride.Value);
            _mipBudgetPumpFailed = false;
            LogDebug("Mip budget accounting armed: selected streaming allowance + measured nonstreaming residency + one shared icon allowance. Native mip limits and camera bias retained; live mip-0 acceptance pending.");
        }
        private void StopMipBudgetAccounting()
        {
            MipBudgetAccounting.Active?.Stop();
            MipBudgetAccounting.Active = null;
            _mipBudgetHarmony?.UnpatchSelf();
            _mipBudgetHarmony = null;
        }
        private void StartMipResidencyPolicy()
        {
            _mipResidencyHarmony = new Harmony(PluginInfo.Guid + ".mip-residency");
            MipResidencyPolicy.Active = MipResidencyPolicy.Install(_mipResidencyHarmony, Find, new Guid(SupportedModuleId));
            LogDebug("Experimental unused-mip retention armed; selected streaming memory budget is unchanged. Live performance acceptance pending.");
        }
        private void StopMipResidencyPolicy()
        {
            MipResidencyPolicy.Active?.Stop();
            MipResidencyPolicy.Active = null;
            _mipResidencyHarmony?.UnpatchSelf();
            _mipResidencyHarmony = null;
        }
    }
}
