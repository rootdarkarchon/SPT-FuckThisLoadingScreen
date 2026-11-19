# Agent guide: FuckThisLoadingScreen

Read `skill://tarkov-modding` before any work in this repo. It holds the shared Tarkov/SPT conventions: environment variables, identity rules, build and package flow, runtime behaviour. For investigation also read `skill://tarkov-investigate`. When delegating, name these skills in the subagent's task context: subagents don't receive this file. This file holds only rules specific to this project; where it conflicts with the skill, this file wins.

## Project rules

- Target: SPT 4.1 on EFT 0.16.9.40743. `Plugin.cs` pins `SupportedModuleId` (`cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7`) and reports a mismatch; do not change the pin without re-verifying every patch in `docs/patch-targets.md`.
- Every Harmony patch and reflected member is listed in `docs/patch-targets.md`. Update that file in the same change as any patch, reflection lookup or transpiler edit.
- Logging: exactly one Info line at initialisation, plus warnings and errors. All other Info output goes through `LogDebug` (`[debug]` prefix, gated on **Debug / Logging**) and is rate-limited where it could repeat. Loading-report files stay controlled by **Debug / Write Loading Reports**, not by Logging.
- Behaviour must not change except for logging and file locations. Measured results (see CHANGELOG.md) must not regress.
- Native call-site guards and the `__runOriginal` contract are load-bearing. A prefix that substitutes a result returns `false`; a patch must not run its work when an earlier prefix already skipped the original.
- Rupture interop: `StreamingBundlePatch.Install` patches `AssetBundle.LoadFromFileAsync(string)` at `Priority.Last`. Rupture's `ClothingBundleHooks.BeforeFileLoad` (default priority) substitutes staged clothing bundles and returns `false`; FTLS then sees `__runOriginal == false` and does nothing. Keep FTLS's priority at `Last` so that ordering holds.
- Stage timer: the `FTLS.LevelStageTimer` companion lives on branch `research/startup` and is not part of this tree.
- Fika: `com.fika.core` is a soft dependency only. No Fika type is referenced by `src/`. Do not claim headless-specific behaviour without code that checks for it.
- Runtime files go under `ModPaths` (`BepInEx/config/FuckThisLoadingScreen/<kind>/`). Do not write to `BepInEx/FuckThisLoadingScreen/` any more; that path is only read by the one-time migration.
- Machine paths: never write a literal install path into tracked files. Use `TARKOV_SPT_INSTALL`.
- Do not commit unless asked.

## Persisted contracts

Defined in `src/FuckThisLoadingScreen/Plugin.cs` (config binding) and `src/FuckThisLoadingScreen/LoadingPolicy.cs` (enum). Section and key names are byte-for-byte; renaming any of them orphans user settings.

- BepInEx GUID: `com.rootdarkarchon.fuckthisloadingscreen`
- Config file: `BepInEx/config/com.rootdarkarchon.fuckthisloadingscreen.cfg`
- Config section | key:
  - General | Enabled
  - Debug | Write Loading Reports, Dump Loaded Textures, Write Hideout Research Reports, Logging
  - Loading | Stop Texture Worker Before Teardown, Integration Priority, Extend Priority Through Loot Bundles, Reduce Bot Profile Preload Delay, Loading Frame Cap Override, Loading Frame Limit, Scoped Spawn Zone Lookup, Skip Built-In Scene Bundle Probes
  - Mip Streaming | Synchronize Decal Results, Resync Decal Texture Arrays, Release Icon Mip Requests, Account For Nonstreaming Textures, Apply Texture Quality With Streaming, Mark Item Textures Streamable, Streaming Budget Override, Measure Streaming Budget, Apply Measured Budget
  - Experimental | Retain Unused Streamed Mips, Parallel Scene Loading
  - Audio | Larger Reader Batches, Reader Batch Size
  - Memory | Non-Collecting Diagnostic Measurements
  - Scenes | Bounded As-Ready Scene Activation, Scene In-Flight Limit
- `LoadingPriority` enum values (persisted through Integration Priority): `Native`, `Normal`, `High`.
- Display categories (`ConfigurationManagerAttributes.Category`) are display metadata and may change; the keys above may not. Current categories: "1. General", "2. Loading", "3. Scenes", "4. Audio", "5. Mip Streaming" (all Mip Streaming keys), "9. Debug" (advanced: Debug, Experimental, Memory diagnostics).
- Runtime data (moved from `BepInEx/FuckThisLoadingScreen/` on first start, see `ModPaths`):
  - loading reports (TXT/CSV) → `BepInEx/config/FuckThisLoadingScreen/captures/loading-reports/`
  - hideout research → `BepInEx/config/FuckThisLoadingScreen/captures/hideout-research/`
  - texture inventory → `BepInEx/config/FuckThisLoadingScreen/dumps/texture-inventory/`
  - streaming budget measurements → `BepInEx/config/FuckThisLoadingScreen/dumps/streaming-budget/`
  - `StreamableTextureIndex.txt` → `BepInEx/config/FuckThisLoadingScreen/cache/StreamableTextureIndex.txt`
- Dependencies declared in `Plugin.cs`: `com.SPT.custom` (hard), `com.fika.core` (soft; no Fika-specific code).
