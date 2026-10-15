using System.Globalization;
using System.Text;

namespace FuckThisLoadingScreen
{
    internal sealed class SceneScheduleRow
    {
        internal double StartMs;
        internal double? EndMs;
        internal int Window, Requested, Submitted, Completed, Failed, Peak;
        internal string Outcome = "active";
    }
    internal static class ExperimentReports
    {
        private static string N(double value) => value.ToString("F3", CultureInfo.InvariantCulture);
        private static string Q(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        internal static string FrameCaps(LoadingRun run)
        {
            var csv = new StringBuilder("start_ms,end_ms,elapsed_ms,previous_target_fps,previous_vsync,requested_target_fps,exit_target_fps,exit_vsync,scope_leases,outcome,error\n");
            foreach (FrameCapRow row in run.FrameCaps)
                csv.Append(N(row.StartMs)).Append(',').Append(N(row.EndMs)).Append(',').Append(N(row.EndMs - row.StartMs)).Append(',')
                    .Append(row.BeforeTarget).Append(',').Append(row.BeforeVSync).Append(',').Append(row.AppliedTarget).Append(',')
                    .Append(row.Outcome == "apply-failed" || row.Outcome == "rollback-failed" ? "" : row.ExitTarget.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.Outcome == "apply-failed" || row.Outcome == "rollback-failed" ? "" : row.ExitVSync.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(row.Leases).Append(',').Append(Q(row.Outcome)).Append(',').Append(Q(row.Error)).Append('\n');
            return csv.ToString();
        }
        internal static string Schedules(LoadingRun run)
        {
            var csv = new StringBuilder("start_ms,end_ms,elapsed_ms,window,requested,submitted,completed,failed,peak_in_flight,outcome\n");
            lock (run) foreach (SceneScheduleRow row in run.Schedules)
                csv.Append(N(row.StartMs)).Append(',').Append(row.EndMs.HasValue ? N(row.EndMs.Value) : "").Append(',')
                    .Append(row.EndMs.HasValue ? N(row.EndMs.Value - row.StartMs) : "").Append(',').Append(row.Window).Append(',')
                    .Append(row.Requested).Append(',').Append(row.Submitted).Append(',').Append(row.Completed).Append(',').Append(row.Failed).Append(',').Append(row.Peak).Append(',')
                    .Append(Q(row.EndMs.HasValue ? row.Outcome : "incomplete-at-export")).Append('\n');
            return csv.ToString();
        }
    }
}
