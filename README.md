# FuckThisLoadingScreen

**FTLS** reduces avoidable work and waiting during raid loading in SPT 4.1.
It is an independent BepInEx client mod. Every optimization has a configuration
switch, so you can compare it with the native loading path.

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
  flight, with activation allowed as ready. This removes avoidable readiness
  holds while preserving first-scene and child-preset request ordering.
  Scene callbacks can occur earlier than on the native sequential path.
- **Loading frame limit:** targets 240 FPS and disables VSync during map
  initialization, then restores the previous settings. It leaves the game's job
  scheduler frame budget unchanged. Actual FPS still depends on the work running.
- **Integration priority:** temporarily raises Unity background loading priority
  to High during map initialization and the awaited loot bundle/pool phase.
- **Built-in scene probes:** skips redundant bundle requests only for exact
  built-in scenes with no corresponding bundle file, manifest entry, registered
  bundle or pending operation. Unknown paths and real bundles use the native path.
- **Spawn-zone lookup:** reuses the ordered zone array already discovered by
  native spawn reconciliation, within that synchronous scope only.
- **Audio batches:** processes 1200 reader records between yields instead of
  300, retaining native decoding and record ordering.
- **Diagnostic memory queries:** avoids forced collections from two diagnostic
  memory measurements. The explicit loading cleanup collections remain intact.

This does not fix or enable mip streaming, change the dependency-retention
budget, or optimize unrelated mods. Large scenes still require native engine
integration; faster scheduling does not eliminate that cost. Loading times vary
with map, hardware, cache state, loot and other plugins.

## Defaults and comparisons

Options are frozen at the start of each loading attempt. Changes during a load
apply to the next attempt; restart the game for controlled comparisons.

| Section / setting | Default | Comparison setting |
| --- | --- | --- |
| General / Enabled | true | false disables all FTLS optimizations |
| Loading / Integration Priority | High | Native; Normal is also available |
| Loading / Extend Priority Through Loot Bundles | true | false limits priority to map initialization |
| Loading / Loading Frame Cap Override | true | false uses the native frame cap |
| Loading / Loading Frame Limit | 240 | Range: 60–360 |
| Loading / Skip Built-In Scene Bundle Probes | true | false retains native probes |
| Loading / Scoped Spawn Zone Lookup | true | false retains native discovery |
| Scenes / Bounded As-Ready Scene Activation | true | false retains native scheduling when parallel mode is off |
| Scenes / Scene In-Flight Limit | 2 | Range: 1–4 |
| Audio / Larger Reader Batches | true | false retains native batches |
| Audio / Reader Batch Size | 1200 | Native: 300; range: 300–4800 |
| Memory / Non-Collecting Diagnostic Measurements | true | false retains native diagnostic queries |
| Experimental / Parallel Scene Loading | false | Optional unbounded scheduling comparison |

Bounded activation takes precedence over the experimental parallel toggle.
To compare native scene scheduling, turn **both** scene scheduling toggles off.
Frame limiting and temporary priority settings restore on completion,
cancellation, failure or shutdown, while preserving newer externally written
settings when ownership has changed.

## Reports and optional stage timer

FTLS writes an option snapshot, outcome and bounded counters to
`BepInEx/FuckThisLoadingScreen/` after each attempt, including frame-cap and
scene-scheduling CSVs. These reports are exported after the measured loading run.

For detailed stage, per-scene, activation and callback timings, install the
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
native call-site guards and optional timer integration. They do not replace live
raid testing. Build outputs, captures, game binaries and local editor state are
excluded from Git.

## License

[MIT](LICENSE), copyright 2026 rootdarkarchon.
