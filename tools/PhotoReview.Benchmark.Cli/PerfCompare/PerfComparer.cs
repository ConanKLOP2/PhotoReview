using System.Globalization;
using System.Text;
using PhotoReview.Benchmark.Cli.ShellHost;

namespace PhotoReview.Benchmark.Cli.PerfCompare;

/// <summary>Thứ tự = mức nghiêm trọng (kết quả tổng = max).</summary>
internal enum PerfVerdict
{
    Pass = 0,
    Warn = 1,
    Inconclusive = 2,
    Regression = 3,
}

/// <summary>
/// Ngưỡng so sánh. Mặc định theo noise floor của harness (docs/refactoring/perf/2026-10-06-s0-noise-floor.md): hồi quy khi chậm hơn
/// max(5 % median baseline, sàn tuyệt đối theo đơn vị) VÀ cận dưới khoảng tin cậy 95 % của hiệu median vẫn vượt ngưỡng đó.
/// </summary>
internal sealed record PerfCompareOptions(
    double ThresholdPercent = 5.0,
    double MinAbsMs = 1.0,
    double MinAbsMb = 16.0,
    double MinAbsCount = 1.0,
    double MinAbsPercent = 2.0,
    double BusyCpuPercent = 60.0,
    double MaxCoefficientOfVariation = 0.25,
    int MinSamples = 3,
    double Z = 1.96)
{
    public double MinAbsFor(string unit) => unit switch
    {
        "ms" => MinAbsMs,
        "MB" => MinAbsMb,
        "count" => MinAbsCount,
        "%" => MinAbsPercent,
        _ => 0,
    };
}

/// <summary>Một dòng so sánh. <c>Worse</c> &gt; 0 nghĩa là bản hiện tại XẤU hơn baseline (đã tính hướng của chỉ số).</summary>
internal sealed record MetricComparison(
    string Name, string Unit, PerfVerdict Verdict, string Reason, int BaseN, int CurN,
    double BaseMedian, double CurMedian, double Worse, double WorsePercent, double Allowed, double LowerBound, double UpperBound,
    bool Improved, int DroppedBase, int DroppedCur);

internal sealed record PerfComparisonResult(PerfVerdict Overall, IReadOnlyList<MetricComparison> Metrics, IReadOnlyList<string> ContextNotes)
{
    /// <summary>0 = PASS/WARN, 1 = hồi quy chắc chắn, 3 = INCONCLUSIVE (2 dành cho lỗi dùng/schema).</summary>
    public int ExitCode => Overall switch
    {
        PerfVerdict.Regression => 1,
        PerfVerdict.Inconclusive => 3,
        _ => 0,
    };
}

internal static class PerfComparer
{
    private const double MedianStdErrorFactor = 1.2533;   // sqrt(pi/2): sai số chuẩn của median so với của trung bình

    public static PerfComparisonResult Compare(PerfResult baseline, PerfResult current, PerfCompareOptions? options = null)
    {
        options ??= new PerfCompareOptions();
        if (!string.Equals(baseline.Scenario, current.Scenario, StringComparison.Ordinal))
        {
            throw new PerfSchemaException($"scenarios differ: baseline '{baseline.Scenario}' vs current '{current.Scenario}' (not comparable)");
        }

        var notes = ContextNoise(baseline, current, options);
        var currentByName = current.Metrics.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var rows = new List<MetricComparison>();
        foreach (var b in baseline.Metrics.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (!currentByName.TryGetValue(b.Name, out var c))
            {
                rows.Add(Empty(b, PerfVerdict.Inconclusive, "metric missing in the current result"));
                continue;
            }

            rows.Add(CompareMetric(b, c, notes, options));
        }

        var baselineNames = baseline.Metrics.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var c in current.Metrics.Where(m => !baselineNames.Contains(m.Name)).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            rows.Add(Empty(c, PerfVerdict.Pass, "new metric (no baseline yet)"));
        }

        if (rows.Count == 0)
        {
            throw new PerfSchemaException("no metrics to compare");
        }

        var overall = rows.Max(r => r.Verdict);
        return new PerfComparisonResult(overall, rows, notes);
    }

    private static MetricComparison Empty(PerfMetric m, PerfVerdict verdict, string reason) =>
        new(m.Name, m.Unit, verdict, reason, 0, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false, 0, 0);

    /// <summary>Lý do làm kết quả "không chắc" ở mức tệp: máy bận, khác máy/tham số, có lần chạy lỗi/bị kill.</summary>
    internal static List<string> ContextNoise(PerfResult baseline, PerfResult current, PerfCompareOptions options)
    {
        var notes = new List<string>();
        foreach (var (label, r) in new[] { ("baseline", baseline), ("current", current) })
        {
            if (double.IsFinite(r.Background.CpuPercentMedian) && r.Background.CpuPercentMedian > options.BusyCpuPercent)
            {
                notes.Add($"{label}: machine busy (median CPU {r.Background.CpuPercentMedian.ToString("F0", CultureInfo.InvariantCulture)} % > {options.BusyCpuPercent.ToString("F0", CultureInfo.InvariantCulture)} %)");
            }

            foreach (var flag in r.Background.NoiseFlags.Where(f => f is "failed-runs" or "forced-kill"))
            {
                notes.Add($"{label}: noise flag {flag}");
            }
        }

        if (!string.Equals(baseline.Machine.Name, current.Machine.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseline.Machine.Cpu, current.Machine.Cpu, StringComparison.OrdinalIgnoreCase)
            || baseline.Machine.LogicalCores != current.Machine.LogicalCores)
        {
            notes.Add($"different machine (baseline '{baseline.Machine.Name}' / '{baseline.Machine.Cpu}' vs current '{current.Machine.Name}' / '{current.Machine.Cpu}')");
        }

        foreach (var key in baseline.Parameters.Keys.Intersect(current.Parameters.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!string.Equals(baseline.Parameters[key], current.Parameters[key], StringComparison.Ordinal))
            {
                notes.Add($"parameter '{key}' differs (baseline {baseline.Parameters[key]} vs current {current.Parameters[key]})");
            }
        }

        return notes;
    }

    internal static MetricComparison CompareMetric(PerfMetric b, PerfMetric c, IReadOnlyList<string> contextNoise, PerfCompareOptions options)
    {
        // Hướng: chuẩn hoá để "lớn hơn = xấu hơn" (chỉ số higherIsBetter đổi dấu), rồi chỉ bỏ ngoại lai PHÍA XẤU (nhiễu chỉ làm xấu đi).
        var sign = b.LowerIsBetter ? 1.0 : -1.0;
        if (b.Direction != c.Direction)
        {
            return Empty(b, PerfVerdict.Inconclusive, "direction differs between baseline and current");
        }

        double[] Oriented(PerfMetric m)
        {
            var finite = m.Samples.Where(double.IsFinite).Select(v => v * sign).ToArray();
            if (finite.Length == 0 && double.IsFinite(m.SummaryP50))
            {
                return [m.SummaryP50 * sign];
            }

            return finite;
        }

        var bAll = Oriented(b);
        var cAll = Oriented(c);
        var bKept = ProbeStatistics.RemoveHighOutliers(bAll);
        var cKept = ProbeStatistics.RemoveHighOutliers(cAll);
        var droppedB = bAll.Length - bKept.Count;
        var droppedC = cAll.Length - cKept.Count;
        if (bKept.Count < options.MinSamples || cKept.Count < options.MinSamples)
        {
            return new MetricComparison(b.Name, b.Unit, PerfVerdict.Inconclusive,
                $"too few samples (baseline {bKept.Count}, current {cKept.Count}; need >= {options.MinSamples})", bKept.Count, cKept.Count,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false, droppedB, droppedC);
        }

        var bMed = ProbeStatistics.Median(bKept);
        var cMed = ProbeStatistics.Median(cKept);
        var sigmaB = Sigma(bKept);
        var sigmaC = Sigma(cKept);
        var worse = cMed - bMed;
        var se = MedianStdErrorFactor * Math.Sqrt((sigmaB * sigmaB / bKept.Count) + (sigmaC * sigmaC / cKept.Count));
        var lower = worse - (options.Z * se);
        var upper = worse + (options.Z * se);
        var percentAllowed = Math.Abs(bMed) * options.ThresholdPercent / 100.0;
        var allowed = Math.Max(percentAllowed, options.MinAbsFor(b.Unit));
        // Trong vùng sàn tuyệt đối (chỉ số rất nhỏ) mọi lệch dưới sàn là "không quan tâm": không cảnh báo trôi.
        var floorDominated = percentAllowed < options.MinAbsFor(b.Unit);
        var percent = Math.Abs(bMed) > 0 ? worse / Math.Abs(bMed) * 100.0 : double.NaN;

        PerfVerdict verdict;
        string reason;
        if (lower > allowed)
        {
            verdict = PerfVerdict.Regression;
            reason = "slower than the threshold and the 95 % interval stays above it";
        }
        else if (worse > allowed)
        {
            verdict = PerfVerdict.Warn;
            reason = "over the threshold but the 95 % interval reaches below it (noise)";
        }
        else if (!floorDominated && worse > allowed / 2 && lower > 0)
        {
            verdict = PerfVerdict.Warn;
            reason = "drifting: more than half the threshold and significantly above zero";
        }
        else
        {
            verdict = PerfVerdict.Pass;
            reason = worse < -allowed ? "improved" : "within the threshold";
        }

        var noise = new List<string>();
        if (contextNoise.Count > 0)
        {
            noise.Add("environment noise (see notes)");
        }

        var relativeSpread = Math.Abs(bMed) > 0 ? Math.Max(sigmaB, sigmaC) / Math.Abs(bMed) : 0;
        if (relativeSpread > options.MaxCoefficientOfVariation)
        {
            noise.Add($"high variance (spread {(relativeSpread * 100).ToString("F0", CultureInfo.InvariantCulture)} % of the median > {(options.MaxCoefficientOfVariation * 100).ToString("F0", CultureInfo.InvariantCulture)} %)");
        }

        if (verdict is PerfVerdict.Regression or PerfVerdict.Warn && noise.Count > 0)
        {
            // Không báo hồi quy giả khi nhiễu lớn: kết luận bị hạ xuống INCONCLUSIVE kèm lý do (một kết quả PASS vẫn là PASS).
            verdict = PerfVerdict.Inconclusive;
            reason = "would be: " + reason + " -- inconclusive because " + string.Join("; ", noise);
        }

        return new MetricComparison(b.Name, b.Unit, verdict, reason, bKept.Count, cKept.Count,
            bMed * sign, cMed * sign, worse, percent, allowed, lower, upper, worse < -allowed, droppedB, droppedC);
    }

    private static double Sigma(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var median = ProbeStatistics.Median(values);
        return ProbeStatistics.MadScale * ProbeStatistics.Median([.. values.Select(v => Math.Abs(v - median))]);
    }

    public static string ToMarkdown(PerfComparisonResult result, string baselineLabel, string currentLabel)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"PERF-COMPARE {baselineLabel} -> {currentLabel}: **{VerdictName(result.Overall)}**");
        foreach (var note in result.ContextNotes)
        {
            text.AppendLine("- note: " + note);
        }

        text.AppendLine();
        text.AppendLine("| Metric | Unit | Baseline median (n) | Current median (n) | Worse (+) | Worse % | Allowed | 95 % CI of worse | Verdict | Reason |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var r in result.Metrics)
        {
            string F(double v, string f = "F2") => ProbeStatistics.Format(v, f);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Name} | {r.Unit} | {F(r.BaseMedian)} ({r.BaseN}) | {F(r.CurMedian)} ({r.CurN}) | {F(r.Worse)} | {F(r.WorsePercent, "F1")} | {F(r.Allowed)} | [{F(r.LowerBound)}, {F(r.UpperBound)}] | {VerdictName(r.Verdict)} | {r.Reason}{(r.DroppedBase + r.DroppedCur > 0 ? $" (outliers dropped {r.DroppedBase}/{r.DroppedCur})" : "")} |"));
        }

        return text.ToString();
    }

    public static string VerdictName(PerfVerdict v) => v switch
    {
        PerfVerdict.Pass => "PASS",
        PerfVerdict.Warn => "WARN",
        PerfVerdict.Inconclusive => "INCONCLUSIVE",
        _ => "REGRESSION",
    };
}
