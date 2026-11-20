using System;
using BepInEx.Configuration;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // All BepInEx settings live here. The persisted section and key are short and stable: never rename them
    // once shipped, or users lose their saved values. Everything the user sees (category, name, order,
    // description) is display metadata supplied through Describe and may change freely.
    internal static class Settings
    {
        private const string GeneralCategory = "1. General";
        private const string LoadingCategory = "2. Loading";
        private const string ScenesCategory = "3. Scenes";
        private const string AudioCategory = "4. Audio";
        private const string MipStreamingCategory = "5. Mip Streaming";
        private const string DebugCategory = "9. Debug";

        private const string MeasureInstructions = "Enable mip streaming in EFT graphics settings, load Streets (the heaviest map), press Measure and look and move around for 60 s, " +
            "including aiming down scopes, then press Apply. Re-measure after changing resolution, upscaler or texture quality.";

        internal static ConfigEntry<bool> Enabled;

        internal static ConfigEntry<LoadingPriority> IntegrationPriority;
        internal static ConfigEntry<bool> ExtendPriorityThroughLootBundles;
        internal static ConfigEntry<bool> LoadingFrameCapOverride;
        internal static ConfigEntry<int> LoadingFrameLimit;
        internal static ConfigEntry<bool> ReduceBotProfilePreloadDelay;
        internal static ConfigEntry<bool> ScopedSpawnZoneLookup;
        internal static ConfigEntry<bool> SkipBuiltInSceneBundleProbes;
        internal static ConfigEntry<bool> StopTextureWorkerBeforeTeardown;

        internal static ConfigEntry<bool> BoundedAsReadySceneActivation;
        internal static ConfigEntry<int> SceneInFlightLimit;

        internal static ConfigEntry<bool> LargerReaderBatches;
        internal static ConfigEntry<int> ReaderBatchSize;

        internal static ConfigEntry<bool> SynchronizeDecalResults;
        internal static ConfigEntry<bool> ResyncDecalTextureArrays;
        internal static ConfigEntry<bool> ReleaseIconMipRequests;
        internal static ConfigEntry<bool> AccountForNonstreamingTextures;
        internal static ConfigEntry<int> StreamingBudgetOverride;
        internal static ConfigEntry<bool> MeasureStreamingBudget;
        internal static ConfigEntry<bool> ApplyMeasuredBudget;

        internal static ConfigEntry<bool> DebugLogging;
        internal static ConfigEntry<bool> WriteLoadingReports;
        internal static ConfigEntry<bool> DumpLoadedTextures;

        // Set by the "Dump now" button; consumed once by Plugin.Update on the main thread.
        internal static bool DumpRequested;

        // Set by the "Measure" button; consumed once by Plugin.Update on the main thread.
        internal static bool MeasureRequested;
        private static GUIStyle _noteStyle;
        internal static ConfigEntry<bool> WriteHideoutResearchReports;
        internal static ConfigEntry<bool> NonCollectingDiagnosticMeasurements;
        internal static ConfigEntry<bool> RetainUnusedStreamedMips;
        internal static ConfigEntry<bool> ParallelSceneLoading;
        internal static ConfigEntry<bool> ApplyTextureQualityWithStreaming;
        internal static ConfigEntry<bool> MarkItemTexturesStreamable;

        // ConfigurationManager lists categories in bind order and sorts settings by descending Order,
        // so bind categories in display order and give the first setting in a category the highest Order.
        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                Describe(GeneralCategory, "Enabled", 100,
                    "Master switch for all loading changes. Loading options are frozen when a loading attempt starts: applies on next raid. " +
                    "Mip-streaming fixes read this at startup: restart required."));

            IntegrationPriority = config.Bind("Loading", "Integration Priority", LoadingPriority.High,
                Describe(LoadingCategory, "Loading priority", 100,
                    "Background loading thread priority during map loading. Native keeps the game's priority; Normal/High apply during LoadMapAndData " +
                    "(and the loot bundle phase when extended) and restore the previous priority afterwards. Applies on next raid."));
            ExtendPriorityThroughLootBundles = config.Bind("Loading", "Extend Priority Through Loot Bundles", true,
                Describe(LoadingCategory, "Extend priority through loot bundles", 90,
                    "Also apply the loading priority to the awaited loot bundle/pool task launched by SpawnLoot. Off limits it to map loading. Applies on next raid."));
            LoadingFrameCapOverride = config.Bind("Loading", "Loading Frame Cap Override", true,
                Describe(LoadingCategory, "Loading frame cap override", 80,
                    "During map loading only, use the loading frame limit with VSync off; the previous values are restored when loading ends or fails. " +
                    "Off keeps the native frame cap. Applies on next raid."));
            LoadingFrameLimit = config.Bind("Loading", "Loading Frame Limit", 240,
                Describe(LoadingCategory, "Loading frame limit", 70,
                    "Render frame limit (FPS) used while the frame cap override is active. Map loading only. Applies on next raid.",
                    new AcceptableValueRange<int>(60, 360)));
            ReduceBotProfilePreloadDelay = config.Bind("Loading", "Reduce Bot Profile Preload Delay", true,
                Describe(LoadingCategory, "Reduce bot profile preload delay", 60,
                    "Shorten the fixed initial bot-profile batch delay at raid start from 500 ms to 1 ms. Off restores 500 ms. Applies on next raid."));
            ScopedSpawnZoneLookup = config.Bind("Loading", "Scoped Spawn Zone Lookup", true,
                Describe(LoadingCategory, "Scoped spawn zone lookup", 50,
                    "Reuse the native ordered BotZone array inside spawn point creation (CreateFromScene/FixSpawns) while loading. " +
                    "Off keeps native discovery. Applies on next raid."));
            SkipBuiltInSceneBundleProbes = config.Bind("Loading", "Skip Built-In Scene Bundle Probes", true,
                Describe(LoadingCategory, "Skip built-in scene bundle probes", 40,
                    "Skip the preliminary bundle probe for a built-in .unity scene that has no manifest, mod, cache, pending or local bundle entry. " +
                    "Unknown paths and real bundle loads stay native. Applies on next raid."));
            StopTextureWorkerBeforeTeardown = config.Bind("Loading", "Stop Texture Worker Before Teardown", true,
                Describe(LoadingCategory, "Stop texture worker before teardown", 30,
                    "Stop the native decal texture worker once before its renderer clears all registered texture trees, avoiding repeated lock starvation " +
                    "with mip streaming. Checked on every teardown; takes effect immediately. Off keeps native behavior."));

            BoundedAsReadySceneActivation = config.Bind("Scenes", "Bounded As-Ready Scene Activation", true,
                Describe(ScenesCategory, "Bounded as-ready scene activation", 100,
                    "Keep at most the in-flight limit of additional scenes loading, with activation allowed as soon as ready. The first scene and child-preset " +
                    "request order stay native; callbacks may fire earlier. Takes precedence over Parallel Scene Loading. Applies on next raid."));
            SceneInFlightLimit = config.Bind("Scenes", "Scene In-Flight Limit", 2,
                Describe(ScenesCategory, "Scene in-flight limit", 90,
                    "Maximum additional scenes in flight per preset in bounded as-ready mode. Applies on next raid.",
                    new AcceptableValueRange<int>(1, 4)));

            LargerReaderBatches = config.Bind("Audio", "Larger Reader Batches", true,
                Describe(AudioCategory, "Larger reader batches", 100,
                    "Use the configured batch size for the audio bake reader during raid map initialization. Off keeps the native batch of 300. Applies on next raid."));
            ReaderBatchSize = config.Bind("Audio", "Reader Batch Size", 1200,
                Describe(AudioCategory, "Reader batch size", 90,
                    "Audio records per coroutine batch (native: 300). Larger batches can lengthen main-thread slices. Applies on next raid.",
                    new AcceptableValueRange<int>(300, 4800)));

            SynchronizeDecalResults = config.Bind("Mip Streaming", "Synchronize Decal Results", true,
                Describe(MipStreamingCategory, "Synchronize decal results", 100,
                    "Guard decal result enumeration, insertion and field writes with a short independent lock (safety fix). Restart required."));
            ResyncDecalTextureArrays = config.Bind("Mip Streaming", "Resync Decal Texture Arrays", true,
                Describe(MipStreamingCategory, "Resync decal texture arrays", 90,
                    "When EFT copies a mip-streamed decal texture into its shared decal array before mip 0 is resident, request mip 0 and recopy that slice " +
                    "once loaded. Fixes decals drawn as solid rectangles. Restart required."));
            ReleaseIconMipRequests = config.Bind("Mip Streaming", "Release Icon Mip Requests", true,
                Describe(MipStreamingCategory, "Release icon mip requests", 80,
                    "Scope item/player icon texture requests, temporary memory policy and camera bias to the native render task, including failure and " +
                    "cancellation. Shared textures stay owned until the last render finishes. Restart required."));
            AccountForNonstreamingTextures = config.Bind("Mip Streaming", "Account For Nonstreaming Textures", true,
                Describe(MipStreamingCategory, "Account for nonstreaming textures", 70,
                    "Treat EFT's map/quality budget as the streamed-texture allowance and add the measured resident nonstreaming texture memory to Unity's " +
                    "total budget, refreshed every 500 ms. Requires Release icon mip requests. Restart required."));
            ApplyTextureQualityWithStreaming = config.Bind("Mip Streaming", "Apply Texture Quality With Streaming", true,
                Describe(MipStreamingCategory, "Apply texture quality with streaming", 60,
                    "Keep EFT's texture-quality mipmap limit (Medium drops one mip, Low two) while mip streaming is on. EFT normally forces the limit to 0 " +
                    "with streaming, so textures that cannot stream load at full size. No effect at High quality. Applied when EFT applies texture quality. " +
                    "Restart required."));
            MarkItemTexturesStreamable = config.Bind("Mip Streaming", "Mark Item Textures Streamable", true,
                Describe(MipStreamingCategory, "Mark item textures streamable", 50,
                    "While EFT loads its item and weapon bundles, present eligible textures (mipmapped, not CPU-readable, not already streaming) as " +
                    "mip-streamable so they stream instead of loading at full size. Bundle files are read through a patching stream; nothing is written to disk " +
                    "except a small offset index, built once in the background after install or a game update. Armed once EFT has loaded its settings, and " +
                    "skipped (no scan, no patch) when mip streaming is off there or on a Fika headless client. Restart required, also after enabling mip streaming."));
            StreamingBudgetOverride = config.Bind("Mip Streaming", "Streaming Budget Override", 0,
                Describe(MipStreamingCategory, "Streaming budget override", 40,
                    "Upper limit in MiB for streamed textures on this GPU, applied on every map. Replaces EFT's per-map streaming budget, higher or lower; " +
                    "0 keeps EFT's value. Measure on Streets so it fits the heaviest map, or use the two buttons below. " +
                    "Requires Account For Nonstreaming Textures: without it this does nothing. Applies within 500 ms, no restart needed.",
                    new AcceptableValueRange<int>(0, 65536)));
            // Persisted keys of the two buttons; their values are unused.
            MeasureStreamingBudget = config.Bind("Mip Streaming", "Measure Streaming Budget", false,
                Describe(MipStreamingCategory, "Measure streaming budget", 30,
                    MeasureInstructions + " Needs a raid with mip streaming on and the GPU video-memory query available. Samples every 250 ms for 60 s " +
                    "and writes the result to BepInEx/config/FuckThisLoadingScreen/dumps/streaming-budget.",
                    drawer: DrawMeasureButton));
            ApplyMeasuredBudget = config.Bind("Mip Streaming", "Apply Measured Budget", false,
                Describe(MipStreamingCategory, "Apply measured budget", 20,
                    "Sets Streaming budget override to the recommendation of the last finished measurement. Needs a finished measurement.",
                    drawer: DrawApplyButton));

            DebugLogging = config.Bind("Debug", "Logging", false,
                Describe(DebugCategory, "Debug logging", 100,
                    "Write extra diagnostics prefixed [debug] to BepInEx/LogOutput.log: feature status, streamable texture index progress and loading report summaries. " +
                    "Rate-limited. Applies immediately.", advanced: true));
            WriteLoadingReports = config.Bind("Debug", "Write Loading Reports", false,
                Describe(DebugCategory, "Write loading reports", 90,
                    "Write per-load option/counter reports (TXT and CSV) to BepInEx/config/FuckThisLoadingScreen/captures/loading-reports. Applies on next raid.",
                    advanced: true));
            // Persisted key kept from the former action toggle; the entry is drawn as a button and its value is unused.
            DumpLoadedTextures = config.Bind("Debug", "Dump Loaded Textures", false,
                Describe(DebugCategory, "Dump loaded textures", 80,
                    "Writes an inventory of every loaded texture (type, size, format, streaming state, mipmap limit, estimated memory) and a grouped " +
                    "summary to BepInEx/config/FuckThisLoadingScreen/dumps/texture-inventory. Scans all textures once; expect a short hitch.",
                    advanced: true, drawer: DrawDumpButton));
            WriteHideoutResearchReports = config.Bind("Debug", "Write Hideout Research Reports", false,
                Describe(DebugCategory, "Write hideout research reports", 70,
                    "Record hideout entry/preloads, scene progress, streaming counters, icon/decal mip probes and a background heartbeat to " +
                    "BepInEx/config/FuckThisLoadingScreen/captures/hideout-research. Observation only, independent of Enabled. Up to 6 captures of 10 minutes each " +
                    "per process; files flush while loading. Restart required.", advanced: true));
            NonCollectingDiagnosticMeasurements = config.Bind("Memory", "Non-Collecting Diagnostic Measurements", true,
                Describe(DebugCategory, "Non-collecting diagnostic measurements", 60,
                    "During raid loading only, use non-collecting memory queries at the two diagnostic call sites inside cleanup. Explicit collections and GC policy " +
                    "stay native. Applies on next raid.", advanced: true));
            RetainUnusedStreamedMips = config.Bind("Experimental", "Retain Unused Streamed Mips", false,
                Describe(DebugCategory, "Retain unused streamed mips (experimental)", 50,
                    "EXPERIMENTAL: let Unity cache unused texture mips within the streaming memory budget instead of EFT's forced immediate discard. " +
                    "Live benefit is unmeasured. Restart required.", advanced: true));
            ParallelSceneLoading = config.Bind("Experimental", "Parallel Scene Loading", false,
                Describe(DebugCategory, "Parallel scene loading (experimental)", 40,
                    "TEST ONLY: use native parallel scheduling within each preset, with as-ready activation so Unity's async queue can progress. Child presets keep " +
                    "native order. Changes activation ordering. Applies on next raid.", advanced: true));

            // A value left true by the former toggle must not linger in the .cfg.
            DumpLoadedTextures.Value = false;
        }

        private static void DrawDumpButton(ConfigEntryBase entry)
        {
            if (GUILayout.Button("Dump loaded textures now", GUILayout.ExpandWidth(true)))
                DumpRequested = true;
        }

        private static GUIStyle NoteStyle()
        {
            // GUI.skin is only valid inside OnGUI; the drawers are the only callers.
            return _noteStyle ?? (_noteStyle = new GUIStyle(GUI.skin.label) { wordWrap = true });
        }

        private static void DrawMeasureButton(ConfigEntryBase entry)
        {
            StreamingBudgetMeasurement measurement = StreamingBudgetMeasurement.Active;
            GUILayout.BeginVertical();
            GUILayout.Label(MeasureInstructions, NoteStyle());
            if (measurement == null)
            {
                GUILayout.Label("Unavailable: the plugin could not bind the EFT members it needs. See BepInEx/LogOutput.log.", NoteStyle());
            }
            else
            {
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && !measurement.Running;
                if (GUILayout.Button(measurement.Running ? "Measuring..." : "Measure streaming budget (60 s)", GUILayout.ExpandWidth(true)))
                    MeasureRequested = true;
                GUI.enabled = enabled;
                if (measurement.Running)
                {
                    GUILayout.Label(measurement.Progress, NoteStyle());
                }
                else
                {
                    if (measurement.Message != null)
                        GUILayout.Label(measurement.Message, NoteStyle());
                    if (measurement.Result != null)
                        GUILayout.Label(measurement.Result.Summary, NoteStyle());
                }
            }

            GUILayout.EndVertical();
        }

        private static void DrawApplyButton(ConfigEntryBase entry)
        {
            StreamingBudgetResult result = StreamingBudgetMeasurement.Active?.Result;
            GUILayout.BeginVertical();
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && result != null;
            if (GUILayout.Button(result == null ? "Apply measured budget" : result.ApplyLabel, GUILayout.ExpandWidth(true)) && result != null)
                StreamingBudgetOverride.Value = result.RecommendedMiB;
            GUI.enabled = enabled;
            if (result == null)
                GUILayout.Label("No measurement yet: press Measure streaming budget first.", NoteStyle());
            else if (StreamingBudgetOverride.Value == result.RecommendedMiB)
                GUILayout.Label("Streaming budget override is set to this value.", NoteStyle());
            if (MipBudgetAccounting.Active == null)
                GUILayout.Label("Account For Nonstreaming Textures is not active, so the override does nothing until it is on (restart required).", NoteStyle());
            GUILayout.EndVertical();
        }

        private static ConfigDescription Describe(string category, string displayName, int order, string description,
            AcceptableValueBase range = null, bool advanced = false, Action<ConfigEntryBase> drawer = null)
        {
            return new ConfigDescription(description, range, new ConfigurationManagerAttributes
            {
                Category = category,
                DispName = displayName,
                Order = order,
                Description = description,
                IsAdvanced = advanced,
                CustomDrawer = drawer,
                HideDefaultButton = drawer != null,
                HideSettingName = drawer != null
            });
        }
    }
}
