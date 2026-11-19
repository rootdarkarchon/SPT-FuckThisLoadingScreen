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
        private int _decalRows, _decalBindings;
        private double _lastDecal = -10000;
        internal const string EventHeader = "elapsed_ms,span_id,stage,event,status,duration_ms,detail";
        private readonly object _gate = new object();
        private readonly Queue<(byte Kind, string Row)> _queue = new Queue<(byte, string)>();
        private readonly Dictionary<int, HideoutResearchSpan> _open = new Dictionary<int, HideoutResearchSpan>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Func<double> _clock;
        private readonly Func<string> _workerSnapshot;
        private readonly Thread _writer;
        private readonly double _started;
        private double _lastMain, _lastActivity, _entryFinished;
        private int _spanCount, _sampleCount, _dropped, _entries, _logCount, _iconRows;
        private int _activeIcons;
        private bool _entrySeen, _closed;
        private string _closeReason, _error;
        internal readonly string DirectoryPath;
        internal static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        internal double Elapsed => _clock() - _started;
        internal bool Closed { get { lock (_gate) return _closed; } }
        internal string Error { get { lock (_gate) return _error; } }

        internal HideoutResearchCapture(string directory, string metadata, string sampleHeader, Func<double> clock = null, Func<string> workerSnapshot = null)
        {
            DirectoryPath = directory;
            _clock = clock ?? (() => Now);
            _workerSnapshot = workerSnapshot;
            _started = _clock();
            _writer = new Thread(() => Write(metadata, sampleHeader)) { IsBackground = true, Name = "FTLS hideout research writer" };
            _writer.Start();
        }

        internal HideoutResearchSpan Begin(string stage, bool entry = false, string detail = "")
        {
            lock (_gate)
            {
                if (_closed)
                    return null;
                if (_spanCount >= MaxSpans)
                { _dropped++; return null; }
                var span = new HideoutResearchSpan(this, ++_spanCount, stage, Elapsed, entry);
                _open.Add(span.Id, span);
                _lastActivity = Elapsed;
                if (entry)
                { _entrySeen = true; _entries++; }
                Event(span, "begin", "", "", detail);
                return span;
            }
        }

        internal void Finish(HideoutResearchSpan span, string status)
        {
            lock (_gate)
            {
                if (_closed || !_open.Remove(span.Id))
                    return;
                _lastActivity = Elapsed;
                if (span.Entry && --_entries == 0)
                    _entryFinished = Elapsed;
                Event(span, "end", status, Number(Elapsed - span.Started), "");
            }
        }

        internal void LaunchReturned(HideoutResearchSpan span)
        {
            lock (_gate)
            if (!_closed)
                Event(span, "launch-return", "", Number(Elapsed - span.Started), "Synchronous kickoff only; task lifetime is the end row.");
        }

        internal void Pulse() { lock (_gate) if (!_closed) _lastMain = Elapsed; }
        internal void Sample(string values)
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                if (_sampleCount >= MaxSamples)
                { _dropped++; return; }
                _sampleCount++;
                Enqueue(true, Number(Elapsed) + "," + values);
            }
        }
        internal void Note(string stage, string detail)
        { lock (_gate) if (!_closed) Enqueue(false, Number(Elapsed) + ",0," + Quote(stage) + ",note,,," + Quote(detail)); }
        internal void StreamingLog(string message)
        { lock (_gate) if (!_closed && _logCount++ < 50) Note("streaming-log", message.Length <= 1024 ? message : message.Substring(0, 1024)); }
        internal bool ReserveIconRow()
        {
            lock (_gate)
            {
                if (_closed)
                    return false;
                if (_iconRows >= MaxIconRows)
                { _dropped++; return false; }
                _iconRows++;
                return true;
            }
        }
        internal void IconRow(string values)
        { lock (_gate) if (!_closed) { _lastActivity = Elapsed; EnqueueKind(2, Number(Elapsed) + "," + values); } }
        internal bool EnterIcon()
        { lock (_gate) { if (_closed) return false; _activeIcons++; _lastActivity = Elapsed; return true; } }
        internal void ExitIcon()
        { lock (_gate) { _activeIcons = Math.Max(0, _activeIcons - 1); _lastActivity = Elapsed; if (_entrySeen && _entries == 0) _entryFinished = Elapsed; } Signal(); }
        internal void DecalActivity() { lock (_gate) if (!_closed) { _lastDecal = Elapsed; _lastActivity = Elapsed; } }
        internal void DecalRow(bool binding, string values)
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                if (binding ? _decalBindings >= MaxDecalBindings : _decalRows >= MaxDecalRows)
                { _dropped++; return; }
                if (binding)
                    _decalBindings++;
                else
                    _decalRows++;
                EnqueueKind(binding ? (byte)4 : (byte)3, Number(Elapsed) + "," + values);
            }
        }

        internal void Close(string reason)
        {
            lock (_gate)
            if (!_closed)
            { _closeReason = reason; Seal(reason); }
            Signal();
        }
        internal bool Join(int milliseconds) => _writer.Join(milliseconds);
        private void Seal(string reason)
        {
            foreach (var span in _open.Values)
                Event(span, "end", "incomplete-at-" + reason, Number(Elapsed - span.Started), "No completion was observed.");
            _open.Clear();
            Enqueue(false, Number(Elapsed) + ",0,capture,end," + Quote(reason) + ",," + Quote("dropped_rows=" + _dropped));
            _closed = true;
        }
        private void Event(HideoutResearchSpan span, string kind, string status, string duration, string detail) =>
            Enqueue(false, Number(Elapsed) + "," + span.Id + "," + Quote(span.Stage) + "," + kind + "," + Quote(status) + "," + duration + "," + Quote(detail));
        private void Enqueue(bool sample, string row)
            => EnqueueKind(sample ? (byte)1 : (byte)0, row);
        private void EnqueueKind(byte kind, string row)
        {
            if (_queue.Count >= MaxQueue)
            { _dropped++; return; }
            _queue.Enqueue((kind, row));
            Signal();
        }
        private void Signal() { try { _wake.Set(); } catch (ObjectDisposedException) { } }

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
                using (var workerState = _workerSnapshot == null ? null : new StreamWriter(Path.Combine(DirectoryPath, "worker.csv"), false, new UTF8Encoding(false)))
                {
                    events.WriteLine(EventHeader);
                    samples.WriteLine(sampleHeader);
                    icons.WriteLine(IconMipProbe.Header);
                    decalMips.WriteLine(DecalMipProbe.MipHeader);
                    decalBindingsFile.WriteLine(DecalMipProbe.BindingHeader);
                    workerState?.WriteLine(HideoutDecalState.Header);
                    heartbeat.WriteLine("elapsed_ms,last_main_ms,main_silence_ms,open_spans,samples,dropped_rows,active_stages,status");
                    double nextFlush = 0;
                    while (true)
                    {
                        bool finished;
                        lock (_gate)
                        {
                            double now = Elapsed;
                            if (!_closed && now >= 600000)
                            { _closeReason = "timeout-600s"; Seal(_closeReason); }
                            if (!_closed && _entrySeen && _entries == 0 && _activeIcons == 0 && now - _entryFinished >= 10000 && now - _lastDecal >= 10000)
                            { _closeReason = "entry-tail-complete"; Seal(_closeReason); }
                            if (!_closed && !_entrySeen && _activeIcons == 0 && _open.Count == 0 && now - _lastActivity >= 30000)
                            { _closeReason = "preload-idle"; Seal(_closeReason); }
                            finished = _closed;
                        }
                        // Never hold the producer lock while doing disk I/O.
                        for (int i = 0; i < MaxQueue; i++)
                        {
                            (byte Kind, string Row) row;
                            lock (_gate)
                            { if (_queue.Count == 0) break; row = _queue.Dequeue(); }
                            (row.Kind == 4 ? decalBindingsFile : row.Kind == 3 ? decalMips : row.Kind == 2 ? icons : row.Kind == 1 ? samples : events).WriteLine(row.Row);
                        }
                        if (Elapsed >= nextFlush || finished)
                        {
                            string row;
                            lock (_gate)
                            {
                                var stages = new List<string>();
                                foreach (var span in _open.Values)
                                { if (stages.Count == 16) break; stages.Add(span.Id + ":" + span.Stage); }
                                row = Number(Elapsed) + "," + Number(_lastMain) + "," + Number(Elapsed - _lastMain) + "," + _open.Count + "," + _sampleCount + "," + _dropped + "," + Quote(string.Join(" | ", stages)) + "," + Quote(_closeReason ?? "recording");
                            }
                            heartbeat.WriteLine(row);
                            if (workerState != null)
                                workerState.WriteLine(Number(Elapsed) + "," + _workerSnapshot());
                            events.Flush();
                            samples.Flush();
                            heartbeat.Flush();
                            icons.Flush();
                            decalMips.Flush();
                            decalBindingsFile.Flush();
                            workerState?.Flush();
                            nextFlush = Elapsed + 1000;
                        }
                        if (finished)
                            break;
                        _wake.WaitOne(250);
                    }
                }
            }
            catch (Exception failure) { lock (_gate) { _error = failure.GetType().Name + ": " + failure.Message; _closed = true; } }
            finally { _wake.Dispose(); }
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
            if (task == null)
            { Finish("null-task"); return; }
            // Do not replace the Task or read Exception: preserve native await/fault semantics.
            task.ContinueWith(done => { try { Finish(done.IsCanceled ? "canceled" : done.IsFaulted ? "faulted" : "completed"); } catch { } },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
