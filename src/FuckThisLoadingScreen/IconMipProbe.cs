using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace FuckThisLoadingScreen
{
    // All Unity reads happen on the requesting/main thread. Only serialized strings
    // cross into the capture writer; probe records retain weak texture references.
    internal sealed class IconMipProbe
    {
        internal const int MaxTextures = 1024;
        internal const string Header = "elapsed_ms,render_id,request_id,phase,request_kind,in_raid,active_scene,model,texture_id,texture_name,material,property,width,height,mip_count,streamable,mip_streaming_active,streaming_priority,requested_mip,minimum_mip,calculated_mip,desired_mip,loading_mip,loaded_mip,snapshot_requested_loaded,decision_loaded,owned_by_render,texture_owners,before_requested_mip,before_minimum_mip,attempts,wait_ms,result,texture_count,streamable_count,nonstreamable_count,timeout_count,budget_mb,max_reduction,pending_loads,loading_textures,upload_count,streaming_manager,camera_mip_bias,probe_error," + MipBudgetAccounting.Header;
        private static int _nextRender, _nextRequest, _contextFrame = -1;
        private static string _scene, _manager;
        private readonly HideoutResearchCapture _capture;
        private readonly Func<bool> _inRaid;
        private readonly Func<float?> _cameraBias;
        private readonly IconMipLifetime.Scope _scope;
        private readonly int _render = Interlocked.Increment(ref _nextRender);
        private readonly List<TextureRecord> _records = new List<TextureRecord>();
        private bool _overflowReported;
        private bool _finished;

        internal IconMipProbe(HideoutResearchCapture capture, IconMipLifetime.Scope scope, Func<bool> inRaid, Func<float?> cameraBias)
        {
            _capture = capture;
            _scope = scope;
            _inRaid = inRaid;
            _cameraBias = cameraBias;
            if (!capture.EnterIcon())
                throw new InvalidOperationException("Icon capture already closed");
            Row(null, null, "render-start", "", null, 0);
        }
        internal static IconMipProbe Start(IconMipLifetime.Scope scope)
        {
            try
            {
                HideoutResearch research = HideoutResearch.Active;
                var capture = research?.IconCapture();
                return capture == null ? null : new IconMipProbe(capture, scope, () => research.InRaid, () => research.CameraBias);
            }
            catch { return null; }
        }
        internal RequestProbe BeginRequest(GameObject model, string kind)
        {
            try
            { return _capture.Closed ? null : new RequestProbe(this, model ? model.name : "", kind); }
            catch { return null; }
        }
        internal void Finish(bool failed)
        {
            if (_finished)
                return;
            _finished = true;
            foreach (var record in _records)
                Row(record.Request, record, "cleanup", failed ? "render-failed" : "render-completed", null, record.Attempts);
            Row(null, null, "render-end", failed ? "failed" : "completed", null, 0);
            _records.Clear();
            _capture.ExitIcon();
        }
        internal sealed class TextureRecord
        {
            internal RequestProbe Request;
            internal WeakReference<Texture2D> Texture;
            internal int Id, BeforeRequested, BeforeMinimum, Attempts;
            internal string Name, Material, Property;
            internal bool Streamable, TimedOut;
        }
        internal sealed class RequestProbe
        {
            private readonly IconMipProbe _owner;
            private readonly HideoutResearchSpan _span;
            internal readonly int Id = Interlocked.Increment(ref _nextRequest);
            internal readonly double Started = HideoutResearchCapture.Now;
            internal readonly string Model, Kind;
            internal int Total, Streamable, Nonstreamable, Timeouts;
            private bool _finished;
            internal double Ended;
            internal RequestProbe(IconMipProbe owner, string model, string kind)
            {
                _owner = owner;
                Model = Clip(model);
                Kind = kind;
                _span = owner._capture.Begin("icons.mip-request", detail: "render_id=" + owner._render + " request_id=" + Id + " kind=" + Kind);
                owner.Row(this, null, "request-start", "", null, 0);
            }
            internal TextureRecord Collect(Texture2D texture, Material material, string property)
            {
                try
                {
                    Total++;
                    bool streamable = texture.streamingMipmaps;
                    if (streamable)
                        Streamable++;
                    else
                        Nonstreamable++;
                    if (_owner._records.Count >= MaxTextures)
                    {
                        if (!_owner._overflowReported)
                        { _owner._overflowReported = true; _owner._capture.Note("icon-probe-overflow", "render_id=" + _owner._render + " retained texture records capped at " + MaxTextures); }
                        return null;
                    }
                    var record = new TextureRecord
                    {
                        Request = this,
                        Texture = new WeakReference<Texture2D>(texture),
                        Id = texture.GetInstanceID(),
                        Name = Clip(texture.name),
                        Material = material ? Clip(material.name) : "",
                        Property = Clip(property),
                        Streamable = streamable,
                        BeforeRequested = texture.requestedMipmapLevel,
                        BeforeMinimum = texture.minimumMipmapLevel
                    };
                    _owner._records.Add(record);
                    return record;
                }
                catch (Exception error) { _owner.Error(error); return null; }
            }
            internal void Requested(TextureRecord texture) { if (texture != null) _owner.Row(this, texture, "request", "", null, 0); }
            internal void Timeout(TextureRecord texture, int attempts)
            {
                if (texture == null || texture.TimedOut)
                    return;
                texture.TimedOut = true;
                texture.Attempts = attempts;
                Timeouts++;
                _owner.Row(this, texture, "timeout", "not-loaded-at-deadline", false, attempts);
            }
            internal async Task<bool> Observe(Task<bool> native)
            {
                string result = "faulted";
                try
                { bool loaded = await native; result = loaded ? "mips-loaded" : "mips-not-loaded"; return loaded; }
                catch (OperationCanceledException) { result = "canceled"; throw; }
                finally { Finish(result); }
            }
            private void Finish(string result)
            {
                if (_finished)
                    return;
                _finished = true;
                Ended = HideoutResearchCapture.Now;
                try
                {
                    foreach (var record in _owner._records)
                        if (ReferenceEquals(record.Request, this))
                            _owner.Row(this, record, "request-texture-end", result, null, record.Attempts);
                    _owner.Row(this, null, "request-end", result, null, 0);
                    _span?.Finish(result);
                }
                catch { }
            }
        }
        private void Error(Exception error)
        { try { _capture.Note("icon-probe-error", Clip(error.GetType().Name + ": " + error.Message)); } catch { } }
        private static string Clip(string value) => value == null ? "" : value.Length <= 256 ? value : value.Substring(0, 256);
        private static string N(double value) => HideoutResearchCapture.Number(value);
        private static string B(bool value) => value ? "true" : "false";
        private static string Q(string value) => HideoutResearchCapture.Quote(Clip(value));
        private void Row(RequestProbe request, TextureRecord record, string phase, string result, bool? decision, int attempts)
        {
            try
            {
                if (!_capture.ReserveIconRow())
                    return;
                var fields = new string[44];
                fields[0] = N(_render);
                fields[1] = request == null ? "" : N(request.Id);
                fields[2] = phase;
                fields[3] = Q(request?.Kind ?? (_scope.PlayerRender ? "player" : "item"));
                fields[6] = Q(request?.Model);
                fields[24] = decision.HasValue ? B(decision.Value) : "";
                fields[29] = N(attempts);
                fields[30] = request == null ? "" : N((request.Ended == 0 ? HideoutResearchCapture.Now : request.Ended) - request.Started);
                fields[31] = Q(result);
                if (request != null)
                { fields[32] = N(request.Total); fields[33] = N(request.Streamable); fields[34] = N(request.Nonstreamable); fields[35] = N(request.Timeouts); }
                try
                {
                    int frame = Time.frameCount;
                    if (frame != _contextFrame)
                    { _contextFrame = frame; _scene = SceneManager.GetActiveScene().name; _manager = HideoutResearch.StreamingManager(PlayerLoop.GetCurrentPlayerLoop()); }
                    fields[4] = B(_inRaid());
                    fields[5] = Q(_scene);
                    fields[41] = Q(_manager);
                    fields[15] = B(QualitySettings.streamingMipmapsActive);
                    fields[36] = N(QualitySettings.streamingMipmapsMemoryBudget);
                    fields[37] = N(QualitySettings.streamingMipmapsMaxLevelReduction);
                    fields[38] = N(Texture.streamingTexturePendingLoadCount);
                    fields[39] = N(Texture.streamingTextureLoadingCount);
                    fields[40] = N(Texture.streamingMipmapUploadCount);
                    float? bias = _cameraBias();
                    fields[42] = bias.HasValue ? N(bias.Value) : "";
                    if (record != null)
                    {
                        fields[7] = N(record.Id);
                        fields[8] = Q(record.Name);
                        fields[9] = Q(record.Material);
                        fields[10] = Q(record.Property);
                        fields[27] = N(record.BeforeRequested);
                        fields[28] = N(record.BeforeMinimum);
                        if (!record.Texture.TryGetTarget(out Texture2D texture) || !texture)
                            fields[43] = Q("texture-destroyed-or-collected");
                        else
                        {
                            fields[11] = N(texture.width);
                            fields[12] = N(texture.height);
                            fields[13] = N(texture.mipmapCount);
                            fields[14] = B(texture.streamingMipmaps);
                            fields[16] = N(texture.streamingMipmapsPriority);
                            fields[17] = N(texture.requestedMipmapLevel);
                            fields[18] = N(texture.minimumMipmapLevel);
                            fields[19] = N(texture.calculatedMipmapLevel);
                            fields[20] = N(texture.desiredMipmapLevel);
                            fields[21] = N(texture.loadingMipmapLevel);
                            fields[22] = N(texture.loadedMipmapLevel);
                            fields[23] = B(texture.IsRequestedMipmapLevelLoaded());
                            fields[25] = B(!_scope.Finished && _scope.Textures.Contains(texture));
                            fields[26] = N(IconMipLifetime.TextureOwners(texture));
                        }
                    }
                }
                catch (Exception error) { fields[43] = Q(error.GetType().Name + ": " + error.Message); }
                _capture.IconRow(string.Join(",", fields) + "," + MipBudgetAccounting.Snapshot());
            }
            catch { } // An observer must not replace native render/request results or failures.
        }
    }
}
