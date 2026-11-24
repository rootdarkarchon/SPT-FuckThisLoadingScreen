using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FuckThisLoadingScreen;
using HarmonyLib;
using UnityEditor;
using UnityEngine;

internal static class IconMipProbeValidation
{
    internal static void Installed(Action<bool, string> check, Func<string, Type> find, string project)
    {
        check(IconMipProbe.Start(new IconMipLifetime.Scope()) == null, "Disabled research creates no icon probe or capture");
        var harmony = new Harmony("ftls.icon-probes.native-fixture");
        var oldContext = SynchronizationContext.Current; var pump = new Pump(); SynchronizationContext.SetSynchronizationContext(pump);
        bool oldStreaming = QualitySettings.streamingMipmapsActive;
        float oldBudget = QualitySettings.streamingMipmapsMemoryBudget;
        var model = new GameObject("FTLS probe, model"); Material material = null;
        var unstreamed = new Texture2D(64, 64, TextureFormat.RGBA32, true) { name = "same, \"texture\"" };
        Texture2D streamed = null; string oldName = null;
        var output = Path.Combine(project, "icon-mip-probe-validation", Guid.NewGuid().ToString("N"));
        var capture = new HideoutResearchCapture(output, "fixture", "elapsed_ms,value");
        var results = new Dictionary<string, bool>();
        try
        {
            QualitySettings.streamingMipmapsActive = true;
            IconMipLifetime.Install(harmony, find, find("ItemIconCreator").Module.ModuleVersionId);
            MipBudgetAccounting.Active = MipBudgetAccounting.Install(harmony, find, find("ItemIconCreator").Module.ModuleVersionId);
            streamed = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/ftls-streaming-lifetime.png"); oldName = streamed.name; streamed.name = unstreamed.name;
            check(streamed.streamingMipmaps && !unstreamed.streamingMipmaps, "Native probe fixture includes genuinely streamed and nonstreamed textures");
            string shaderPath = "Assets/ftls-mip-probe.shader";
            File.WriteAllText(shaderPath, "Shader \"Hidden/FTLS/MipProbe\" { Properties { _Stream(\"Stream\",2D)=\"white\"{} _Static(\"Static\",2D)=\"white\"{} } SubShader { Pass { Color(1,1,1,1) } } }");
            AssetDatabase.ImportAsset(shaderPath, ImportAssetOptions.ForceSynchronousImport);
            material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)) { name = "probe, material" };
            material.SetTexture("_Stream", streamed); material.SetTexture("_Static", unstreamed);
            model.AddComponent<MeshRenderer>().sharedMaterial = material;
            foreach (bool player in new[] { false, true })
            {
                streamed.ClearRequestedMipmapLevel(); streamed.ClearMinimumMipmapLevel();
                IconMipLifetime.Begin(out var scope);
                scope.Probe = new IconMipProbe(capture, scope, () => true, () => null);
                var type = find(player ? "IconCreatorGraphicsSettings" : "ItemIconCreator");
                var method = AccessTools.DeclaredMethod(type, player ? "RequestMipZeroFast" : "RequestMipZero");
                var native = (Task<bool>)method.Invoke(RuntimeHelpers.GetUninitializedObject(type), new object[] { model });
                IconMipLifetime.End(ref native, scope); pump.Until(native);
                results[player ? "player-fast" : "item"] = native.GetAwaiter().GetResult();
                check(native.Status == TaskStatus.RanToCompletion, "Probe preserves successful native request Task completion");
                check(IconMipLifetime.ActiveTextures == 0 && IconMipLifetime.ActiveBudgets == 0 && scope.Probe == null,
                    "Observed native request releases ownership and discards its diagnostic records");
            }
            foreach (bool player in new[] { false, true })
            {
                IconMipLifetime.Begin(out var deadlineScope);
                deadlineScope.Probe = new IconMipProbe(capture, deadlineScope, () => true, () => null);
                var deadline = deadlineScope.Probe.BeginRequest(model, player ? "player-deadline-fixture" : "item-deadline-fixture");
                var texture = deadline.Collect(unstreamed, material, "_Static");
                deadline.Timeout(texture, player ? 50 : 200);
                var negative = deadline.Observe(Task.FromResult(false));
                check(!negative.Result && negative.Status == TaskStatus.RanToCompletion, "Controlled deadline probe keeps false mip result separate from Task failure");
                IconMipLifetime.Current.Value = null; IconMipLifetime.Release(deadlineScope, false);
            }
            IconMipLifetime.Begin(out var failedScope);
            failedScope.Probe = new IconMipProbe(capture, failedScope, () => false, () => null);
            var fault = failedScope.Probe.BeginRequest(model, "fixture-fault").Observe(Task.FromException<bool>(new InvalidOperationException("original probe fault")));
            IconMipLifetime.Current.Value = null;
            try { fault.GetAwaiter().GetResult(); check(false, "Probe must preserve fault"); }
            catch (InvalidOperationException e) { check(e.Message == "original probe fault", "Probe preserves the original request exception"); }
            IconMipLifetime.Release(failedScope, true);
        }
        finally
        {
            IconMipLifetime.Current.Value = null;
            MipBudgetAccounting.Active?.Stop(); MipBudgetAccounting.Active = null; harmony.UnpatchSelf();
            capture.Close("fixture-complete"); check(capture.Join(5000), "Dedicated icon probe writer drains and closes");
            if (streamed) { streamed.name = oldName; streamed.ClearRequestedMipmapLevel(); streamed.ClearMinimumMipmapLevel(); }
            if (material) UnityEngine.Object.DestroyImmediate(material);
            UnityEngine.Object.DestroyImmediate(model); UnityEngine.Object.DestroyImmediate(unstreamed);
            QualitySettings.streamingMipmapsActive = oldStreaming; SynchronizationContext.SetSynchronizationContext(oldContext);
            QualitySettings.streamingMipmapsMemoryBudget = oldBudget;
        }
        var lines = File.ReadAllLines(Path.Combine(output, "icon-mips.csv"));
        var header = Csv(lines[0]); var data = lines.Skip(1).Select(Csv).ToArray();
        string Value(string[] row, string field) => row[Array.IndexOf(header, field)];
        check(data.Length > 10 && data.All(row => row.Length == header.Length), "Icon CSV field counts and quoted commas match the declared schema");
        var requests = data.Where(row => Value(row, "phase") == "request").ToArray();
        check(requests.Length == 4 && requests.Select(row => Value(row, "texture_id")).Distinct().Count() == 2 &&
            requests.All(row => Value(row, "texture_name") == "same, \"texture\""), "Identical texture names retain distinct native IDs with lossless CSV escaping");
        check(requests.Count(row => Value(row, "streamable") == "true" && Value(row, "owned_by_render") == "true") == 2 &&
            requests.Count(row => Value(row, "streamable") == "false" && Value(row, "owned_by_render") == "false") == 2,
            "Probes distinguish streamability from actual lifetime ownership");
        check(requests.All(row => Value(row, "mip_budget_accounting") == "active" && Value(row, "mip_budget_icon_mb") == "100" &&
            float.Parse(Value(row, "budget_mb"), System.Globalization.CultureInfo.InvariantCulture) ==
            float.Parse(Value(row, "mip_budget_base_mb"), System.Globalization.CultureInfo.InvariantCulture) +
            float.Parse(Value(row, "mip_budget_nonstream_mb"), System.Globalization.CultureInfo.InvariantCulture) + 100),
            "Native request probes export the actual corrected total and its separate accounting components");
        check(data.Count(row => Value(row, "phase") == "request-texture-end" && (Value(row, "request_kind") == "item" || Value(row, "request_kind") == "player-fast")) == 4 &&
            data.Where(row => Value(row, "phase") == "request-texture-end").All(row => Value(row, "loaded_mip") != "" && Value(row, "requested_mip") != ""),
            "Per-texture request completion reads loaded/requested mips before render cleanup clears the request");
        var timeouts = data.Where(row => Value(row, "phase") == "timeout").ToArray();
        check(timeouts.Any(row => Value(row, "streamable") == "false" && Value(row, "attempts") == "200" && Value(row, "request_kind") == "item-deadline-fixture") &&
            timeouts.Any(row => Value(row, "streamable") == "false" && Value(row, "attempts") == "50" && Value(row, "request_kind") == "player-deadline-fixture"),
            "Controlled deadline observations retain decision attempt counts and real texture streamability");
        check(timeouts.All(row => Value(row, "decision_loaded") == "false" && Value(row, "in_raid") == "true"), "Timeout decisions and raid context are explicit");
        check(data.Any(row => Value(row, "phase") == "request-end" && Value(row, "result") == "mips-not-loaded") &&
            data.Any(row => Value(row, "phase") == "request-end" && Value(row, "result") == "faulted"), "Summary rows preserve bool failure and Task failure as different results");
        foreach (var pair in results)
            check(data.Any(row => Value(row, "phase") == "request-end" && Value(row, "request_kind") == pair.Key &&
                Value(row, "result") == (pair.Value ? "mips-loaded" : "mips-not-loaded")), "Native request bool result matches its diagnostic summary: " + pair.Key);
        check(data.Where(row => Value(row, "phase") == "cleanup" && Value(row, "streamable") == "true").All(row =>
            Value(row, "requested_mip") == "-1" && Value(row, "minimum_mip") == "8" && Value(row, "texture_owners") == "0"),
            "Cleanup rows read actual cleared Unity mip getters and zero remaining owners");
        check(data.All(row => Value(row, "probe_error") == ""), "All diagnostic texture and global getters succeed in the supported Unity runtime");

        var bounded = new HideoutResearchCapture(Path.Combine(output, "bounded"), "fixture", "elapsed_ms,value");
        bool reserved = true;
        for (int n = 0; n < HideoutResearchCapture.MaxIconRows; n++) reserved &= bounded.ReserveIconRow();
        check(reserved, "Icon row allowance remains available within its bound");
        check(!bounded.ReserveIconRow(), "Excess icon rows are rejected at their fixed limit"); bounded.Close("bounded"); check(bounded.Join(5000), "Bounded icon writer closes");
        check(File.ReadAllText(Path.Combine(bounded.DirectoryPath, "events.csv")).Contains("dropped_rows=1"), "Icon row overflow is included in dropped-row accounting");
    }
    private static string[] Csv(string line)
    {
        var fields = new List<string>(); var field = new System.Text.StringBuilder(); bool quoted = false;
        for (int n = 0; n < line.Length; n++)
        {
            char c = line[n];
            if (c == '"') { if (quoted && n + 1 < line.Length && line[n + 1] == '"') { field.Append('"'); n++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString()); return fields.ToArray();
    }
    internal sealed class Pump : SynchronizationContext
    {
        private readonly Queue<Action> _queue = new Queue<Action>();
        public override void Post(SendOrPostCallback callback, object state) { lock (_queue) _queue.Enqueue(() => callback(state)); }
        internal void Until(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted)
            {
                Action action = null; lock (_queue) if (_queue.Count > 0) action = _queue.Dequeue();
                if (action != null) action(); else Thread.Sleep(1);
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Icon probe native fixture did not finish");
            }
        }
    }
}
