using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FTLS.LevelStageTimer
{
    [BepInPlugin("com.rootdarkarchon.ftls.levelstagetimer", "FTLS Level Stage Timer", "0.7.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private Harmony harmony;
        private Recorder recorder;
        private SceneTracker scenes;
        private ActivationRecorder activations;
        private string lifecycleManifest;
        private readonly List<string> coverage = new List<string>();
        private readonly Queue<Capture> pendingExports = new Queue<Capture>();
        private string output, runtime;
        private Type schedulerType;
        private PropertyInfo schedulerInstance;
        private float nextSample, maxFrame;
        private double frameSeconds;
        private int frames;
        private bool sampleWarning;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // Chainloader loads assemblies and invokes plugin Awake one at a time.
        // Start runs after that synchronous startup, so optional types are discoverable
        // without changing dependency ordering of the user's installed mods.
        private void Start()
        {
            if (!Config.Bind("Capture", "Enabled", true, "Record the next loading attempts. Restart the game after changing settings.").Value) return;
            int count = Config.Bind("Capture", "MaxCaptures", 3,
                new ConfigDescription("Maximum loading attempts per game process, including failures/timeouts.", new AcceptableValueRange<int>(1, 10))).Value;
            int timeout = Config.Bind("Capture", "TimeoutSeconds", 600,
                new ConfigDescription("Maximum duration of one capture, including waits.", new AcceptableValueRange<int>(30, 600))).Value;
            recorder = new Recorder(Math.Max(1, Math.Min(10, count)), Math.Max(30, Math.Min(600, timeout)));
            output = Path.Combine(Paths.BepInExRootPath, "FTLS.LevelStageTimer");
            try
            {
                Type app = AccessTools.TypeByName("EFT.TarkovApplication") ?? throw new TypeLoadException("EFT.TarkovApplication");
                runtime = "Loaded EFT module MVID: " + app.Module.ModuleVersionId + "\nLoaded EFT assembly: " + app.Assembly.FullName;
                bool fika = AccessTools.TypeByName("Fika.Core.Main.GameMode.CoopGame") != null;
                harmony = new Harmony(Info.Metadata.GUID);
                foreach (HookSpec spec in HookCatalog.Specs.Concat(HookCatalog.OptionalSpecs))
                {
                    if (spec.Fika && !fika) continue;
                    if (!HookCatalog.Available(spec, AccessTools.TypeByName))
                    {
                        coverage.Add(spec.Stage + " | unavailable optional group: " + spec.OptionalGroupType);
                        continue;
                    }
                    foreach (MethodBase method in HookCatalog.Resolve(spec, AccessTools.TypeByName))
                    {
                        int countArgument = Array.FindIndex(method.GetParameters(), p => p.Name == "resources" || p.Name == "resourceInfo" || p.Name == "lootItems");
                        PatchStage(method, new Hook { Stage = spec.Stage, Kind = spec.Kind, Root = spec.Root, CountArgument = countArgument, MaxCalls = spec.MaxCalls, Aggregate = spec.Aggregate });
                        coverage.Add(spec.Stage + " | " + method.DeclaringType.FullName + "." + method.Name + " | " + method +
                            (spec.OptionalGroupType == null ? "" : " | module MVID: " + method.Module.ModuleVersionId) +
                            (spec.Aggregate ? " | all calls also recorded in aggregates.csv" : ""));
                        if (spec.MaxCalls > 0 && method is MethodInfo info)
                        {
                            MethodInfo step = DetailProbes.StateMachineStep(info);
                            if (step != null)
                            {
                                PatchStage(step, new Hook { Stage = spec.Stage + ".step", Kind = "work", MaxCalls = 64 }, step: true);
                                coverage.Add(spec.Stage + ".step | synchronous MoveNext execution | " + step.DeclaringType.FullName);
                            }
                        }
                    }
                }
                DetailProbes.Install(harmony, AccessTools.TypeByName, fika, coverage);
                RetentionProbes.Install(harmony, AccessTools.TypeByName, coverage);
                scenes = new SceneTracker(recorder, new OperationAccess(AccessTools.TypeByName("Comfort.Common.IOperation") ?? throw new TypeLoadException("Comfort.Common.IOperation")));
                SceneProbes.Tracker = scenes;
                SceneProbes.Install(harmony, AccessTools.TypeByName, coverage);
                activations = ActivationProbes.Install(harmony, app.Assembly, recorder, coverage, out lifecycleManifest);
                Type metrics = AccessTools.TypeByName("EFT.Utilities.ClientMetricsEvents");
                ConstructorInfo constructor = metrics?.GetConstructors().Single();
                if (constructor == null) throw new MissingMethodException("ClientMetricsEvents constructor");
                harmony.Patch(constructor, postfix: HookMethod(nameof(LoadingHooks.MarkerCreatedPostfix), Priority.Last));
                MethodInfo started = AccessTools.DeclaredMethod(metrics, "SetGameStarted");
                if (started == null || started.ReturnType != typeof(void) || started.GetParameters().Length != 0)
                    throw new MissingMethodException("ClientMetricsEvents.SetGameStarted");
                harmony.Patch(started, postfix: HookMethod(nameof(LoadingHooks.StartedPostfix), Priority.Last));
                coverage.Add("terminal | ClientMetricsEvents.SetGameStarted");
                schedulerType = AccessTools.TypeByName("Diz.Jobs.JobScheduler");
                Type singleton = AccessTools.TypeByName("Comfort.Common.Singleton`1");
                if (schedulerType != null && singleton != null)
                    schedulerInstance = AccessTools.Property(singleton.MakeGenericType(schedulerType), "Instance");
                LoadingHooks.Recorder = recorder; // Arm only after every required hook succeeds.
                ActivationProbes.Recorder = activations;
                Logger.LogInfo($"Level stage timer armed: {count} attempts, {timeout}s/attempt, 2048 spans, 1200 samples, 512 scene records, 8192 lifecycle keys and 256 Pinewood slow calls maximum. Output: {output}");
            }
            catch (Exception error)
            {
                Logger.LogError("Level stage timer refused incomplete hook coverage: " + error);
                Cleanup();
                enabled = false;
            }
        }

        private static HarmonyMethod HookMethod(string name, int priority) =>
            new HarmonyMethod(typeof(LoadingHooks), name) { priority = priority };

        private void PatchStage(MethodBase method, Hook hook, bool step = false)
        {
            LoadingHooks.Hooks.Add(method, hook);
            if (hook.Aggregate)
            {
                harmony.Patch(method, HookMethod(nameof(LoadingHooks.AggregatePrefix), Priority.First),
                    HookMethod(nameof(LoadingHooks.AggregatePostfix), Priority.Last),
                    finalizer: HookMethod(nameof(LoadingHooks.AggregateFinalizer), Priority.Last));
                return;
            }
            Type returns = HookCatalog.ReturnType(method);
            string postfix = typeof(Task).IsAssignableFrom(returns) ? nameof(LoadingHooks.TaskPostfix) :
                returns == typeof(IEnumerator) ? nameof(LoadingHooks.EnumeratorPostfix) : nameof(LoadingHooks.SyncPostfix);
            harmony.Patch(method, HookMethod(step ? nameof(LoadingHooks.StepPrefix) : nameof(LoadingHooks.Prefix), Priority.First), HookMethod(postfix, Priority.Last),
                finalizer: HookMethod(nameof(LoadingHooks.Finalizer), Priority.Last));
        }

        private void Update()
        {
            if (recorder == null) return;
            if (recorder.IsActive)
            {
                frames++;
                frameSeconds += Time.unscaledDeltaTime;
                maxFrame = Math.Max(maxFrame, Time.unscaledDeltaTime);
                if (Time.realtimeSinceStartup >= nextSample)
                {
                    nextSample = Time.realtimeSinceStartup + 1;
                    try
                    {
                        object scheduler = schedulerInstance?.GetValue(null);
                        object app = LoadingHooks.RootInstance;
                        object bundleLock = app == null ? null : AccessTools.Field(app.GetType(), "BundleLock")?.GetValue(app);
                        recorder.Sample(string.Join(",", new[] {
                            Application.backgroundLoadingPriority.ToString(), QualitySettings.asyncUploadTimeSlice.ToString(Invariant),
                            QualitySettings.asyncUploadBufferSize.ToString(Invariant),
                            ReadProperty(schedulerType, null, "QueueLength"), ReadProperty(schedulerType, scheduler, "IsForceModeEnabled"),
                            ReadProperty(bundleLock?.GetType(), bundleLock, "MaxConcurrentOperations"),
                            GC.CollectionCount(0).ToString(Invariant), GC.CollectionCount(1).ToString(Invariant), GC.CollectionCount(2).ToString(Invariant),
                            GC.GetTotalMemory(false).ToString(Invariant), frames.ToString(Invariant),
                            (frameSeconds * 1000 / frames).ToString("F3", Invariant), (maxFrame * 1000).ToString("F3", Invariant),
                            Application.targetFrameRate.ToString(Invariant), QualitySettings.vSyncCount.ToString(Invariant),
                            ReadProperty(schedulerType, scheduler, "FrameTicks") }));
                    }
                    catch (Exception error)
                    {
                        if (!sampleWarning) Logger.LogWarning("Loading sample unavailable: " + error.GetType().Name);
                        sampleWarning = true;
                    }
                    frames = 0; frameSeconds = 0; maxFrame = 0;
                }
            }
            Capture ready = recorder.TakeReady();
            if (ready != null) pendingExports.Enqueue(ready);
            // Superseded captures must not add file I/O/serialization to a retry's loading window.
            if (!recorder.IsActive)
                while (pendingExports.Count > 0) Export(pendingExports.Dequeue());
            if (recorder.Exhausted)
            {
                Cleanup();
                enabled = false;
                Logger.LogInfo("Loading capture budget exhausted; timer hooks removed.");
            }
        }

        private static string ReadProperty(Type type, object instance, string name)
        {
            PropertyInfo property = type == null ? null : AccessTools.Property(type, name);
            if (property == null || (!property.GetGetMethod(true).IsStatic && instance == null)) return "unavailable";
            return Convert.ToString(property.GetValue(instance), Invariant);
        }

        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        private void Export(Capture capture)
        {
            // Small bounded files, written only after the measured capture has closed.
            try
            {
                scenes?.Detach(capture); // Release subscriptions even when creating the output directory fails.
                string folder = Path.Combine(output, capture.StartedUtc.ToString("yyyyMMdd-HHmmss", Invariant) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(folder);
                var csv = new StringBuilder("id,stage,kind,start_ms,duration_ms,outcome,input_count,start_thread,end_thread,detail\n");
                foreach (StageRow row in capture.Rows)
                    csv.Append(row.Id).Append(',').Append(Csv(row.Stage)).Append(',').Append(Csv(row.Kind)).Append(',')
                        .Append(recorder.Milliseconds(row.Start - capture.Start).ToString("F3", Invariant)).Append(',')
                        .Append(recorder.Milliseconds(row.End - row.Start).ToString("F3", Invariant)).Append(',')
                        .Append(Csv(row.Outcome)).Append(',').Append(row.Count).Append(',').Append(row.StartThread).Append(',').Append(row.EndThread)
                        .Append(',').Append(Csv(row.Detail)).Append('\n');
                File.WriteAllText(Path.Combine(folder, "stages.csv"), csv.ToString());
                var aggregates = new StringBuilder("stage,calls,completed,threw,incomplete_calls,total_call_ms,max_call_ms,first_start_ms,last_end_ms\n");
                foreach (AggregateRow row in capture.Aggregates.Values.OrderBy(r => r.Stage))
                    aggregates.Append(Csv(row.Stage)).Append(',').Append(row.Calls).Append(',').Append(row.Completed).Append(',').Append(row.Threw)
                        .Append(',').Append(row.Calls - row.Completed - row.Threw).Append(',')
                        .Append(recorder.Milliseconds(row.TotalTicks).ToString("F3", Invariant)).Append(',')
                        .Append(recorder.Milliseconds(row.MaxTicks).ToString("F3", Invariant)).Append(',')
                        .Append(recorder.Milliseconds(row.FirstStart - capture.Start).ToString("F3", Invariant)).Append(',')
                        .Append(row.Completed + row.Threw == 0 ? "" : recorder.Milliseconds(row.LastEnd - capture.Start).ToString("F3", Invariant)).Append('\n');
                File.WriteAllText(Path.Combine(folder, "aggregates.csv"), aggregates.ToString());
                File.WriteAllText(Path.Combine(folder, "scene-phases.csv"), SceneTracker.Export(capture));
                File.WriteAllText(Path.Combine(folder, "retention.csv"), RetentionProbes.Export(capture));
                File.WriteAllText(Path.Combine(folder, "scene-lifecycle.csv"), activations.ExportMethods(capture));
                File.WriteAllText(Path.Combine(folder, "scene-handlers.csv"), activations.ExportMethods(capture, handlersOnly: true));
                File.WriteAllText(Path.Combine(folder, "scene-lifecycle-totals.csv"), activations.ExportScenes(capture));
                File.WriteAllText(Path.Combine(folder, "pinewood-slow-calls.csv"), activations.ExportPinewood(capture));
                File.WriteAllText(Path.Combine(folder, "scene-lifecycle-coverage.txt"), lifecycleManifest);
                File.WriteAllLines(Path.Combine(folder, "samples.csv"), new[] {
                    "elapsed_ms,background_priority,upload_slice_ms,upload_buffer_mb,scheduler_queue,scheduler_force_mode,bundle_limit,gc0,gc1,gc2,managed_bytes,frames,mean_frame_ms,max_frame_ms,target_frame_rate,v_sync_count,scheduler_frame_ticks"
                }.Concat(capture.Samples));
                File.WriteAllText(Path.Combine(folder, "capture.txt"), "FTLS Level Stage Timer 0.7.0\nUTC start: " + capture.StartedUtc.ToString("O") +
                    "\nOutcome: " + capture.Outcome + "\nElapsed ms: " + recorder.Milliseconds(capture.Terminal - capture.Start).ToString("F3", Invariant) +
                    "\nDropped spans: " + capture.DroppedRows + "\nDropped samples: " + capture.DroppedSamples + "\n" + runtime +
                    "\nPer-stage suppressed spans: " + (capture.Suppressed.Count == 0 ? "none" : string.Join("; ", capture.Suppressed.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))) +
                    "\nDropped aggregate stages: " + capture.DroppedAggregateStages +
                    "\nDropped scenes: " + capture.DroppedScenes + "\nScene observer errors: " + capture.SceneObserverErrors +
                    "\nScene requests: " + capture.SceneRequests + "\nRecorded scenes: " + capture.ScenePhases.Count + "\nBundle error read failures: " + capture.BundleErrorReadFailures +
                    "\nUnrecorded lifecycle detail calls: " + capture.DroppedLifecycleDetails + "\nUnmatched lifecycle calls: " + capture.UnmatchedLifecycleCalls +
                    "\nAmbiguous lifecycle calls: " + capture.AmbiguousLifecycleCalls + "\nLifecycle observer errors: " + capture.LifecycleObserverErrors +
                    "\nLifecycle depth overflows: " + capture.DroppedLifecycleDepth + "\nPinewood observed calls: " + capture.PinewoodCalls + "\nPinewood retained slow calls: " + capture.PinewoodDetails.Count +
                    "\nScene handler methods: " + capture.SceneHandlerMethods.Count + "\nUnrecorded scene handler calls: " + capture.DroppedSceneHandlerCalls + "\nOut-of-scope scene handler calls: " + capture.OutOfScopeSceneHandlerCalls +
                    "\nRetention records: " + capture.Retentions.Count + "\nDropped retention records: " + capture.DroppedRetentions + "\nRetention observer errors: " + capture.RetentionObserverErrors + "\nRetention roots count actual root recursive entries. Recursive entries include overlapping calls; incomplete records have no duration. Membership/admission/repeated-visit/yield columns are unavailable and intentionally blank; no async MoveNext instrumentation is installed.\n" +
                    "\nInclusive wall-clock spans overlap; do not sum durations. Wait spans include intentional synchronization/countdown.\n" +
                    "Task completion observer scheduling and timer overhead are included. Samples are main-thread observations, not continuous state.\n" +
                    "GC counters are process-wide; managed_bytes uses GetTotalMemory(false). No collection is requested by the timer.\n" +
                    "Detail .step spans include inline continuations into callers; they are not exclusive costs of the named method. Callback handlers include assembly and method in detail.\n" +
                    "aggregates.csv sums finished calls including throws, even beyond detail caps; totals are inclusive, not merged wall coverage or CPU time. Incomplete calls have no duration.\n" +
                    "scene-phases.csv separates bundle readiness, observed Unity readiness and activation/completion; automatic-unsplit loads have no invented activation duration.\n" +
                    "Scene records have an independent 512-request budget; general scene spans retain first 64 detail. Explicit activation starts at entry to the unchanged setter.\n" +
                    "Lifecycle timing is synchronous elapsed time by actual object scene and entry window. Self excludes nested observed hooks, not all native work; includes instrumentation and other Harmony patches.\n" +
                    "scene-handlers.csv identifies each observed sceneLoaded subscriber method. Multicast snapshot/order/duplicates/arguments/exceptions are preserved. Dispatcher inclusive overlaps handlers; its self excludes observed handlers. ORBIT handlers are forwarded without individual timing.\n" +
                    "Per-scene lifecycle totals include calls beyond the 8192 method-detail keys. The slowest 256 Pinewood indoor calls are retained separately. Native engine work, other assemblies, generic and nonvoid lifecycle methods are not covered.\n" +
                    "Hook coverage:\n" + string.Join("\n", coverage));
                Logger.LogInfo($"Loading capture saved ({capture.Outcome}): {folder}");
            }
            catch (Exception error) { Logger.LogError("Loading capture export failed: " + error); }
            finally { if (!recorder.IsActive) LoadingHooks.RootInstance = null; frames = 0; frameSeconds = 0; maxFrame = 0; nextSample = 0; }
        }

        private void Cleanup()
        {
            LoadingHooks.Recorder = null;
            SceneProbes.Tracker = null;
            ActivationProbes.Recorder = null;
            scenes?.Stop();
            harmony?.UnpatchSelf();
            harmony = null;
            LoadingHooks.Hooks.Clear();
            if (recorder != null)
            {
                Capture pending;
                while ((pending = recorder.TakeReady(shutdown: true)) != null) pendingExports.Enqueue(pending);
                while (pendingExports.Count > 0) Export(pendingExports.Dequeue());
            }
        }
        private void OnDestroy() => Cleanup();
    }
}
