# FTLS Level Stage Timer

Version 0.7.0 includes individual scene-loaded subscriber timings alongside
512-scene records, bundle failure reasons and managed activation/Pinewood detail.

Optional diagnostic plugin for FuckThisLoadingScreen, targeting installed EFT
40743 / SPT 4.1 with optional Fika 2.4.4 stages. Mip streaming is outside its
scope. The timer changes no loading, graphics, pooling, or GC settings.

From the repository root, build/package: `scripts/Build-LevelStageTimer.ps1`.
Validate: `scripts/Test-LevelStageTimer.ps1` (Unity 2022.3.43f1; uses the installed
Harmony/MonoMod runtime and checks the installed game/Fika signatures).

Extract the runtime-only ZIP into the game directory, then restart EFT. Remove
`BepInEx/plugins/FTLS.LevelStageTimer` to uninstall. Settings are under
`BepInEx/config/com.rootdarkarchon.ftls.levelstagetimer.cfg`; changes need restart.
By default the next three loading attempts are captured automatically. Each is
limited to 600 seconds, 2048 stage spans, and 1200 telemetry rows. Capture count
is configurable from 1 to 10; duration from 30 to 600 seconds. Hooks are removed
when the capture budget is exhausted. Cancellation, timeout, shutdown and a
superseding attempt are labeled rather than reported as successful loads.

Outputs: `BepInEx/FTLS.LevelStageTimer/<UTC timestamp>-<id>/`:

- `stages.csv`: monotonic start/duration, completion status, input collection
  size where available, and entry/completion-observer thread IDs. Async durations cover
  task completion; coroutine durations cover enumeration. Rows are inclusive
  wall time, can nest/overlap, and are not CPU timings. The appended `detail`
  column identifies callback subscriber assembly/type/method without retaining
  the subscriber or its target object.
- `samples.csv`: main-thread observations approximately once per second of
  loading priority, upload settings, scheduler queue/force mode, bundle limit,
  process-wide collection counters, non-collecting managed-memory estimates,
  frame duration statistics, target frame rate, VSync count and scheduler frame
  ticks. Samples can miss short state changes and
  cannot execute while the main thread is blocked.
- `capture.txt`: outcome, elapsed time, dropped rows, loaded EFT module MVID,
  and exact hook coverage. A missing required hook prevents arming the timer.
- `scene-phases.csv`: bounded per-scene bundle/loading/activation boundaries and
  durations, operation policy and outcomes, plus `bundle_error` (up to 1024
  characters) and a truncation flag. Unknown boundaries remain blank. Errors are read from completed
  operations without changing logging, consuming results or retrying requests.
  `ready_to_activation_ms` measures explicit readiness holds;
  `completion_to_native_return_ms` measures observed completion handoff latency.
  Unknown or inapplicable intervals are blank.
- `scene-lifecycle.csv`: per-scene/method/entry-window invocation counts, inclusive
  elapsed time, self time relative to other observed callbacks, longest call,
  exceptions and calls crossing Unity completion. At most 8192 keys; counts/times
  for additional calls still contribute to the independent per-scene totals.
- `scene-lifecycle-totals.csv`: per-scene totals for each entry window, including
  calls beyond the method-detail cap. It also identifies incomplete callbacks.
- `pinewood-slow-calls.csv`: the slowest 256 observed Pinewood indoor callback
  invocations, with start/duration/self, depth, outcome and entry window. This is
  a slow-call selection, not the first 256 calls or a complete chronological trace.
- `scene-lifecycle-coverage.txt`: exact hooked methods and omitted groups.
- `scene-handlers.csv`: per-scene subscriber method counts, inclusive/self/max
  durations, incomplete and thrown invocations. Labels include assembly and
  declaring type. Duplicate registrations are delivered and counted each time.
  These rows also appear in `scene-lifecycle.csv` as `scene-loaded-handler`.
- `aggregates.csv`: count, completed/thrown/incomplete calls, summed inclusive
  call duration, maximum call duration and first/last timestamps for every
  captured loot-cluster, bot-zone, spawn-marker create and parameter-fix call. These totals continue after individual
  rows reach their cap or the global row budget fills. Incomplete calls have no
  duration; thrown calls are included in the sum and identified separately.
  At most 16 aggregate records are retained, independent of call count.

The added detail stages cover Fika's factory/create path, player prefab and
inventory/quest construction, native player/body/animator/movement/visual/audio
initialization, empty hands and network setup. Bot stages split profile loading,
controller setup, cover restore/cache, doors, settings, zones, patrol cache and
loot clusters. Cleanup splits explicit first/aggressive GC calls, pending
finalizers, heap preallocation, working-set trimming and audio reset; lamp and
season setup are timed separately. The GC probes call the same original GC
operations with unchanged arguments, only at the native cleanup call sites.

Additional native bot details cover stationary weapons, cover colliders, zone
leaving, event-system construction/activation, spawner construction and both
native navmesh-cut initialization calls. Optional mod stages time ORBIT, SPT,
SAIN and ABPS bot-controller postfix methods. ORBIT is further split into config
fetch (including zone fetch), zone fetch, danger-zone refresh, navmesh-cutter
setup and roster/manager construction. These nested rows must not be added
together. Probes do not replace network requests or alter initialization order.

Spawn details cover host spawn-system setup, `CreateFromScene`, `FixSpawns`,
the GameObject overload of marker creation, and marker parameter updates. The
last two retain up to eight individual rows each plus all-call aggregate totals,
so large Streets marker reconciliations cannot hide behind the detail cap.

The timer installs and arms in Unity `Start`, after Chainloader has loaded plugin
assemblies, without changing other mods' dependency ordering. If an optional
group is absent, coverage explicitly labels it unavailable. If present, every
listed signature is required. Direct postfix-method probes also work when the
mod registers its bot-controller patch after timer installation; their owned
patches are left intact when timer patches are removed.

Selected async details also have `.step` rows for synchronous `MoveNext`
execution. These can include inline continuations into callers, including later
bot setup or cleanup after the named Task has already completed. They are not
exclusive method costs and can outlive the named method's Task span. Use the
full Task and nested ordinary-method spans for attribution. In particular,
Fika passes `async:false` to `Player.Init`, which
starts `PlayerBody.Init` without awaiting its Task. A body Task span can outlive
its parent initialization span. Empty-hands spawn measures the scheduling call,
not completion of the later readiness callback.

`callback.player-spawned` and `callback.world-person-add` time the two exact
multicast dispatch sites. Their `.handler` rows identify individual subscribers.
Delivery keeps the original invocation-list snapshot, order, duplicate entries,
arguments and exception propagation. A subscriber throw stops subsequent
delivery. Hooks on ordinary methods include other Harmony patches only to the
extent allowed by their patch ordering; these are not exclusive plugin costs.

Detail stages are limited to 8, 16, 32 or 64 rows per capture; async steps and
callback handlers to 64 per stage. The overall 2048-row limit still applies.
`Per-stage suppressed spans` in `capture.txt` reports additional calls left
without individual rows because of these limits. Their work still executes
normally. A capped stage's `stages.csv` coverage is partial: do not extrapolate
it as a full-stage total. For cluster and zone stages, use `aggregates.csv` for
the full finished-call totals; the same applies to spawn marker create/fix rows.
Aggregate sums include overlaps/reentrancy and
are not merged wall coverage, exclusive CPU costs, or additive with parent rows.

The independent scene record budget is 512 operations per capture (the measured
Streets run requested 248). These records survive exhaustion of the 2048 ordinary
stage spans. Only the first 64 scenes retain legacy phase rows in `stages.csv`;
use `scene-phases.csv` for complete scene coverage and inspect `Scene requests`,
`Recorded scenes` and `Dropped scenes` before claiming a full-map total.
Scene records include
bundle/scene names, load mode, the original activation policy, outcomes,
phase durations and relative timestamps. The native scene coroutine's existing
bundle request and Unity load calls remain intact. The probes observe returned
operations and existing progress reads; they never change activation flags,
priorities, parallelism, yields or bundle requests. Completion subscriptions are
removed when finished, on export, or when the plugin stops. Dropped scenes and
observer errors are reported in `capture.txt`.

`bundle_ms` covers the root bundle request including its dependencies, until its
completion event (or immediate observation of an already completed operation).
It is elapsed wait, not disk throughput or decompression CPU time.
`unity_to_ready_ms` runs from the Unity load call to the first existing progress
read at or above 0.9. `dispatch_ms` is the synchronous time inside Unity's load
API, including a null-operation fallback if used; it overlaps Unity loading.
`activation_ms` starts at entry to the native explicit activation setter
and ends at Unity operation completion. The interval between readiness and that
setter is held waiting, excluded from both phase durations. The combined Unity
interval includes that held wait and must not be summed with its component rows.

For initially automatic activation, a readiness read gives only an
`automatic-observed-tail`, not an exact native activation boundary. If the game
never reads progress at 0.9 before completion, the record is `automatic-unsplit`:
activation and readiness durations stay blank while `unity_combined_ms` remains
available. The timer does not delay activation to obtain a cleaner measurement.
The `scene.unity-loading` span ends at observed readiness or Unity completion;
for an automatic-unsplit record that span also contains activation.
These wall intervals can include main-thread scheduling, callbacks and native
engine work; they do not establish exclusive CPU attribution. The native
operation can report success before explicit activation. `scene.request` spans through Unity completion;
`scene.activation-request` also captures native no-op calls. Use the per-scene
records to distinguish these cases. Pending or failed operations retain explicit
statuses and unknown boundaries stay blank. No ORBIT hooks were added or changed.

Activation callbacks are discovered once during installation, from the installed
Assembly-CSharp only. The current game has 963 closed, instance, zero-argument
void `Awake`, `OnEnable` and `Start` methods. The timer also observes the managed
`SceneManager.Internal_SceneLoaded` dispatcher, including subscriber delivery.
The method budget is 1024 including that dispatcher; exceeding it refuses arming
instead of silently choosing an arbitrary subset. Open generic declarations
(currently 21) and nonvoid Start methods (currently 9 async/iterator methods) are
explicitly omitted; their returned tasks/enumerators are untouched.

Each callback is attributed to its component's actual scene path and handle at
entry. Unmatched paths, ambiguous repeated paths and mismatched handles are
counted rather than assigned to the last requested or active scene. Entry
windows distinguish Unity loading, held-ready, observed activation and
after-completion calls. Start may run after Unity completion and is not silently
charged to activation. For automatic activation, callbacks may occur before an
observed 0.9 progress read and therefore appear in the Unity-loading window.

The callback nesting budget is 128. Self time removes observed nested callbacks
(including derived Awake methods directly calling a base Awake); it is not
exclusive native CPU time. Synchronous callback elapsed time includes native
callees, scheduling/preemption, instrumentation and other Harmony patches.
Scene-loaded dispatch includes its child callbacks, so its inclusive duration
must not be added to theirs. A callback can cross operation completion; this is
reported explicitly and its whole duration remains in its entry-window bucket.
Native engine integration/deserialization, constructors/serialization callbacks,
other assemblies, generic declarations and deferred async/iterator work remain
outside these probes. No Unity profiling mode is enabled, no objects are scanned
or traversed, and no callbacks are replayed. Timings and counters hold no component
references. Original callback order, arguments, return values and exceptions are
preserved. All lifecycle hooks are removed with the existing capture budget.

Scene-loaded handler timing replaces only the existing delegate invocation call
inside the managed dispatcher. It delivers the original invocation-list snapshot
in order, including duplicates, using unchanged Scene/LoadSceneMode arguments.
A handler exception stops delivery and propagates unchanged. Diagnostic setup or
finalization errors cannot replay a handler or suppress its exception. When
disarmed, delivery uses the original multicast call directly. No handler target
or delegate is retained in a capture. At most 128 distinct subscriber methods are
identified per timer instance; additional subscribers still execute normally and
omissions are counted. ORBIT subscribers execute without individual timing.
Dispatcher inclusive time includes subscribers; its self time now excludes
observed subscriber calls, so compare inclusive dispatcher time with version 0.4.

Use `scripts/Analyze-LevelStageTimer.ps1 -CapturePath <folder>` to inspect one
capture or the last ten captures. It merges overlapping instances of each stage
before reporting coverage, and excludes incomplete/failed/disposed spans.
Different stages still overlap; their coverage must not be added together.
The timer preserves Task identity and does not inspect/consume Task exceptions.
Coroutine wrappers forward yielded objects and disposal. Observer scheduling
and instrumentation overhead are included in measured time.

For useful comparison, keep map, inventory, loot/bot settings, and plugin set
fixed. Record the first load and a repeat load; manually record when Start was
pressed and the number of peers. Fika host/player waits and deployment are
explicit wait spans. Keep captures from host and clients separate. Other plugins can perform substantial work during loading; this timer does
not establish per-plugin attribution or an unmodified EFT baseline.

First inspect `resources.bundle-job` versus `resources.create-pools`, then
`scenes.preset`, culling/audio initialization, and `memory.collect` with its
nested collection-forcing memory measurements. The retained-request stage
alone does not necessarily include its returned container's entire loading job.
Only act on repeatable measured costs. Deferring required initialization can
move stalls into gameplay and requires separate acceptance testing.
