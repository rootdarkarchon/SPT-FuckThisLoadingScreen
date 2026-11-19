using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace FuckThisLoadingScreen
{
    // Main-thread observers only. No tree lock, GPU readback, native-pointer
    // acquisition, material cloning or mip writes. Disk writer receives CSV text.
    internal sealed class DecalMipProbe
    {
        internal const int MaxTrees = 256, MaxDraws = 512, MaxCameras = 8, MaxPerFrame = 16, MaxBindingScan = 512;
        internal const string MipHeader = "elapsed_ms,frame,renderer_id,renderer_scene,camera_id,camera_name,camera_x,camera_y,camera_z,texture_id,texture_name,streamable,width,height,mip_count,requested_before,requested_mip,minimum_mip,calculated_mip,desired_mip,loading_mip,loaded_mip,requested_loaded,result_state,result_count,result_camera_id,result_frame,result_age_frames,result_mip,result_fresh,pixels_h,pixels_v,closest_distance,drop_after_frames,mip_offset,worker_params_camera,worker_params_frame,budget_mb,probe_error,mip_enabled,max_reduction,discard_unused," + MipBudgetAccounting.Header;
        internal const string BindingHeader = "elapsed_ms,frame,renderer_id,decal_id,scene,path,distance_m,x,y,z,draw_material_id,source_material_id,material,shader,property,texture_id,texture_name,texture_type,streamable,lower_pixel_x,lower_pixel_y,upper_pixel_x,upper_pixel_y,tint_r,tint_g,tint_b,tint_a,keywords,probe_error,camera_id,camera_name,source_texture_id,source_texture_type";
        private readonly Func<HideoutResearchCapture> capture;
        private readonly Func<bool> inRaid;
        private readonly FieldInfo texture, results, drop, offset, paramsLock, updateParams, paramsCamera, paramsFrame;
        private readonly FieldInfo resultFrame, resultMip, resultH, resultV, resultDistance, drawMaterial, decalMaterial, lower, upper, tint;
        private readonly MethodInfo updateMethod, workerMethod;
        private ConditionalWeakTable<object, Track> tracks = new ConditionalWeakTable<object, Track>();
        private readonly List<Draw> draws = new List<Draw>();
        private readonly HashSet<string> bindings = new HashSet<string>();
        private HideoutResearchCapture currentCapture;
        private int trees, frame = -1, frameSamples, scanDraw, scanDecal;
        private double nextBindings;
        private bool overflow;
        private int buildingRenderer;
        private Context context;
        private sealed class Track { internal readonly Dictionary<int, double> Next = new Dictionary<int, double>(); }
        private sealed class Draw { internal WeakReference<object> Owner; internal WeakReference<IList> Decals; internal Bounds Bounds; internal int Renderer; }
        internal sealed class BuildScope { internal DecalMipProbe Owner; internal int Previous; }
        internal sealed class Context
        {
            internal DecalMipProbe Owner; internal Context Previous; internal HideoutResearchCapture Capture;
            internal Camera Camera; internal int Renderer; internal string Scene;
        }
        internal sealed class Sample
        {
            internal Context Context; internal object Tree; internal Texture2D Texture; internal int Before;
        }
        private sealed class Result { internal int Camera, Frame, Mip; internal float H, V, Distance; }

        internal DecalMipProbe(Func<string, Type> find, Func<HideoutResearchCapture> capture, Func<bool> inRaid)
        {
            this.capture = capture; this.inRaid = inRaid;
            Type tree = find("TextureQuadTree"), value = tree.GetNestedType("ThreadResults"), draw = find("StaticDeferredDecalDrawInstance"), decal = find("StaticDeferredDecal");
            texture = Field(tree, "_texture", typeof(Texture2D)); results = Field(tree, "_results");
            if (!results.FieldType.IsGenericType || results.FieldType.GetGenericArguments()[0] != typeof(int) || results.FieldType.GetGenericArguments()[1] != value) throw new MissingFieldException("Decal result dictionary ABI");
            drop = Field(tree, "DropResultsAfterFramesCount", typeof(int)); offset = Field(tree, "MipOffset", typeof(int));
            paramsLock = Field(tree, "updateParamsLock", typeof(object)); updateParams = Field(tree, "_updateParams");
            paramsCamera = Field(updateParams.FieldType, "cameraInstanceID", typeof(int)); paramsFrame = Field(updateParams.FieldType, "frameIndex", typeof(int));
            resultFrame = Field(value, "updatedDuringFrame", typeof(int)); resultMip = Field(value, "requestedMipMapLevel", typeof(int));
            resultH = Field(value, "pixelsHBuf", typeof(float)); resultV = Field(value, "pixelsVBuf", typeof(float)); resultDistance = Field(value, "closestDist", typeof(float));
            drawMaterial = Field(draw, "_material", typeof(Material)); decalMaterial = Field(decal, "_material", typeof(Material));
            lower = Field(decal, "LowerLeftPixel", typeof(Vector2)); upper = Field(decal, "UpperRightPixel", typeof(Vector2)); tint = Field(decal, "_tint", typeof(Color));
            updateMethod = AccessTools.DeclaredMethod(tree, "UpdateTextureRequestedMipMapLevel"); workerMethod = AccessTools.DeclaredMethod(tree, "WorkThread");
        }
        private static FieldInfo Field(Type type, string name, Type expected = null)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || (expected != null && field.FieldType != expected)) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        internal static void Install(Harmony harmony, Func<string, Type> find, Guid module)
        {
            Type renderer = find("StaticDeferredDecalRenderer"), tree = find("TextureQuadTree"), draw = find("StaticDeferredDecalDrawInstance");
            if (new[] { renderer, tree, draw }.Any(t => t.Module.ModuleVersionId != module)) throw new NotSupportedException("Decal mip probes require the supported EFT module");
            var camera = AccessTools.DeclaredMethod(renderer, "UpdateBuffer", new[] { typeof(Camera) });
            var buffers = AccessTools.DeclaredMethod(draw, "UpdateBuffers");
            var request = AccessTools.DeclaredMethod(tree, "UpdateTextureRequestedMipMapLevel", Type.EmptyTypes);
            var rebuild = AccessTools.DeclaredMethod(renderer, "UpdateInstancesBuffers", Type.EmptyTypes);
            if (camera?.ReturnType != typeof(void) || camera.IsStatic || request?.ReturnType != typeof(void) || request.IsStatic ||
                rebuild?.ReturnType != typeof(void) || rebuild.IsStatic || buffers?.ReturnType != typeof(void) || buffers.GetParameters().Length != 3 || !typeof(IList).IsAssignableFrom(buffers.GetParameters()[0].ParameterType) || buffers.GetParameters()[2].ParameterType != typeof(Bounds))
                throw new MissingMethodException("Decal mip observer ABI");
            harmony.Patch(camera, prefix: Hook(nameof(CameraPrefix)), finalizer: Hook(nameof(CameraFinalizer)));
            harmony.Patch(rebuild, prefix: Hook(nameof(BuildPrefix)), finalizer: Hook(nameof(BuildFinalizer)));
            harmony.Patch(buffers, postfix: Hook(nameof(BuffersPostfix)));
            harmony.Patch(request, prefix: Hook(nameof(MipPrefix)), postfix: Hook(nameof(MipPostfix)), finalizer: Hook(nameof(MipFinalizer)));
        }
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(DecalMipProbe), name);
        internal static void CameraPrefix(object __instance, Camera __0, out Context __state)
        { __state = null; try { __state = HideoutResearch.Active?.DecalMips.EnterCamera(__instance as Component, __0); } catch { } }
        internal static void CameraFinalizer(Context __state) { if (__state != null) __state.Owner.LeaveCamera(__state); }
        internal static void BuildPrefix(object __instance, out BuildScope __state)
        {
            __state = null; var probe = HideoutResearch.Active?.DecalMips;
            if (probe != null && __instance is Component renderer && renderer)
            { __state = new BuildScope { Owner = probe, Previous = probe.buildingRenderer }; probe.buildingRenderer = renderer.GetInstanceID(); }
        }
        internal static void BuildFinalizer(BuildScope __state) { if (__state != null) __state.Owner.buildingRenderer = __state.Previous; }
        internal static void BuffersPostfix(object __instance, IList __0, Bounds __2)
        { try { HideoutResearch.Active?.DecalMips.Remember(__instance, __0, __2); } catch { } }
        internal static void MipPrefix(object __instance, out Sample __state)
        { __state = null; try { __state = HideoutResearch.Active?.DecalMips.Select(__instance); } catch { } }
        internal static void MipPostfix(Sample __state) { if (__state != null) __state.Context.Owner.Observe(__state, null); }
        internal static void MipFinalizer(Exception __exception, Sample __state)
        { if (__exception != null && __state != null) __state.Context.Owner.Observe(__state, __exception.GetType().Name); }

        internal Context EnterCamera(Component renderer, Camera camera)
        {
            if (!renderer || !camera || !inRaid()) return null;
            var file = capture(); if (file == null || file.Closed) return null;
            if (!ReferenceEquals(file, currentCapture)) { currentCapture = file; bindings.Clear(); tracks = new ConditionalWeakTable<object, Track>(); trees = 0; overflow = false; nextBindings = 0; }
            file.DecalActivity();
            var next = new Context { Owner = this, Previous = context, Capture = file, Camera = camera, Renderer = renderer.GetInstanceID(), Scene = renderer.gameObject.scene.name };
            context = next;
            if (HideoutResearchCapture.Now >= nextBindings)
            { nextBindings = HideoutResearchCapture.Now + 250; ScanBindings(next); }
            return next;
        }
        internal void LeaveCamera(Context value) { context = value.Previous; }
        internal void Remember(object owner, IList decals, Bounds bounds)
        {
            draws.RemoveAll(d => !d.Owner.TryGetTarget(out _));
            var record = draws.Find(d => d.Owner.TryGetTarget(out object target) && ReferenceEquals(target, owner));
            if (record == null)
            {
                if (draws.Count >= MaxDraws) { Overflow("draw groups"); return; }
                record = new Draw { Owner = new WeakReference<object>(owner) }; draws.Add(record);
            }
            record.Decals = new WeakReference<IList>(decals); record.Bounds = bounds;
            if (buildingRenderer != 0) record.Renderer = buildingRenderer;
        }
        internal Sample Select(object tree)
        {
            if (context == null || context.Capture.Closed) return null;
            int nowFrame = Time.frameCount;
            if (nowFrame != frame) { frame = nowFrame; frameSamples = 0; }
            if (frameSamples >= MaxPerFrame) return null;
            if (!tracks.TryGetValue(tree, out Track track))
            {
                if (trees >= MaxTrees) { Overflow("texture trees"); return null; }
                track = new Track(); tracks.Add(tree, track); trees++;
            }
            int camera = context.Camera.GetInstanceID(); double now = HideoutResearchCapture.Now;
            if (track.Next.TryGetValue(camera, out double next) && now < next) return null;
            if (!track.Next.ContainsKey(camera) && track.Next.Count >= MaxCameras) { Overflow("cameras per tree"); return null; }
            var tex = texture.GetValue(tree) as Texture2D; if (!tex) return null;
            track.Next[camera] = now + 1000; frameSamples++;
            return new Sample { Context = context, Tree = tree, Texture = tex, Before = tex.requestedMipmapLevel };
        }
        private void Overflow(string reason)
        { if (!overflow) { overflow = true; currentCapture?.Note("decal-probe-overflow", reason + " limit reached; observations remain bounded"); } }
        private bool Synchronized()
        {
            return Harmony.GetPatchInfo(workerMethod)?.Transpilers.Any(p => p.PatchMethod.DeclaringType == typeof(DecalResultSynchronization)) == true &&
                Harmony.GetPatchInfo(updateMethod)?.Prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(DecalResultSynchronization)) == true;
        }
        private List<Result> ReadResults(object tree, out string state, out int count)
        {
            var values = new List<Result>(); count = 0;
            if (!Synchronized()) { state = "unsynchronized-not-read"; return values; }
            if (!Monitor.TryEnter(DecalResultSynchronization.Gate)) { state = "result-gate-busy"; return values; }
            try
            {
                var dict = (IDictionary)results.GetValue(tree); count = dict.Count;
                foreach (DictionaryEntry entry in dict)
                {
                    if (values.Count >= MaxCameras) break; var result = entry.Value;
                    values.Add(new Result { Camera = (int)entry.Key, Frame = (int)resultFrame.GetValue(result), Mip = (int)resultMip.GetValue(result),
                        H = (float)resultH.GetValue(result), V = (float)resultV.GetValue(result), Distance = (float)resultDistance.GetValue(result) });
                }
                state = count > MaxCameras ? "camera-rows-truncated" : count == 0 ? "no-results" : "observed"; return values;
            }
            finally { Monitor.Exit(DecalResultSynchronization.Gate); }
        }
        internal void Observe(Sample sample, string error)
        {
            try
            {
                var ctx = sample.Context; var tex = sample.Texture; var camera = ctx.Camera;
                if (!tex || !camera) { ctx.Capture.Note("decal-probe-lost-object", "camera or texture destroyed during native update; exception=" + error); return; }
                var values = ReadResults(sample.Tree, out string resultState, out int count);
                int? workerCamera = null, workerFrame = null; object gate = paramsLock.GetValue(sample.Tree);
                if (Monitor.TryEnter(gate))
                { try { object data = updateParams.GetValue(null); workerCamera = (int)paramsCamera.GetValue(data); workerFrame = (int)paramsFrame.GetValue(data); } finally { Monitor.Exit(gate); } }
                if (values.Count == 0) values.Add(null);
                foreach (var result in values)
                {
                    int limit = (int)drop.GetValue(null); int age = result == null ? 0 : Time.frameCount - result.Frame;
                    Vector3 pos = camera.transform.position;
                    var row = new[] { N(Time.frameCount), N(ctx.Renderer), Q(ctx.Scene), N(camera.GetInstanceID()), Q(camera.name), N(pos.x), N(pos.y), N(pos.z),
                        N(tex.GetInstanceID()), Q(tex.name), B(tex.streamingMipmaps), N(tex.width), N(tex.height), N(tex.mipmapCount), N(sample.Before), N(tex.requestedMipmapLevel),
                        N(tex.minimumMipmapLevel), N(tex.calculatedMipmapLevel), N(tex.desiredMipmapLevel), N(tex.loadingMipmapLevel), N(tex.loadedMipmapLevel), B(tex.IsRequestedMipmapLevelLoaded()),
                        Q(resultState), N(count), result == null ? "" : N(result.Camera), result == null ? "" : N(result.Frame), result == null ? "" : N(age), result == null ? "" : N(result.Mip),
                        result == null ? "" : B(age <= limit), result == null ? "" : N(result.H), result == null ? "" : N(result.V), result == null ? "" : N(result.Distance),
                        N(limit), N(offset.GetValue(null)), workerCamera.HasValue ? N(workerCamera.Value) : "", workerFrame.HasValue ? N(workerFrame.Value) : "",
                        N(QualitySettings.streamingMipmapsMemoryBudget), Q(error), B(QualitySettings.streamingMipmapsActive), N(QualitySettings.streamingMipmapsMaxLevelReduction), B(Texture.streamingTextureDiscardUnusedMips) };
                    ctx.Capture.DecalRow(false, string.Join(",", row) + "," + MipBudgetAccounting.Snapshot());
                }
            }
            catch (Exception failure) { sample.Context.Capture.Note("decal-probe-error", failure.GetType().Name + ": " + failure.Message); }
        }
        private void ScanBindings(Context ctx)
        {
            if (draws.Count == 0) return; int visited = 0, groups = 0;
            long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 1000;
            while (visited < MaxBindingScan && groups <= draws.Count)
            {
                if (scanDraw >= draws.Count) { scanDraw = 0; scanDecal = 0; }
                var draw = draws[scanDraw];
                if ((draw.Renderer != 0 && draw.Renderer != ctx.Renderer) || !draw.Owner.TryGetTarget(out object owner) || !draw.Decals.TryGetTarget(out IList decals) || scanDecal >= decals.Count ||
                    draw.Bounds.SqrDistance(ctx.Camera.transform.position) > 2500)
                { scanDraw++; scanDecal = 0; groups++; continue; }
                var decal = decals[scanDecal++] as Component; visited++;
                if (decal && Vector3.Distance(decal.transform.position, ctx.Camera.transform.position) <= 50 && drawMaterial.GetValue(owner) is Material material && material)
                    WriteBinding(ctx, decal, material);
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
            }
        }
        internal void WriteBinding(Context ctx, Component decal, Material material)
        {
            if (bindings.Count >= HideoutResearchCapture.MaxDecalBindings) { Overflow("decal bindings"); return; }
            Material source = decalMaterial.GetValue(decal) as Material; Vector2 lo = (Vector2)lower.GetValue(decal), hi = (Vector2)upper.GetValue(decal); Color color = (Color)tint.GetValue(decal);
            var properties = new List<string>(); material.GetTexturePropertyNames(properties);
            foreach (string property in properties)
            {
                Texture tex = material.GetTexture(property); int id = tex ? tex.GetInstanceID() : 0;
                Texture sourceTexture = source && source.HasProperty(property) ? source.GetTexture(property) : null;
                string key = decal.GetInstanceID() + ":" + material.GetInstanceID() + ":" + property + ":" + id;
                if (!bindings.Add(key)) continue;
                Vector3 pos = decal.transform.position;
                var row = new[] { N(Time.frameCount), N(ctx.Renderer), N(decal.GetInstanceID()), Q(decal.gameObject.scene.name), Q(Path(decal.transform)), N(Vector3.Distance(pos, ctx.Camera.transform.position)),
                    N(pos.x), N(pos.y), N(pos.z), N(material.GetInstanceID()), source ? N(source.GetInstanceID()) : "", Q(material.name), Q(material.shader ? material.shader.name : ""), Q(property),
                    N(id), Q(tex ? tex.name : ""), Q(tex ? tex.GetType().Name : "missing"), tex is Texture2D two ? B(two.streamingMipmaps) : "", N(lo.x), N(lo.y), N(hi.x), N(hi.y),
                    N(color.r), N(color.g), N(color.b), N(color.a), Q(string.Join(";", material.shaderKeywords)), "\"\"", N(ctx.Camera.GetInstanceID()), Q(ctx.Camera.name),
                    sourceTexture ? N(sourceTexture.GetInstanceID()) : "0", Q(sourceTexture ? sourceTexture.GetType().Name : "missing") };
                ctx.Capture.DecalRow(true, string.Join(",", row));
                if (bindings.Count >= HideoutResearchCapture.MaxDecalBindings) { Overflow("decal bindings"); break; }
            }
        }
        private static string Path(Transform value)
        { var names = new List<string>(); for (int n = 0; value && n < 16; n++, value = value.parent) names.Add(value.name); names.Reverse(); return string.Join("/", names); }
        private static string N(object value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        private static string B(bool value) => value ? "true" : "false";
        private static string Q(string value) => HideoutResearchCapture.Quote(value != null && value.Length > 512 ? value.Substring(0, 512) : value);
    }
}
