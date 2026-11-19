# Changelog

## 1.1.0

Released as one build. Versions 1.0.2 through 1.0.10 were development builds and
were never released publicly. Their details are kept below as development history
so the measurements and evidence remain traceable.

The startup research (executable-to-menu timer, startup audit and logs) is not part
of this tree. It lives on branch `research/startup`. Version 0.2.0 and 0.2.1 of that
timer were withdrawn after startup regressions. Version 0.2.2 removes early runtime
and sender state-machine detours; delayed local request replays pass, and one live
0.2.2 capture reached the menu. Native boot before the first helper callback remains
unattributed. Startup behavior in the main FTLS plugin is unchanged.

### Added

- **Bot profile preload delay** (Loading / Reduce Bot Profile Preload Delay, default
  true). Reduces each fixed initial bot-profile batch wait from 500 ms to 1 ms during
  raid startup. Keeps a nonzero asynchronous delay and sequential requests; profile
  generation and backup loading remain native. Reports include
  `BotPreloadDelaysReduced` and `BotPreloadDelayNominalSavedMs`. Two shortened waits
  remove 998 ms of requested delay. These counters do not measure the actual
  raid-loading improvement.
- **Texture inventory dump** (Debug / Dump Loaded Textures, action). Setting it to true
  writes one texture inventory to `BepInEx/config/FuckThisLoadingScreen/dumps/texture-inventory`,
  then resets.
- **Hideout research reports** (Debug / Write Hideout Research Reports, default false,
  restart required). See "Hideout research reports" below.
- **Mip budget accounting** (Mip Streaming / Account For Nonstreaming Textures). See
  1.0.8 in the development history.
- **Icon mip lifetimes** (Mip Streaming / Release Icon Mip Requests). See the
  "Icon mip lifetimes" entry under Fixed.
- **Decal result synchronization** (Mip Streaming / Synchronize Decal Results).
- **Decal texture-array resync** (Mip Streaming / Resync Decal Texture Arrays). See
  1.0.10 in the development history.

### Changed

- **Hideout research reports** now also write schema 4 to schema 6 columns (icon
  request, budget accounting and decal observations) when their stages are armed;
  see the development history for 1.0.7 through 1.0.9 and "Hideout research reports"
  below.
- **Bot preload delay** sizes the initial bot-profile batch wait at 1 ms instead of
  500 ms; see Added.
- **Data locations:** runtime files moved from `BepInEx/FuckThisLoadingScreen/` to
  `BepInEx/config/FuckThisLoadingScreen/` (loading reports under `captures/`, hideout
  research under `captures/`, texture inventory under `dumps/`, the streamable texture
  index under `cache/`). Existing files are moved once on first start, with one log
  line; the old folder is removed if it ends up empty. The `.cfg` file is unchanged.
- **Logging:** by default FTLS writes one initialisation line plus warnings and errors.
  Other output is `[debug]` lines behind the new **Debug / Logging** option (default false).
- **Configuration menu:** entries are grouped into numbered categories "1. General" to
  "9. Debug"; debug, diagnostic and experimental entries sit under "9. Debug" and are
  marked advanced. Section and key names are unchanged.
- **Stage timer:** the optional level stage timer is no longer in this tree. It lives on
  branch `research/startup`.

### Fixed

- **Decal texture-worker teardown** (Loading / Stop Texture Worker Before Teardown,
  default true). Before a renderer clears all registered texture trees, the worker is
  canceled and joined once, outside the tree lock. This targets the repeated lock
  starvation measured during hideout floor customization with mip streaming enabled.
  Native tree disposal, token cleanup and subsequent worker creation remain in charge.
  Shared tree ownership and mismatched worker/token generations keep native cleanup.
  A 2 s quiescence timeout logs a fallback and continues native cleanup. The option and
  master switch are checked once per `ClearTree` call, including hideout customization
  and scene teardown. It works without research capture. The exact EFT module, field
  types and native disposal-loop shape are guarded before patching.
  **Live evidence:** matched mip-enabled captures dropped hideout entry from 94.24 s to
  11.67 s, with maximum disposal lock wait falling from 10.79 s to 0.012 ms.
- **Decal result synchronization.** The render-side result enumeration and the worker's
  dictionary insertion/result writes use a separate short gate. Worker traversal,
  sleeping and joining happen outside this gate. Reader exceptions release it through a
  finalizer.
- **Icon mip lifetimes.** Item and player icon renders own their exact streamed textures
  until their native render Task completes, fails or is canceled. Shared textures retain
  requests until the last owner finishes; prior explicit requests are restored and
  automatic requests are cleared. Temporary memory-budget, maximum-reduction and
  camera-bias adjustments share ownership across both icon creators and restore after
  the last render. Newer externally changed values are retained. These fixes require a
  restart to enable or disable safely. Texture collection deduplicates by object
  identity, so different textures with matching asset names both receive mip requests.
  Per-render request lists retain native 200-attempt counters and the fast-player
  50-yield limit. Failure cleanup also releases an owned player-icon queue slot so
  later queued renders can finish and release their streaming policy. Completed scopes
  drop their model, texture and camera references.
- **Mip budget accounting.** Corrects Unity's total texture budget when mip streaming is
  on (details under 1.0.8 in the development history).
- **Decal texture-array resync.** A streamed decal source copied before its mip 0 is
  resident no longer leaves an incomplete slice for the rest of the raid. The puddle
  rectangles (puddle decals drawn as solid wet rectangles) were confirmed fixed in a
  Customs raid.

### Experimental

Off by default. Each needs a restart after changing.

- **Retain Unused Streamed Mips** (Experimental / Retain Unused Streamed Mips, default
  false). The dependency-retention budget remains game-controlled by default. The
  option permits Unity caching of unused mips within the selected streaming memory
  budget. Forced eviction is a policy choice and its live cost has not been measured
  ([Unity's discard/caching contract](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Texture-streamingTextureDiscardUnusedMips.html)).
  Large scenes still require native engine integration; faster scheduling does not
  eliminate that cost. Loading times vary with map, hardware, cache state, loot and
  other plugins.
- **Apply Texture Quality With Streaming** (Mip Streaming / Apply Texture Quality With
  Streaming, default false). EFT's `ChangeTexturesQuality` forces
  `QualitySettings.globalTextureMipmapLimit` to 0 whenever mip streaming is on, so
  Medium/Low texture quality no longer reduces textures that cannot stream (about 5 GiB
  on Streets). This keeps EFT's own streaming-off limit (`TextureQualityToMipBias`:
  Medium drops one mip, Low two) with streaming on. The log reports each application.
- **Mark Item Textures Streamable** (Mip Streaming / Mark Item Textures Streamable,
  default false). While EFT loads an uncompressed bundle from `assets/content/items` or
  `assets/content/weapons`, the `LoadFromFileAsync` call is served by
  `AssetBundle.LoadFromStreamAsync` over a read-only stream of the original file. That
  stream returns 1 at the serialized `m_StreamingMipmaps` byte of each eligible texture
  (2D, mipmapped, pixels in an external stream, not CPU-readable, not exempt from the
  mipmap limit, not yet streamable) and the file's bytes everywhere else; only a byte
  that holds 0 is changed. Offsets come from each bundle's texture metadata
  (AssetsTools.NET), found by a background scan at startup (half the logical processors,
  at most 8; progress logged every 10 s) and saved to
  `BepInEx/config/FuckThisLoadingScreen/cache/StreamableTextureIndex.txt` (path, length, timestamp,
  offsets); later launches rescan only changed bundles. Loads never scan: a bundle not
  indexed yet loads natively. No bundle is copied or buffered in memory. Bundles with
  nothing to patch, compressed bundles, map data, mod bundles and loads already
  substituted by another mod load natively. Streams close once their bundle is unloaded.
  The log reports the index result and patched bundles/textures. This release ships
  `AssetsTools.NET.dll` and its license (`AssetsTools.NET-LICENSE.txt`).
  The first live test scanned on the load path (about 167 ms per bundle, 33 s per 200
  bundles), which slowed loot loading by 10–20 s; the background index replaces it.

**First live Streets results** (mip streaming on, 24 GB GPU, Task Manager peak
dedicated memory; texture inventory estimates in parentheses): High 14.5 GB;
High + Mark Item Textures Streamable 13.6 GB (nonstreaming Texture2D -1.16 GiB);
Medium 15 GB; Medium + Apply Texture Quality 10.8 GB (Texture2D and arrays about
-4.2 GiB); Medium + both 9.9 GB. Visual acceptance is pending.

### Testing status

- The teardown change has live evidence (see Fixed).
- The additional result, lifetime and budget patches have Unity/Mono and installed-ABI
  checks, including unpatched/patched duplicate-name comparisons at the installed
  request entry points and native policy entry execution. Live icon correctness, raid
  transitions and performance acceptance remain pending.
- Mip budget accounting: live mip-0 completion and world-texture improvement need a new
  capture and visual test. The correction intentionally increases Unity's texture-memory
  allowance; it is not a hardware VRAM measurement or an assurance that every desired
  mip fits. Native mip-reduction limits, camera bias, request deadlines and the
  unused-mip retention setting remain unchanged for the budget comparison.
- Suggested live test: keep the residency experiment off first. Generate uncached
  inventory and player icons, enter and leave hideout, then enter and leave a raid with
  mip streaming enabled. Check icon appearance, mip timeout logs, restored budget/bias
  and zero remaining icon owners. Compare retention off and on in separate restarts with
  the same texture budget, camera route and cache state before attributing any benefit
  to the eviction-policy change.

### Hideout research reports

Enable **Debug / Write Hideout Research Reports** and restart. The observer records
background hideout preloads as well as entry, so there can be a preload-only capture
before the entry capture. Output is under
`BepInEx/config/FuckThisLoadingScreen/captures/hideout-research/<UTC timestamp-id>/`:

- `capture.txt`: exact hook coverage, EFT/Unity identities, FTLS option snapshot,
  bounds and interpretation notes.
- `events.csv`: inclusive stages, task kickoff versus completion, native scene progress
  and up to 50 mip-timeout/streaming log messages. Nested stages overlap; do not add
  their durations as independent CPU work.
- `samples.csv`: 500 ms snapshots of streaming state, texture memory in bytes,
  pending/loading textures, cumulative uploads, upload limits, main-camera mip bias,
  icon activity, frame gaps and streaming-manager player-loop state.
- `heartbeat.csv`: a separate writer's 1 s heartbeat, last observed main-thread progress
  and open stages. It continues during a game-thread stall and flushes the other files.
  Growing main silence identifies lost game-thread progress; it does not identify a
  specific Unity/driver wait.
- `worker.csv` (schema 2): 1 s observations of the decal texture worker's thread state,
  global tree-lock ownership, current/last/maximum lock-hold duration and tree count at
  the last acquisition. This writer does not acquire the game lock or call Unity, so it
  can report while hideout entry is blocked.

Schema 2 adds customization pack/bundle/item stages, decal buffer/tree rebuilds,
individual tree disposal, and exact spans around disposal's native `Monitor.Enter` and
`Thread.Join` calls. Customization events include the type (floor, wall, ceiling or
target); disposal events identify the managed tree. The research wrappers forward the
worker's original lock calls with bookkeeping only. The separate teardown fix can
cancel/join the worker early; its `decals.texture-worker.quiesce` span records the time
and outcome. Capture metadata records whether that fix is armed and its initial toggle.
A long `decals.texture-tree.dispose.lock-wait` identifies lock contention; a long
`decals.texture-tree.dispose.join` identifies worker shutdown waiting. Worker
observations are sequential samples, not atomic call-stack snapshots.

Schema 3 appends `icon_mip_owned_textures`, `icon_policy_owners`,
`icon_renders_completed`, `icon_renders_failed` and `icon_textures_released` to
`samples.csv`. Completion/release counters are cumulative per process. Owned textures
and policy owners should return to zero when icon work finishes. The legacy native
icon-budget sentinel fields can be zero while another render still owns the shared
policy; use the new owner counters to interpret that case. The legacy static player
texture list is not the adapter's per-render request list; use
`icon_mip_owned_textures` for current lifetime ownership. Capture metadata records which
mip fixes and residency experiment are armed.

Capture limits: captures stop 10 seconds after entry and pending icon work finish, 30
seconds after idle background preloading/icon work, or at 600 seconds, with at most six
captures per process. Files are written during the attempt, including failures; an
interrupted capture can lack completion rows. Counts and queues are bounded and dropped
records are reported. No full texture scans or forced collections are used. For
comparison, reproduce once with mip streaming on and once off after separate restarts,
keeping other settings and hideout preload policy constant. Initial and cached visits
should be compared separately.

### Development history: 1.0.2–1.0.4

No separate detailed notes were kept for these development builds.

### Development history: 1.0.5 (withdrawn)

Version **1.0.5 is withdrawn**. Executing the installed item request method exposed an
async completion failure from its `MoveNext` detour; even a no-op IL roundtrip
reproduced the problem in the supported Unity/Mono runtime.

### Development history: 1.0.6

Version **1.0.6** uses ordinary method entry adapters and leaves all icon state machines
undetoured.

### Development history: 1.0.7

Version **1.0.7** adds schema 4's `icon-mips.csv` when research and **Release Icon Mip
Requests** are enabled. It identifies each item/player request and texture by
process-local ID, name, material/property, size and mip count. Rows record `streamable`
(Unity's per-texture `streamingMipmaps` flag), global streaming state,
requested/minimum/calculated/desired/loading/loaded mip levels, ownership, and pre-request
values. Timeout rows include the original attempt limit, elapsed wait and the failed
loaded-state decision; the subsequent state snapshot can differ if streaming completes at
that boundary. Request-end rows distinguish `mips-loaded`, `mips-not-loaded`,
cancellation and failure. Cleanup rows observe the texture again after releasing ownership
and restoring shared policy.

These probes use textures already collected for the icon request and preserve the
existing request deadlines and mip policy. Nonstreamable textures are observed too; their
presence alone does not establish the cause of a timeout. The file is capped at 8,192
rows per capture and 1,024 retained weak texture records per render. Scene and `in_raid`
context allow later raid icon requests to be distinguished from menu/hideout work. A
later icon render can start a new capture after an earlier capture closes, within the
six-capture process limit.

### Development history: 1.0.8

Version **1.0.8** adds **Mip Streaming / Account For Nonstreaming Textures**. With mip
streaming enabled, Unity's total texture budget becomes:

`EFT map/quality allowance + ceil(nonstreaming texture bytes / 1048576) + shared icon allowance`

The icon allowance is 100 MiB while at least one render owns the icon policy, regardless
of the number of overlapping item/player renders. The correction also remains active
outside icons so world textures retain the selected streamed allowance. Residency is
reread every 500 ms and immediately after native settings, scene-budget or icon-policy
writes. The original EFT base field is preserved; refreshes never add a corrected total
to itself. A newer unrelated budget write yields ownership until another native policy
write. Disabling mip streaming removes the residency adjustment; shutdown restores the
unadjusted policy after pending icons finish. This requires the icon lifetime adapter.
Turning the option off and restarting provides the previous budget comparison.

Schema 5 appends `mip_budget_base_mb`, `mip_budget_nonstream_mb`, `mip_budget_icon_mb`
and `mip_budget_accounting` to `samples.csv` and `icon-mips.csv`. While accounting is
active, the components sum to the actual `budget_mb`. `request-texture-end` rows snapshot
each texture before request cleanup: use `loaded_mip=0` together with the `mips-loaded`
request result to verify success. A cleared request can make `snapshot_requested_loaded`
true without loading mip 0, so cleanup's boolean alone is not evidence of success.

### Development history: 1.0.9

Version **1.0.9**, schema 6, retains those probes and adds targeted native decal
observations for persistent ground patches:

- `decal-mips.csv`: before/after native texture requests, loaded/loading/desired mips,
  streamability, camera identity/position, per-camera worker result mip and age, pixel
  estimates, native expiry threshold/offset, global worker camera/frame, Unity's
  mip-reduction clamp, and the existing budget-accounting components. Compare
  `camera_id` with `result_camera_id` and `worker_params_camera`.
- `decal-bindings.csv`: draw/source material IDs, shader/property, texture ID and type
  (including missing/non-Texture2D bindings), original texture ID/type, observing camera,
  scene/object path, position, distance, atlas pixel rectangle and tint. Join on
  `texture_id`; use nearby object paths to identify the affected floor decal. Bindings
  are deduplicated snapshots, not a complete scene inventory or continuous material
  history.

Raid decal rendering starts and keeps a capture alive without opening inventory. The
six-capture process limit and absolute ten-minute capture limit remain. Mip sampling is
at most once per tree/camera/second, with 16 samples/frame, 256 tracked trees and eight
cameras/tree. Nearby binding discovery scans at most 512 objects per 250 ms step, with a
1 ms time budget and 50 m camera radius. Captures are capped at 65,536 decal mip rows and
4,096 binding rows; overflow is reported. Worker results are read only with the separate
result synchronization patches installed, through a nonblocking result gate. Otherwise
rows explicitly say `unsynchronized-not-read`. The native tree lock is never acquired by
these probes. All Unity reads stay on the main thread; no pixel/GPU readback, native
texture pointer acquisition, extra mip requests or material instances are introduced.
Budget accounting and FTR are unchanged by the probes. For reproduction, linger near the
broken patch for 20–30 seconds, then look at a healthy area nearby.

### Development history: 1.0.10

Version **1.0.10** adds **Mip Streaming / Resync Decal Texture Arrays**. EFT copies every
static decal material texture into a shared `Texture2DArray` with `Graphics.CopyTexture`,
and only rebuilds that array when the material set changes. With mip streaming on, a
streamed source copied before its mip 0 is resident leaves an incomplete slice for the
rest of the raid. In the 2026-11-18 Customs capture the only streamed decal source was
`Terminal_Decal_puddle_mask`; the suspected result is puddle decals drawn as solid wet
rectangles. The fix observes each native array build, requests mip 0 for streamed sources
that were not fully resident, and recopies that slice with the native copy branches once
loaded, then clears the request. Destroyed or rebuilt arrays are dropped. Each deferral
and recopy is logged once and noted in research captures (`decals.texture-array.deferred`
/ `.resynced`). The puddle rectangles were confirmed fixed in a Customs raid.

## 1.0.1

- Loading reports are opt-in: **Debug / Write Loading Reports** defaults to false.
