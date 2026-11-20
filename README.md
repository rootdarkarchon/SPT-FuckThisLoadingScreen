# FuckThisLoadingScreen

**FTLS** reduces avoidable work and waiting during raid loading in SPT 4.1.
It is an independent BepInEx client mod. Every optimization has a configuration
switch, so you can compare it with the native loading path.

Current version: **1.1.0**. Version history and per-release details are in
[CHANGELOG.md](CHANGELOG.md).

## Compatibility

- **SPT 4.1 / EFT client 0.16.9.5.40743**, Unity 2022.3.43f1.
- Tested with SPT 4.1.6 and optional Fika 2.4.4.
- Fika is optional; the mod also hooks the local-game loading path.
- The optimization plugin checks the EFT module identity and required method
  shapes before applying patches. An unsupported client disables its patches
  and logs the reason. Do not assume compatibility with other client builds.

This is a client mod; there is no server component.

## Installation

1. Close the game. Remove any previous loading-research prototype plugin before
   installing FTLS, so two copies do not patch the same loading methods.
2. Extract `FuckThisLoadingScreen-<version>-SPT4.1.zip` into your SPT game directory.
   The DLL belongs at `BepInEx/plugins/FuckThisLoadingScreen/FuckThisLoadingScreen.dll`.
3. Start the game. Settings are available in BepInEx Configuration Manager and
   `BepInEx/config/com.rootdarkarchon.fuckthisloadingscreen.cfg`.

The standalone plugin GUID is `com.rootdarkarchon.fuckthisloadingscreen`.
To uninstall, remove its plugin directory and restart the game.

## What it changes

- **Bounded scene activation:** up to two additional scenes per preset can be in
  flight, with activation allowed as ready. Preserves first-scene and child-preset
  request ordering.
- **Loading frame limit:** targets 240 FPS and disables VSync during map
  initialization, then restores the previous settings. It does not change the
  game's job scheduler frame budget.
- **Integration priority:** temporarily raises Unity background loading priority
  to High during map initialization and the awaited loot bundle/pool phase.
- **Built-in scene probes:** skips redundant bundle requests only for exact
  built-in scenes with no corresponding bundle file, manifest entry, registered
  bundle or pending operation.
- **Spawn-zone lookup:** reuses the ordered zone array already discovered by
  native spawn reconciliation, within that synchronous scope only.
- **Audio batches:** processes 1200 reader records between yields instead of 300,
  keeping native decoding and record ordering.
- **Bot profile preload delay:** reduces each fixed initial bot-profile batch wait
  from 500 ms to 1 ms during raid startup. Requests stay sequential and profile
  generation and backup loading stay native.
- **Diagnostic memory queries:** avoids forced collections in two diagnostic memory
  measurements. The explicit loading cleanup collections remain.
- **Decal texture-worker teardown:** cancels and joins the decal texture worker once
  before a renderer clears its texture trees, targeting lock starvation during
  hideout customization with mip streaming enabled.
- **Mip streaming fixes:** decal result synchronization, icon mip lifetimes, mip
  budget accounting and decal texture-array resync. These are controlled by the
  **Mip Streaming** settings and need a restart after changes.
- **Texture memory with mip streaming (on by default):** Apply Texture Quality With
  Streaming keeps Medium/Low texture quality effective, and Mark Item Textures
  Streamable lets item and weapon textures stream. The second builds a small offset
  index in the background once after install or a game update.
- **Experimental (off by default):** Retain Unused Streamed Mips. Details,
  measurements and caveats are in [CHANGELOG.md](CHANGELOG.md).

## Defaults and comparisons

Options are frozen at the start of each loading attempt. Changes during a load
apply to the next attempt; restart the game for controlled comparisons.
Experimental options are off by default. Settings are grouped into numbered BepInEx configuration-manager categories; debug, diagnostic and experimental entries sit under "9. Debug". Section and key names are unchanged.

| Section / setting | Default | Comparison setting |
| --- | --- | --- |
| General / Enabled | true | false disables all FTLS optimizations |
| Loading / Integration Priority | High | Native; Normal is also available |
| Loading / Extend Priority Through Loot Bundles | true | false limits priority to map initialization |
| Loading / Reduce Bot Profile Preload Delay | true | false restores the native 500 ms per initial batch; enabled uses 1 ms |
| Loading / Loading Frame Cap Override | true | false uses the native frame cap |
| Loading / Loading Frame Limit | 240 | Range: 60–360 |
| Loading / Skip Built-In Scene Bundle Probes | true | false retains native probes |
| Loading / Scoped Spawn Zone Lookup | true | false retains native discovery |
| Loading / Stop Texture Worker Before Teardown | true | false retains native teardown timing |
| Mip Streaming / Synchronize Decal Results | true | false at startup retains native result access; restart required |
| Mip Streaming / Resync Decal Texture Arrays | true | false at startup retains native array copies; restart required |
| Mip Streaming / Release Icon Mip Requests | true | false at startup retains native icon lifetimes; restart required |
| Mip Streaming / Account For Nonstreaming Textures | true | false retains the previous budget; requires icon lifetimes; restart required |
| Mip Streaming / Apply Texture Quality With Streaming | true | false retains EFT's limit 0 with streaming on; restart required |
| Mip Streaming / Mark Item Textures Streamable | true | false loads item/weapon bundles natively; skipped (no bundle scan) when EFT's loaded mip streaming setting is off or on a Fika headless client; restart required, also after enabling mip streaming |
| Mip Streaming / Streaming Budget Override | 0 | MiB. 0 keeps EFT's per-map budget; above 0 replaces it (higher or lower) on every map; needs Account For Nonstreaming Textures; applies within 500 ms |
| Mip Streaming / Measure Streaming Budget | button | "Measure streaming budget (60 s)" samples this GPU in a raid and shows a recommendation in the menu |
| Mip Streaming / Apply Measured Budget | button | Sets Streaming Budget Override to the last recommendation |
| Experimental / Retain Unused Streamed Mips | false | true enables the unmeasured caching candidate; restart required |
| Scenes / Bounded As-Ready Scene Activation | true | false retains native scheduling when parallel mode is off |
| Scenes / Scene In-Flight Limit | 2 | Range: 1–4 |
| Audio / Larger Reader Batches | true | false retains native batches |
| Audio / Reader Batch Size | 1200 | Native: 300; range: 300–4800 |
| Memory / Non-Collecting Diagnostic Measurements | true | false retains native diagnostic queries |
| Experimental / Parallel Scene Loading | false | Optional unbounded scheduling comparison |
| Debug / Write Loading Reports | false | true enables per-load report files; summary lines go to the log under Debug / Logging |
| Debug / Write Hideout Research Reports | false | true enables hideout/mip diagnostics; restart required |
| Debug / Dump Loaded Textures | button | "Dump loaded textures now" writes one texture inventory to `BepInEx/config/FuckThisLoadingScreen/dumps/texture-inventory/` |
| Debug / Logging | false | true adds `[debug]` log lines; the initialisation line and warnings/errors are always written |

Bounded activation takes precedence over the experimental parallel toggle.
To compare native scene scheduling, turn **both** scene scheduling toggles off.
Frame limiting and temporary priority settings restore on completion,
cancellation, failure or shutdown, while preserving newer externally written
settings when ownership has changed.

The texture-worker teardown option and master switch are checked once per
`ClearTree` call. Decal result synchronization, icon lifetimes, budget accounting
and the residency experiment also read **General / Enabled** at startup. Restart
after changing their options or the master switch for these patches. Icon hooks
stay installed until pending render Tasks finish during plugin cleanup, so
ownership is not interrupted.

## Streaming budget

EFT picks the mip-streaming texture budget from a per-map table and never looks at
the GPU. **Streaming Budget Override** replaces it with one value for your GPU on
every map. Unity's budget is an upper limit, not a reservation, and EFT discards
unused mips, so a higher budget costs nothing until textures need it. The override
requires **Account For Nonstreaming Textures** (restart after enabling it).

1. Enable mip streaming in EFT's graphics settings.
2. Load Streets, the heaviest map.
3. Open the Configuration Manager (F12), press **Measure streaming budget**, and look
   and move around for 60 s, including aiming down scopes. The result appears under
   the button; no log reading is needed.
4. Press **Apply measured budget**. It sets the override to the recommendation, which
   takes effect within 500 ms.

Re-measure after changing resolution, upscaler or texture quality. Measuring
needs a raid, mip streaming on and EFT's video-memory query; otherwise the button
reports why. A raid that ends mid-measurement cancels it.

The recommendation is the VRAM EFT reports as available to the game, minus the memory
used by everything that is not a texture, minus resident nonstreaming textures and a
safety margin (the larger of 512 MiB and 10 %), rounded down to 64 MiB and limited to
256-65536 MiB. The verdict compares it with EFT's map budget and the peak streamed
texture demand:

- **EFT's budget exceeds what this GPU can hold:** applying lowers it and avoids VRAM
  paging stutter.
- **Textures were budget-limited:** applying raises it, giving sharper textures.
- **Budget never limited textures here:** applying raises the ceiling for heavier
  scenes; no visible change is expected now.

Each measurement is also written to
`BepInEx/config/FuckThisLoadingScreen/dumps/streaming-budget/`.

## Reports and data

Reports are **off by default**.

- **Loading reports:** enable **Debug / Write Loading Reports** to write per-load
  option snapshots, outcomes and bounded counters to
  `BepInEx/config/FuckThisLoadingScreen/captures/loading-reports/`, including
  frame-cap and scene-scheduling CSVs. Reports are exported after the measured
  loading run. The toggle does not change any loading optimization.
- **Hideout research reports:** enable **Debug / Write Hideout Research Reports**
  and restart. The observer is built into FTLS, works independently of
  **General / Enabled**, and leaves mip settings and native loading behavior alone.
  Output goes to `BepInEx/config/FuckThisLoadingScreen/captures/hideout-research/<UTC timestamp-id>/`
  (`capture.txt`, `events.csv`, `samples.csv`, `heartbeat.csv`, `worker.csv`).
  Captures stop automatically. Turn the option off and restart after testing.
  File columns, capture limits and interpretation notes are in
  [CHANGELOG.md](CHANGELOG.md#hideout-research-reports).

The optional stage timer is not part of this package. It lives on branch
`research/startup` of this repository.

## Data locations

Settings stay in `BepInEx/config/com.rootdarkarchon.fuckthisloadingscreen.cfg`.
Runtime files go under `BepInEx/config/FuckThisLoadingScreen/`:

| Kind | Folder |
| --- | --- |
| Loading reports (TXT, CSV) | `captures/loading-reports/` |
| Hideout research | `captures/hideout-research/` |
| Texture inventory | `dumps/texture-inventory/` |
| Streaming budget measurements | `dumps/streaming-budget/` |
| Streamable texture index | `cache/StreamableTextureIndex.txt` |

On first start, existing files in the old `BepInEx/FuckThisLoadingScreen/` folder
are moved into these folders once, and the old folder is removed if it is empty.

## Logging

FTLS normally writes one initialisation line plus warnings and errors. Enable
**Debug / Logging** to add `[debug]` lines with progress and counters. Report
files are still controlled by **Debug / Write Loading Reports**.

## Fika

FTLS declares `com.fika.core` and `com.fika.headless` as soft dependencies and
references no Fika type. It patches the EFT client loading path listed in
[docs/patch-targets.md](docs/patch-targets.md). The only headless-specific code:
when the `com.fika.headless` plugin is loaded, Mark Item Textures Streamable stays
off and the item bundle scan does not run (Fika.Headless forces EFT's mip streaming
off). Other behaviour on a headless Fika host has not been verified.

## Build from source

Requires the .NET 10 SDK and a supported local SPT installation. Game, BepInEx
and Harmony assemblies are referenced from that installation and are not bundled
in this repository. Set `TARKOV_SPT_INSTALL` to the installation folder, or pass
`-p:SptInstallPath=<folder>` to override it:

```powershell
dotnet build .\FuckThisLoadingScreen.slnx -c Release
```

`scripts/Build.ps1` builds and packages the plugin. Run
`Get-Help .\scripts\Build.ps1` for its parameters, and do not install into a
running game.

Validation additionally requires the Unity 2022.3.43f1 editor.
`scripts/Test.ps1` runs the batch validation in
`tests/FuckThisLoadingScreen.Validation/` and writes to `artifacts/test-output/`.
Run `Get-Help .\scripts\Test.ps1` for its parameters.

The checks cover loading scopes, restoration, cancellation, bounded scheduling,
native call-site guards, concurrent decal access, real imported streaming texture
cleanup, overlapping icon renders, failure/cancellation, settings refresh,
deferred cleanup, player queue recovery, retained-Task reference release and
small/large texture minimum-mip defaults. They do not replace live raid testing.
Build outputs, captures, game binaries and local editor state are excluded from Git.

## License

[MIT](LICENSE), copyright 2026 rootdarkarchon.
