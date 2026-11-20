# Patch targets

Every Harmony patch, reflected member and IL edit that FTLS applies, grouped by feature. Target build: EFT 0.16.9.40743 (module `cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7`, pinned as `SupportedModuleId` in `Plugin.cs`). Names are `Namespace.Type` as found in `Assembly-CSharp`. Lookups by string (`find("...")`, `AccessTools.Field`) fail at startup with a logged error if the target is missing, so a stale name shows up as a skipped feature rather than a crash.

Patch kinds: `prefix`, `postfix`, `finalizer`, `transpiler`. Priorities are given where the code sets them; the default is `Normal`.

Update this file in the same change as any patch, reflection lookup or IL edit.

## Startup and raid flow (`Plugin.cs`)

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `EFT.TarkovApplication` | `LocalGameMatching` (2 params) | prefix, postfix, finalizer | Loading scope, frame cap, priority |
| `EFT.TarkovApplication` | `NetworkGameMatching` (2 params) | prefix, postfix, finalizer | Loading scope |
| `EFT.TarkovApplication` | `GameReconnect` (5 params) | prefix, postfix, finalizer | Loading scope |
| `EFT.TarkovApplication` | `LoadMapAndData` (4 params) | prefix, postfix, finalizer | Map load scope |
| `EFT.Utilities.ClientMetricsEvents` | constructor | postfix | Metrics hook (`MetricsCreated`) |
| `EFT.Utilities.ClientMetricsEvents` | `SetGameStarted` (0 params) | postfix | Game-started marker (`GameStarted`) |

## Loading priority and scheduling

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `EFT.LoadScenesFromPresetOperation` | `Execute` (two overloads, 7 params) | prefix | Bounded scene activation (`PresetPrefix`) |
| `EFT.LoadScenesFromPresetOperation` | `ParallelLoadCoroutine` (2 params) | prefix, postfix, finalizer | Scheduling (`Scheduling`) |
| `EFT.AssetsManager.AssetsManager` | `LoadScene` (5 params) | prefix | Scene load (`ScenePrefix`) |
| `EFT.BaseLocalGame<EftGamePlayerOwner>` | `SpawnLoot` (1 param, `Task`) | prefix, postfix, finalizer | Loot launch priority (`LootLaunch`) |
| `EFT.ObjectsFactory` | `LoadBundlesAndCreatePools` (6 params; first is `pools`) | prefix, postfix, finalizer | Loot bundle priority (`LootBundles`) |

## Bot profile preload (`BotProfilePreloadDelay`)

Reduces the fixed 500 ms initial batch waits in the bot profile loader to 1 ms.

- Reflects `BotProfileClient.LoadBots`, its state machine `MoveNext`, and private fields `_startProfilesLoaded` and `_backupLoader`.
- Transpiler rewrites the `Task.Delay(int)` call in the state machine. Labels on `CodeInstruction` are preserved.
- Counters: `BotPreloadDelaysReduced`, `BotPreloadDelayNominalSavedMs` (see CHANGELOG 1.1.0).

## Audio (`PolicyHooks`, `AudioTranspiler`)

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `Audio.SpatialSystem.AudioBakeDataReader` | `get_HasReachedMaxIterationsPerFrame` (0 params) | transpiler (`AudioTranspiler`) | Larger reader batches; the batch size is read from `PolicyHooks.EffectiveAudioBatchSize` |

## Memory diagnostics (`InGameMemoryManagement`, `MemoryTranspiler`, `PolicyHooks.MeasureMemory`)

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `InGameMemoryManagement` | `GetTotalAllocatedMemoryGB` (0 params) | native measure | Non-collecting diagnostic query |
| `InGameMemoryManagement` | `Collect` (5 params) | transpiler (`MemoryTranspiler`) | Non-collecting diagnostic query; the native call to `MeasureMemory` replaces the collecting one |

## Scene bundles and bounded scenes

- `SceneBundleHooks`:
  - Reflects `EFT.AssetsManager.BundlesManager`: `LoadBundleAsync(string, bool)`, properties `DownloadingUrl` and `Manifest`, fields `_bundles` and `_loadBundleOperations`. Also reflects `SPT.Custom.Utils.BundleManager.Bundles`.
  - Reflects `EFT.AssetsManager.AssetsManager+LoadSceneOperation`: declared method `Coroutine` (6 params) and its `MoveNext` state machine (transpiler at `Priority.Last`). The `sceneName` field is read from the coroutine's declaring type.
  - Feature: Skip Built-In Scene Bundle Probes.
- `BoundedScenes`:
  - Patches `EFT.LoadScenesFromPresetOperation.DirectLoadCoroutine` (prefix).
  - Reflects `EFT.AssetsManager.IAssetsManager`, `EFT.ResourceKey`, `EFT.AssetsManager.LoadSceneOperation`, `EFT.AssetsManagerExtension.LoadScene`. Field access is compiled with `Expression`.
  - Feature: Bounded As-Ready Scene Activation, Scene In-Flight Limit.

## Streamable bundle redirection (`StreamingBundlePatch`)

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `UnityEngine.AssetBundle` | `LoadFromFileAsync(string)` | prefix at `Priority.Last` | Streamable texture index substitution. Runs after Rupture's `ClothingBundleHooks` prefix, which returns `false` for staged bundles. |

Startup gate (`Plugin.PumpStreamableGate`, `GameMipStreaming`; verified against EFT 0.16.9.40743): the patch and its background index scan are armed only after EFT has loaded its settings, and not at all when

- BepInEx has loaded `com.fika.headless` (soft dependency for load order only; Fika.Headless's `SettingsPatch` sets `MipStreaming` to false), or
- the loaded setting is false: `Comfort.Common.Singleton<EFT.Settings.SettingsManager>.Instance.Graphics` (field) `.Settings` (field on `SettingsManager.SettingsWithProvider<GraphicsSettingsGroup>`) `.MipStreaming` (`Bsg.GameSettings.GameSetting<bool>` field on `EFT.Settings.Graphics.GraphicsSettingsGroup`) `.Value`. `Plugin.Update` polls `Singleton<SettingsManager>.Instantiated` until it is true; `SettingsManager.InstantiateSingleton` creates the singleton only after `Load`, so the value is the loaded one.

The live object is read instead of `Graphics.ini` because SPT's `SaveSettingsToSptFolderPatch` (SPT.Custom) redirects `SettingsManager.SettingsFolderPath` to `SPT_Runtime/user/sptSettings`; the `%APPDATA%` file is the live EFT client's. EFT applies a changed `MipStreaming` live (`GraphicsSettingsController` binding → `QualitySettings.streamingMipmapsActive`), but the gate decides once per process, so item textures are marked only after a restart.

## Spawn zones (`SpawnZoneHooks`)

| Target | Members | Kinds | Feature |
| --- | --- | --- | --- |
| `EFT.Game.Spawning.SpawnPointsCollection` | `CreateFromScene` (2 params), `FixSpawns` (4 params; `zones` is `BotZone[]`) | prefix, postfix | Scoped spawn zone lookup |
| `EFT.Game.Spawning.SpawnPointMarker` | `Create` (2 params; `GameObject`) | transpiler | Replaces the native `FindUnityObjectsOfType<BotZone>()` call with the scoped lookup (`FindZones`) |

## Mip streaming

### Decal result synchronization (`DecalResultSynchronization`)

- `TextureQuadTree`: `UpdateTextureRequestedMipMapLevel` (0 params) prefix/finalizer/transpiler (reader guard); `WorkThread(object)` transpiler (worker guard).
- Field refs by name: `updatedDuringFrame`, `requestedMipMapLevel`, `pixelsHBuf`, `pixelsVBuf`, `closestDist`.
- Feature: Synchronize Decal Results.

### Decal texture-array resync (`DecalTextureArrayResync`)

- `StaticDeferredDecalDrawInstance`: field `_materials`; `CreateTextureArray`.
- Feature: Resync Decal Texture Arrays.

### Decal mip probes (`DecalMipProbe`, diagnostic)

- `StaticDeferredDecalRenderer`: `UpdateBuffer(Camera)` (prefix/finalizer), `UpdateInstancesBuffers` (prefix/finalizer).
- `StaticDeferredDecalDrawInstance`: `UpdateBuffers` (postfix).
- `TextureQuadTree`: `UpdateTextureRequestedMipMapLevel` (prefix/postfix/finalizer); fields `_texture`, `_results`, `DropResultsAfterFramesCount`, `MipOffset`, `updateParamsLock`, `_updateParams`, `ThreadResults` nested type, and `WorkThread`.
- Used only when Write Hideout Research Reports is on.

### Hideout decal probes (`HideoutDecalProbes`, diagnostic)

- `TextureQuadTree`: `Dispose()` (transpiler; `Monitor.Enter/Exit` and `Thread.Join` are rewritten to probe calls); `WorkThread(object)` (prefix/finalizer/transpiler). Field `_trees` (count property), `_workThreads`, `_terminateToken`.
- Used only when Write Hideout Research Reports is on.

### Texture worker teardown (`TextureWorkerTeardown`)

- `StaticDeferredDecalRenderer.ClearTree` (0 params): prefix at `Priority.Last`, transpiler (`GuardClearTree`).
- Fields: `_texturesQuadTrees` (renderer); `_trees` (HashSet of `TextureQuadTree`), `_workThreads` (`Thread`), `_terminateTokenSource`, `_terminateToken` (`TextureQuadTree`).
- Feature: Stop Texture Worker Before Teardown.

### Icon mip lifetimes (`IconMipLifetime`)

- `ItemIconCreator`: fields `requests` (int), `mipBudget` (float); `RenderModel` (2 params; prefix/postfix/finalizer); `RequestMipZero` (1 param); `ResetRequestedMip`.
- `EFT.PlayerIcons.PlayerIconCreator`: `RenderModel` (2 params); `CompleteRender`, `WaitForRender` (postfix); field `_containerIsBusy`.
- `IconCreatorGraphicsSettings`: fields `_requests`, `_mipBudget`, `_textures2d`; `RequestMipZero`, `RequestMipZeroFast` (prefix); `Prepare`, `Restore` (prefix).
- `EFT.Settings.Graphics.GraphicsSettingsController`: `IncreaseMemoryBudgetForIcon`, `RestoreMemoryBudget` (prefix); `RefreshStreamingMemoryBudget` (postfix); static fields `MipStreamingMemoryBudget` (float), `StreamingMipmapsMaxLevelReduction` (int).
- `EFT.CameraControl.CameraManager`: `IncreaseStreamingMipMapBias` (prefix); static `instance`; field `_streamingController`.
- Feature: Release Icon Mip Requests.

### Icon mip probes (`IconMipProbe`, diagnostic)

- `ItemIconCreator`, `IconCreatorGraphicsSettings`, `EFT.PlayerIcons.PlayerIconCreator`: `RequestMipZero` and `RequestMipZeroFast` prefixes and render-start probes, registered by `HideoutResearch` (see the table in the Hideout research section). Diagnostic only; used when Write Hideout Research Reports is on.

### Mip budget accounting (`MipBudgetAccounting`)

- `EFT.Settings.Graphics.GraphicsSettingsController`: static field `MipStreamingMemoryBudget`; `RefreshStreamingMemoryBudget(Vector4, int?)` postfix at `Priority.Last`.
- Features: Account For Nonstreaming Textures, Streaming Budget Override (`BaseBudget` stays EFT's selected value; `EffectiveBudget` is the override when it is above 0).

### Streaming budget measurement (`StreamingBudgetGame`)

No patches; every member is bound once at startup (EFT 0.16.9.40743) and read on the main thread.

| Target | Member | Use |
| --- | --- | --- |
| `EFT.CameraControl.CameraManager` | static field `instance` (not the `Instance` property, which creates a manager when none exists) | The manager whose `_vramWrapper` is set by `SetCamera`; null means no sample |
| `EFT.CameraControl.CameraManager` | `GetVRamUsage(out ulong totalVRam, out ulong localBudget, out ulong localCurrentUsage)` | DXGI VRAM query, bound as a closed delegate and rebuilt only when `instance` changes. Returns zeros when `_vramWrapper` is null, which the measurement treats as unavailable |
| `EFT.InGameStatus` | static bool `InRaid` | Set in `TarkovApplication.GamePrepare`, cleared in `OnGameEnd`; also true while the raid loads |
| `Comfort.Common.Singleton<EFT.GameWorld>` | static `Instantiated`, `Instance` | Together with `InRaid`: a raid with a live world |
| `EFT.GameWorld` | `LocationId` (string) | Map name for the result; falls back to the active scene name when empty |
| `EFT.Settings.Graphics.GraphicsSettingsController` | static float `MipStreamingMemoryBudget` | EFT's selected map budget when accounting is not active |

Unity counters read per sample: `Texture.desiredTextureMemory`, `nonStreamingTextureMemory`, `currentTextureMemory`; `QualitySettings.streamingMipmapsActive`.

### Mip residency policy (`MipResidencyPolicy`)

- `GraphicsSettingsController.RefreshStreamingMemoryBudget`: transpiler (`Guard`) and postfix (`Refreshed`).
- Uses `Texture.set_streamingTextureDiscardUnusedMips` for Retain Unused Streamed Mips.

### Texture quality with streaming (`TextureQualityWithStreaming`, experimental)

- `EFT.Settings.Graphics.GraphicsSettingsController.ChangeTexturesQuality(bool, int)`: postfix.
- `EFT.Settings.Graphics.GraphicsSettingsGroup.TextureQualityToMipBias(int)`: read for the mip-bias calculation.
- Feature: Apply Texture Quality With Streaming.

## Hideout research (`HideoutResearch`, diagnostic)

Active only when **Debug / Write Hideout Research Reports** is on and the game is restarted.

| Target | Members | Kinds | Probe key |
| --- | --- | --- | --- |
| `EFT.TarkovApplication` | nested `HideoutController` | (reflection) | — |
| `EFT.Hideout.HideoutRepresentation` | `SetHideoutData` (2 params) | prefix/postfix | `hideout.apply-data-customization` |
| `EFT.ClientBackendSession` | `FlushOperationQueue` (0 params) | prefix/postfix | `backend.flush-operations` |
| `EFT.ModernLoadScenesFromPreset` | (scene load) | prefix/postfix | — |
| `EFT.BaseLocalGame<EFT.HideoutPlayerOwner>` | (hideout game) | prefix/postfix | — |
| `EFT.HideoutGame` | `vmethod_3` (18 params) | prefix/postfix | `hideout.player-create` |
| `EFT.ObjectsFactory` | `LoadBundlesAndCreatePools` (6 params) | prefix/postfix | `resources.bundles-pools` |
| `ItemIconCreator` | `RequestMipZero` (1), `RenderModel` (2, start) | prefix, postfix | `icons.item-mip-zero`, `icons.item-render` |
| `EFT.PlayerIcons.PlayerIconCreator` | `RenderModel` (2, start) | prefix, postfix | `icons.player-render` |
| `IconCreatorGraphicsSettings` | `RequestMipZero` (1), `RequestMipZeroFast` (1) | prefix, postfix | `icons.player-mip-zero`, `icons.player-mip-zero-fast` |
| `EFT.Hideout.HideoutAreaStashController` | `UpdateStash` (1) | prefix/postfix | `customization.update-stash` |
| `EFT.Hideout.HideoutCustomizationItemsInstaller` | (install) | prefix/postfix | — |
| `StaticDeferredDecalRenderer` | (decal renderer) | prefix/postfix | — |
| `TextureQuadTree` | `Dispose` (0) | prefix/postfix | `decals.texture-tree.dispose` |
| `StaticDeferredDecalQuadTree` | `Dispose` (0) | prefix/postfix | `decals.render-tree.dispose` |
| `StaticDeferredDecalDrawInstance` | (draw) | prefix/postfix | — |
| `UnityEngine.SceneManagement.SceneManager` | `LoadSceneAsync(string, LoadSceneMode)` | prefix/postfix | — |

Patched through `Hook(...)` with prefix at `Priority.First` and postfix or finalizer at `Priority.Last`. Probe names and parameter counts above come from the `Add(...)` calls in `HideoutResearch.cs`.

## Validation harness

`tests/FuckThisLoadingScreen.Validation/` (run by `scripts/Test.ps1`) is the Unity 2022.3.43f1 batch harness. It checks the game signatures and runtime behaviour that the patches above depend on; it does not install into the game.

## Not verified in this pass

- `LoadingFrameCap` (`Loading Frame Cap Override`, `Loading Frame Limit`): patch targets not re-read per file in this pass.
- `PolicyHooks` (`AccessTools.Method` for `AssetBundle` and `AudioBakeData` calls): listed only where the grep showed a call site.
- `SceneBundleHooks.Bind` and the `LoadSceneOperation` coroutine signature: confirmed by name, not by IL.
- Rupture `ClothingBundleHooks.Install` (in the Rupture repository, `src/Rupture/Clothing/`) patches the same `LoadFromFileAsync(string)` overload with the default priority. Its prefix returns `false` for staged bundles.
