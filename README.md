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
- **Experimental (off by default):** Retain Unused Streamed Mips, Apply Texture
  Quality With Streaming and Mark Item Textures Streamable. Details, measurements
  and caveats are in [CHANGELOG.md](CHANGELOG.md).

## Defaults and comparisons

Options are frozen at the start of each loading attempt. Changes during a load
apply to the next attempt; restart the game for controlled comparisons.
Experimental options are off by default.

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
| Mip Streaming / Apply Texture Quality With Streaming | false | experimental: keep the texture-quality mipmap limit with streaming on; restart required |
| Mip Streaming / Mark Item Textures Streamable | false | experimental: patch item/weapon bundle textures to mip-stream while loading; restart required |
| Experimental / Retain Unused Streamed Mips | false | true enables the unmeasured caching candidate; restart required |
| Scenes / Bounded As-Ready Scene Activation | true | false retains native scheduling when parallel mode is off |
| Scenes / Scene In-Flight Limit | 2 | Range: 1–4 |
| Audio / Larger Reader Batches | true | false retains native batches |
| Audio / Reader Batch Size | 1200 | Native: 300; range: 300–4800 |
| Memory / Non-Collecting Diagnostic Measurements | true | false retains native diagnostic queries |
| Experimental / Parallel Scene Loading | false | Optional unbounded scheduling comparison |
| Debug / Write Loading Reports | false | true enables per-load report files and summary logs |
| Debug / Write Hideout Research Reports | false | true enables hideout/mip diagnostics; restart required |
| Debug / Dump Loaded Textures | false | action: true writes one texture inventory to `BepInEx/FuckThisLoadingScreen/TextureInventory`, then resets |

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

## Reports and stage timer

Reports are **off by default**.

- **Loading reports:** enable **Debug / Write Loading Reports** to write per-load
  option snapshots, outcomes and bounded counters to the BepInEx log and
  `BepInEx/FuckThisLoadingScreen/`, including frame-cap and scene-scheduling CSVs.
  Reports are exported after the measured loading run. The toggle does not change
  any loading optimization.
- **Hideout research reports:** enable **Debug / Write Hideout Research Reports**
  and restart. The observer is built into FTLS, works independently of
  **General / Enabled**, and leaves mip settings and native loading behavior alone.
  Output goes to `BepInEx/FuckThisLoadingScreen/HideoutResearch/<UTC timestamp-id>/`
  (`capture.txt`, `events.csv`, `samples.csv`, `heartbeat.csv`, `worker.csv`).
  Captures stop automatically. Turn the option off and restart after testing.
  File columns, capture limits and interpretation notes are in
  [CHANGELOG.md](CHANGELOG.md#hideout-research-reports).

For detailed raid stage, per-scene, activation and callback timings, install the
separate **FTLS Level Stage Timer** package. It is optional and is not included
in the main mod package. By default it records the next three loading attempts
per game start and writes to `BepInEx/FTLS.LevelStageTimer/`.

See the [timer documentation](tools/FTLS.LevelStageTimer/README.md) for capture
limits, columns and interpretation. Timings are inclusive wall-clock intervals;
nested and overlapping rows must not be added as independent CPU costs.

## Build from source

Requires the .NET 10 SDK and a supported local SPT installation. Game, BepInEx
and Harmony assemblies are referenced from that installation and are not bundled
in this repository. Set `SPT_INSTALL_PATH` or pass the installation path explicitly:

```powershell
dotnet build .\FuckThisLoadingScreen.slnx -c Release -p:SptInstallPath="D:\Games\SPT"
.\scripts\Build-Package.ps1 -SptInstallPath "D:\Games\SPT"
```

The runtime ZIP contains the plugin DLL and MIT license only and is written to
`bin/Release/`. To package the optional timer:

```powershell
.\scripts\Build-LevelStageTimer.ps1 -SptInstallPath "D:\Games\SPT"
```

Validation additionally requires the Unity 2022.3.43f1 editor. The scripts create
temporary fixture projects under ignored `obj/` and test with the installed
Harmony/Mono runtime and game signatures:

```powershell
.\scripts\Test-FuckThisLoadingScreen.ps1 -SptInstallPath "D:\Games\SPT" -Unity "C:\Path\To\Unity.exe"
.\scripts\Test-LevelStageTimer.ps1 -SptInstallPath "D:\Games\SPT" -Unity "C:\Path\To\Unity.exe"
.\scripts\Analyze-LevelStageTimer.ps1 -CapturePath "D:\Games\SPT\BepInEx\FTLS.LevelStageTimer"
```

The checks cover loading scopes, restoration, cancellation, bounded scheduling,
native call-site guards, concurrent decal access, real imported streaming texture
cleanup, overlapping icon renders, failure/cancellation, settings refresh,
deferred cleanup, player queue recovery, retained-Task reference release,
small/large texture minimum-mip defaults and optional timer integration. They do not replace live
raid testing. Build outputs, captures, game binaries and local editor state are
excluded from Git.

## License

[MIT](LICENSE), copyright 2026 rootdarkarchon.
