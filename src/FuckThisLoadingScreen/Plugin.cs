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
    [BepInPlugin("com.rootdarkarchon.fuckthisloadingscreen", "FuckThisLoadingScreen", "1.0.1")]
    [BepInDependency("com.SPT.custom", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.rootdarkarchon.ftls.levelstagetimer", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string SupportedModuleId = "cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7";
        private Harmony harmony;
        private LoadingPolicy policy;
        private sealed class RunReport { internal string Summary; internal LoadingRun Run; }
        private readonly Queue<RunReport> reports = new Queue<RunReport>();
        private ConfigEntry<bool> master, audio, measurements, parallel, spawnZones, sceneProbes, lootPriority, frameOverride, boundedScenes, debugReports;
        private ConfigEntry<int> batch, frameLimit, sceneWindow;
        private ConfigEntry<LoadingPriority> priority;

        private void Awake()
        {
            master = Config.Bind("General", "Enabled", true, "Master switch. Options are frozen at the start of each loading attempt; changes apply to the next attempt.");
            debugReports = Config.Bind("Debug", "Write Loading Reports", false, "Write per-load option/counter summaries to the BepInEx log and TXT/CSV reports to BepInEx/FuckThisLoadingScreen. Off by default. Options freeze per attempt; changes apply to the next loading attempt.");
            audio = Config.Bind("Audio", "Larger Reader Batches", true, "Use the configured audio bake reader batch during raid map initialization. Off retains native batches of 300.");
            batch = Config.Bind("Audio", "Reader Batch Size", 1200,
                new ConfigDescription("Audio records per coroutine batch. 300 is native; 1200 is the first test setting. Larger batches can increase main-thread slices.", new AcceptableValueRange<int>(300, 4800)));
            measurements = Config.Bind("Memory", "Non-Collecting Diagnostic Measurements", true, "During raid loading only, use non-collecting memory queries at the two diagnostic call sites inside cleanup. Explicit collections and GC policy remain native.");
            priority = Config.Bind("Loading", "Integration Priority", LoadingPriority.High, "Native preserves the existing priority. Normal/High apply during LoadMapAndData and optionally the awaited loot bundle/pool phase; restore the previous priority on completion, cancellation, failure or shutdown.");
            lootPriority = Config.Bind("Loading", "Extend Priority Through Loot Bundles", true, "Apply the configured integration priority to the awaited bundle/pool Task launched by SpawnLoot. Does not extend map-only audio or scene controls. Off retains the previous map-only priority scope. Retention/yield budgets remain native.");
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
                Logger.LogInfo("FTLS armed; native comparison modes retained. Options are frozen per attempt. " + ReadOptions());
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
            FrameCapOverride = frameOverride.Value, LoadingFrameLimit = frameLimit.Value, BoundedScenes = boundedScenes.Value, SceneWindow = sceneWindow.Value, DebugReports = debugReports.Value };
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
            if (policy != null && !policy.IsLoading) FlushReports();
        }
        private void FlushReports()
        {
            while (reports.Count > 0)
            {
                RunReport report = reports.Dequeue();
                string text = "FuckThisLoadingScreen 1.0.1 | EFT MVID=" + SupportedModuleId + " | " + report.Summary;
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
            PolicyHooks.Policy = null;
            BoundedScenes.Enabled = null; BoundedScenes.Window = null; BoundedScenes.BeginReport = null; BoundedScenes.CaptureActive = null;
            SpawnZoneHooks.Reset();
            policy?.Stop();
            harmony?.UnpatchSelf(); harmony = null;
            FlushReports();
        }
        private void OnDisable() => Cleanup();
        private void OnDestroy() => Cleanup();
    }
}
