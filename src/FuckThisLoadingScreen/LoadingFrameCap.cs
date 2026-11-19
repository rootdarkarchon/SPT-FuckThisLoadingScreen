using System;

namespace FuckThisLoadingScreen
{
    internal sealed class FrameCapRow
    {
        internal double StartMs, EndMs;
        internal int BeforeTarget, BeforeVSync, AppliedTarget, ExitTarget, ExitVSync, Leases;
        internal string Outcome = "active", Error = "";
    }
    internal sealed class FrameCapLease
    {
        internal bool Closed;
        internal FrameCapRow Row;
    }

    // Only called on Unity's main thread by map-scope ownership. JobScheduler.FrameTicks
    // is deliberately untouched: this experiment changes render pacing alone.
    internal sealed class LoadingFrameCap
    {
        private readonly Func<int> _readTarget, _readVSync;
        private readonly Action<int> _writeTarget, _writeVSync;
        private FrameCapRow _active;
        private int _leases;
        internal LoadingFrameCap(Func<int> readTarget, Action<int> writeTarget, Func<int> readVSync, Action<int> writeVSync)
        { _readTarget = readTarget; _writeTarget = writeTarget; _readVSync = readVSync; _writeVSync = writeVSync; }

        internal FrameCapLease Enter(LoadingRun run, int target, double now)
        {
            if (_active != null)
            { _leases++; _active.Leases++; return new FrameCapLease { Row = _active }; }
            if (run.FrameCaps.Count >= 16)
            { run.DroppedFrameCaps++; return null; }
            var row = new FrameCapRow { StartMs = now, AppliedTarget = Math.Max(60, Math.Min(360, target)), Leases = 1 };
            run.FrameCaps.Add(row);
            bool changedVSync = false, changedTarget = false;
            try
            {
                row.BeforeTarget = _readTarget();
                row.BeforeVSync = _readVSync();
                changedVSync = true;
                _writeVSync(0);
                changedTarget = true;
                _writeTarget(row.AppliedTarget);
                _active = row;
                _leases = 1;
                return new FrameCapLease { Row = row };
            }
            catch (Exception error)
            {
                row.Error = error.GetType().Name;
                row.Outcome = "apply-failed";
                row.EndMs = now;
                try
                { if (changedTarget && _readTarget() == row.AppliedTarget) _writeTarget(row.BeforeTarget); }
                catch { row.Outcome = "rollback-failed"; }
                try
                { if (changedVSync && _readVSync() == 0) _writeVSync(row.BeforeVSync); }
                catch { row.Outcome = "rollback-failed"; }
                return null;
            }
        }
        internal void Leave(FrameCapLease lease, double now)
        {
            if (lease == null || lease.Closed)
                return;
            lease.Closed = true;
            if (!ReferenceEquals(_active, lease.Row) || --_leases > 0)
                return;
            FrameCapRow row = _active;
            _active = null;
            row.EndMs = now;
            bool external = false, error = false;
            try
            {
                row.ExitTarget = _readTarget();
                if (row.ExitTarget == row.AppliedTarget)
                    _writeTarget(row.BeforeTarget);
                else
                    external = true;
            }
            catch (Exception ex) { row.Error = ex.GetType().Name; error = true; }
            try
            {
                row.ExitVSync = _readVSync();
                if (row.ExitVSync == 0)
                    _writeVSync(row.BeforeVSync);
                else
                    external = true;
            }
            catch (Exception ex) { row.Error = ex.GetType().Name; error = true; }
            row.Outcome = error ? "restore-error" : external ? "external-change-preserved" : "restored";
        }
    }
}
