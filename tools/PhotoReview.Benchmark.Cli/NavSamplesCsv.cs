using System.Globalization;
using System.Text;
using PhotoReview.PerfAnalysis;

namespace PhotoReview.Benchmark.Cli;

/// <summary>
/// <c>--perf-analyze</c> side output <c>navs.csv</c>: one row per navigation of every group (the per-key latencies that
/// summary.json only reports as P50/P95). tools/diag/tune-rank.ps1 pools these across the runs of a config, because the P95
/// of one 40-100 key run varies ~3x at an identical config (docs/refactoring/perf/2026-10-06-s0-noise-floor.md).
/// Columns: group, nav, kind, complete (1 = a final Presented row was matched), firstVisualMs, finalVisualMs (empty when absent).
/// </summary>
internal static class NavSamplesCsv
{
    internal const string Header = "group,nav,kind,complete,firstVisualMs,finalVisualMs";

    internal static string Build(IEnumerable<GroupSummary> groups)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);
        foreach (var g in groups)
        {
            var key = g.Key.ToString().Replace(',', ';');
            foreach (var n in g.Navs.OrderBy(n => n.Nav))
            {
                sb.Append(key).Append(',')
                    .Append(n.Nav.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(n.Kind.Replace(',', ';')).Append(',')
                    .Append(n.Incomplete ? '0' : '1').Append(',')
                    .Append(Ms(n.FirstVisualMs)).Append(',')
                    .AppendLine(Ms(n.FinalVisualMs));
            }
        }
        return sb.ToString();
    }

    internal static void Write(string runDir, PerfAnalyze.AnalysisResult analysis) =>
        File.WriteAllText(Path.Combine(runDir, "navs.csv"), Build(analysis.Groups.Select(g => g.Summary)), new UTF8Encoding(false));

    private static string Ms(double? v) => v is { } d && !double.IsNaN(d) ? d.ToString("R", CultureInfo.InvariantCulture) : "";
}
