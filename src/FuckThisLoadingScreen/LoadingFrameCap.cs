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
        private readonly Func<int> readTarget, readVSync;
        private readonly Action<int> writeTarget, writeVSync;
        private FrameCapRow active;
        private int leases;
        internal LoadingFrameCap(Func<int> readTarget, Action<int> writeTarget, Func<int> readVSync, Action<int> writeVSync)
        { this.readTarget = readTarget; this.writeTarget = writeTarget; this.readVSync = readVSync; this.writeVSync = writeVSync; }

        internal FrameCapLease Enter(LoadingRun run, int target, double now)
        {
            if (active != null) { leases++; active.Leases++; return new FrameCapLease { Row = active }; }
            if (run.FrameCaps.Count >= 16) { run.DroppedFrameCaps++; return null; }
            var row = new FrameCapRow { StartMs = now, AppliedTarget = Math.Max(60, Math.Min(360, target)), Leases = 1 };
            run.FrameCaps.Add(row);
            bool changedVSync = false, changedTarget = false;
            try
            {
                row.BeforeTarget = readTarget(); row.BeforeVSync = readVSync();
                changedVSync = true; writeVSync(0);
                changedTarget = true; writeTarget(row.AppliedTarget);
                active = row; leases = 1;
                return new FrameCapLease { Row = row };
            }
            catch (Exception error)
            {
                row.Error = error.GetType().Name; row.Outcome = "apply-failed"; row.EndMs = now;
                try { if (changedTarget && readTarget() == row.AppliedTarget) writeTarget(row.BeforeTarget); }
                catch { row.Outcome = "rollback-failed"; }
                try { if (changedVSync && readVSync() == 0) writeVSync(row.BeforeVSync); }
                catch { row.Outcome = "rollback-failed"; }
                return null;
            }
        }
        internal void Leave(FrameCapLease lease, double now)
        {
            if (lease == null || lease.Closed) return;
            lease.Closed = true;
            if (!ReferenceEquals(active, lease.Row) || --leases > 0) return;
            FrameCapRow row = active; active = null; row.EndMs = now;
            bool external = false, error = false;
            try
            {
                row.ExitTarget = readTarget();
                if (row.ExitTarget == row.AppliedTarget) writeTarget(row.BeforeTarget); else external = true;
            }
            catch (Exception ex) { row.Error = ex.GetType().Name; error = true; }
            try
            {
                row.ExitVSync = readVSync();
                if (row.ExitVSync == 0) writeVSync(row.BeforeVSync); else external = true;
            }
            catch (Exception ex) { row.Error = ex.GetType().Name; error = true; }
            row.Outcome = error ? "restore-error" : external ? "external-change-preserved" : "restored";
        }
    }
}
