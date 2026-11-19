using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FuckThisLoadingScreen
{
    // No Unity calls here: the writer must keep making progress when the game thread stops.
    internal sealed class HideoutResearchCapture
    {
        internal const int MaxSpans = 2048, MaxSamples = 1400, MaxQueue = 8192;
        internal const int MaxIconRows = 8192;
        internal const int MaxDecalRows = 65536, MaxDecalBindings = 4096;
        private int decalRows, decalBindings;
        private double lastDecal = -10000;
        internal const string EventHeader = "elapsed_ms,span_id,stage,event,status,duration_ms,detail";
        private readonly object gate = new object();
        private readonly Queue<(byte Kind, string Row)> queue = new Queue<(byte, string)>();
        private readonly Dictionary<int, HideoutResearchSpan> open = new Dictionary<int, HideoutResearchSpan>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Func<double> clock;
        private readonly Func<string> workerSnapshot;
        private readonly Thread writer;
        private readonly double started;
        private double lastMain, lastActivity, entryFinished;
        private int spanCount, sampleCount, dropped, entries, logCount, iconRows;
        private int activeIcons;
        private bool entrySeen, closed;
        private string closeReason, error;
        internal readonly string DirectoryPath;
        internal static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        internal double Elapsed => clock() - started;
        internal bool Closed { get { lock (gate) return closed; } }
        internal string Error { get { lock (gate) return error; } }

        internal HideoutResearchCapture(string directory, string metadata, string sampleHeader, Func<double> clock = null, Func<string> workerSnapshot = null)
        {
            DirectoryPath = directory; this.clock = clock ?? (() => Now); this.workerSnapshot = workerSnapshot; started = this.clock();
            writer = new Thread(() => Write(metadata, sampleHeader)) { IsBackground = true, Name = "FTLS hideout research writer" };
            writer.Start();
        }

        internal HideoutResearchSpan Begin(string stage, bool entry = false, string detail = "")
        {
            lock (gate)
            {
                if (closed) return null;
                if (spanCount >= MaxSpans) { dropped++; return null; }
                var span = new HideoutResearchSpan(this, ++spanCount, stage, Elapsed, entry);
                open.Add(span.Id, span); lastActivity = Elapsed;
                if (entry) { entrySeen = true; entries++; }
                Event(span, "begin", "", "", detail);
                return span;
            }
        }

        internal void Finish(HideoutResearchSpan span, string status)
        {
            lock (gate)
            {
                if (closed || !open.Remove(span.Id)) return;
                lastActivity = Elapsed;
                if (span.Entry && --entries == 0) entryFinished = Elapsed;
                Event(span, "end", status, Number(Elapsed - span.Started), "");
            }
        }

        internal void LaunchReturned(HideoutResearchSpan span)
        {
            lock (gate) if (!closed) Event(span, "launch-return", "", Number(Elapsed - span.Started), "Synchronous kickoff only; task lifetime is the end row.");
        }

        internal void Pulse() { lock (gate) if (!closed) lastMain = Elapsed; }
        internal void Sample(string values)
        {
            lock (gate)
            {
                if (closed) return;
                if (sampleCount >= MaxSamples) { dropped++; return; }
                sampleCount++; Enqueue(true, Number(Elapsed) + "," + values);
            }
        }
        internal void Note(string stage, string detail)
        { lock (gate) if (!closed) Enqueue(false, Number(Elapsed) + ",0," + Quote(stage) + ",note,,," + Quote(detail)); }
        internal void StreamingLog(string message)
        { lock (gate) if (!closed && logCount++ < 50) Note("streaming-log", message.Length <= 1024 ? message : message.Substring(0, 1024)); }
        internal bool ReserveIconRow()
        {
            lock (gate)
            {
                if (closed) return false;
                if (iconRows >= MaxIconRows) { dropped++; return false; }
                iconRows++; return true;
            }
        }
        internal void IconRow(string values)
        { lock (gate) if (!closed) { lastActivity = Elapsed; EnqueueKind(2, Number(Elapsed) + "," + values); } }
        internal bool EnterIcon()
        { lock (gate) { if (closed) return false; activeIcons++; lastActivity = Elapsed; return true; } }
        internal void ExitIcon()
        { lock (gate) { activeIcons = Math.Max(0, activeIcons - 1); lastActivity = Elapsed; if (entrySeen && entries == 0) entryFinished = Elapsed; } Signal(); }
        internal void DecalActivity() { lock (gate) if (!closed) { lastDecal = Elapsed; lastActivity = Elapsed; } }
        internal void DecalRow(bool binding, string values)
        {
            lock (gate)
            {
                if (closed) return;
                if (binding ? decalBindings >= MaxDecalBindings : decalRows >= MaxDecalRows) { dropped++; return; }
                if (binding) decalBindings++; else decalRows++;
                EnqueueKind(binding ? (byte)4 : (byte)3, Number(Elapsed) + "," + values);
            }
        }

        internal void Close(string reason)
        {
            lock (gate) if (!closed) { closeReason = reason; Seal(reason); }
            Signal();
        }
        internal bool Join(int milliseconds) => writer.Join(milliseconds);
        private void Seal(string reason)
        {
            foreach (var span in open.Values) Event(span, "end", "incomplete-at-" + reason, Number(Elapsed - span.Started), "No completion was observed.");
            open.Clear();
            Enqueue(false, Number(Elapsed) + ",0,capture,end," + Quote(reason) + ",," + Quote("dropped_rows=" + dropped));
            closed = true;
        }
        private void Event(HideoutResearchSpan span, string kind, string status, string duration, string detail) =>
            Enqueue(false, Number(Elapsed) + "," + span.Id + "," + Quote(span.Stage) + "," + kind + "," + Quote(status) + "," + duration + "," + Quote(detail));
        private void Enqueue(bool sample, string row)
            => EnqueueKind(sample ? (byte)1 : (byte)0, row);
        private void EnqueueKind(byte kind, string row)
        {
            if (queue.Count >= MaxQueue) { dropped++; return; }
            queue.Enqueue((kind, row)); Signal();
        }
        private void Signal() { try { wake.Set(); } catch (ObjectDisposedException) { } }

        private void Write(string metadata, string sampleHeader)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(Path.Combine(DirectoryPath, "capture.txt"), metadata, new UTF8Encoding(false));
                using (var events = new StreamWriter(Path.Combine(DirectoryPath, "events.csv"), false, new UTF8Encoding(false)))
                using (var samples = new StreamWriter(Path.Combine(DirectoryPath, "samples.csv"), false, new UTF8Encoding(false)))
                using (var heartbeat = new StreamWriter(Path.Combine(DirectoryPath, "heartbeat.csv"), false, new UTF8Encoding(false)))
                using (var icons = new StreamWriter(Path.Combine(DirectoryPath, "icon-mips.csv"), false, new UTF8Encoding(false)))
                using (var decalMips = new StreamWriter(Path.Combine(DirectoryPath, "decal-mips.csv"), false, new UTF8Encoding(false)))
                using (var decalBindingsFile = new StreamWriter(Path.Combine(DirectoryPath, "decal-bindings.csv"), false, new UTF8Encoding(false)))
                using (var workerState = workerSnapshot == null ? null : new StreamWriter(Path.Combine(DirectoryPath, "worker.csv"), false, new UTF8Encoding(false)))
                {
                    events.WriteLine(EventHeader); samples.WriteLine(sampleHeader);
                    icons.WriteLine(IconMipProbe.Header);
                    decalMips.WriteLine(DecalMipProbe.MipHeader); decalBindingsFile.WriteLine(DecalMipProbe.BindingHeader);
                    workerState?.WriteLine(HideoutDecalState.Header);
                    heartbeat.WriteLine("elapsed_ms,last_main_ms,main_silence_ms,open_spans,samples,dropped_rows,active_stages,status");
                    double nextFlush = 0;
                    while (true)
                    {
                        bool finished;
                        lock (gate)
                        {
                            double now = Elapsed;
                            if (!closed && now >= 600000) { closeReason = "timeout-600s"; Seal(closeReason); }
                            if (!closed && entrySeen && entries == 0 && activeIcons == 0 && now - entryFinished >= 10000 && now - lastDecal >= 10000) { closeReason = "entry-tail-complete"; Seal(closeReason); }
                            if (!closed && !entrySeen && activeIcons == 0 && open.Count == 0 && now - lastActivity >= 30000) { closeReason = "preload-idle"; Seal(closeReason); }
                            finished = closed;
                        }
                        // Never hold the producer lock while doing disk I/O.
                        for (int i = 0; i < MaxQueue; i++)
                        {
                            (byte Kind, string Row) row;
                            lock (gate) { if (queue.Count == 0) break; row = queue.Dequeue(); }
                            (row.Kind == 4 ? decalBindingsFile : row.Kind == 3 ? decalMips : row.Kind == 2 ? icons : row.Kind == 1 ? samples : events).WriteLine(row.Row);
                        }
                        if (Elapsed >= nextFlush || finished)
                        {
                            string row;
                            lock (gate)
                            {
                                var stages = new List<string>();
                                foreach (var span in open.Values) { if (stages.Count == 16) break; stages.Add(span.Id + ":" + span.Stage); }
                                row = Number(Elapsed) + "," + Number(lastMain) + "," + Number(Elapsed - lastMain) + "," + open.Count + "," + sampleCount + "," + dropped + "," + Quote(string.Join(" | ", stages)) + "," + Quote(closeReason ?? "recording");
                            }
                            heartbeat.WriteLine(row);
                            if (workerState != null) workerState.WriteLine(Number(Elapsed) + "," + workerSnapshot());
                            events.Flush(); samples.Flush(); heartbeat.Flush(); icons.Flush(); decalMips.Flush(); decalBindingsFile.Flush(); workerState?.Flush(); nextFlush = Elapsed + 1000;
                        }
                        if (finished) break;
                        wake.WaitOne(250);
                    }
                }
            }
            catch (Exception failure) { lock (gate) { error = failure.GetType().Name + ": " + failure.Message; closed = true; } }
            finally { wake.Dispose(); }
        }

        internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        internal static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\"\"").Replace('\r', ' ').Replace('\n', ' ') + "\"";
    }

    internal sealed class HideoutResearchSpan
    {
        internal readonly HideoutResearchCapture Owner;
        internal readonly int Id;
        internal readonly string Stage;
        internal readonly double Started;
        internal readonly bool Entry;
        internal HideoutResearchSpan(HideoutResearchCapture owner, int id, string stage, double started, bool entry)
        { Owner = owner; Id = id; Stage = stage; Started = started; Entry = entry; }
        internal void Finish(string status) => Owner.Finish(this, status);
        internal void Observe(Task task)
        {
            Owner.LaunchReturned(this);
            if (task == null) { Finish("null-task"); return; }
            // Do not replace the Task or read Exception: preserve native await/fault semantics.
            task.ContinueWith(done => { try { Finish(done.IsCanceled ? "canceled" : done.IsFaulted ? "faulted" : "completed"); } catch { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
