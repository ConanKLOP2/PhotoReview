using System.Globalization;

namespace PhotoReview.Benchmark.Cli.ShellHost;

/// <summary>Thống kê của perf host/probe WP-29 (không phụ thuộc WPF/Win32; test ở <c>ProbeStatisticsTests</c> + mutation).</summary>
internal static class ProbeStatistics
{
    /// <summary>Hệ số quy MAD về độ lệch chuẩn của phân bố chuẩn.</summary>
    internal const double MadScale = 1.4826;

    /// <summary>
    /// Percentile nội suy tuyến tính (kiểu Excel PERCENTILE.INC): <paramref name="percent"/> trong [0,100]; rỗng = NaN.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> values, double percent)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var p = Math.Clamp(percent, 0, 100);
        var sorted = values.Order().ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var rank = p / 100.0 * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var fraction = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    public static double Median(IReadOnlyList<double> values) => Percentile(values, 50);

    /// <summary>
    /// Bỏ ngoại lai PHÍA CAO (nhiễu khởi động/GC/tải nền chỉ làm chậm): giá trị &gt; median + k * 1,4826 * MAD. Cần &gt;= 5 mẫu và MAD &gt; 0;
    /// ngược lại giữ nguyên. Thứ tự đầu vào được giữ.
    /// </summary>
    public static IReadOnlyList<double> RemoveHighOutliers(IReadOnlyList<double> values, double k = 3.0)
    {
        if (values.Count < 5)
        {
            return values;
        }

        var median = Median(values);
        var mad = Median([.. values.Select(v => Math.Abs(v - median))]);
        if (mad <= 0)
        {
            return values;
        }

        var limit = median + (k * MadScale * mad);
        return [.. values.Where(v => v <= limit)];
    }

    public static SampleSummary Summarize(IReadOnlyList<double> values, bool removeOutliers = true)
    {
        var finite = values.Where(double.IsFinite).ToArray();
        var kept = removeOutliers ? RemoveHighOutliers(finite) : finite;
        if (kept.Count == 0)
        {
            return new SampleSummary(0, finite.Length, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        return new SampleSummary(
            kept.Count, finite.Length - kept.Count,
            kept.Min(), Median(kept), Percentile(kept, 95), kept.Max(), kept.Average());
    }

    /// <summary>So <paramref name="candidate"/> với <paramref name="baseline"/> theo median; hồi quy = delta &gt; max(percent, minAbsMs).</summary>
    public static Comparison Compare(IReadOnlyList<double> baseline, IReadOnlyList<double> candidate,
        double thresholdPercent = 5.0, double minAbsoluteMs = 1.0)
    {
        var b = Summarize(baseline);
        var c = Summarize(candidate);
        var delta = c.Median - b.Median;
        var percent = b.Median > 0 ? delta / b.Median * 100.0 : double.NaN;
        var allowed = Math.Max(Math.Abs(b.Median) * thresholdPercent / 100.0, minAbsoluteMs);
        var regression = double.IsFinite(delta) && delta > allowed;
        return new Comparison(b, c, delta, percent, regression);
    }

    public static string Format(double value, string format = "F1") =>
        double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "n/a";
}

/// <summary>Kết quả <see cref="ProbeStatistics.Summarize"/>: <c>Count</c> mẫu giữ lại, <c>Dropped</c> mẫu bị loại.</summary>
internal sealed record SampleSummary(int Count, int Dropped, double Min, double Median, double P95, double Max, double Mean);

internal sealed record Comparison(SampleSummary Baseline, SampleSummary Candidate, double DeltaMs, double DeltaPercent, bool IsRegression);

/// <summary>Một ô lịch đo: vòng, ứng dụng, có phải vòng khởi động (bỏ).</summary>
internal sealed record ProbeSlot(int Round, string App, bool IsWarmup);

internal static class ProbeSchedule
{
    /// <summary>
    /// Lịch xen kẽ: mỗi vòng mọi ứng dụng chạy đúng một lần theo thứ tự xáo (seed cố định = lịch lặp lại được), không ứng dụng nào
    /// chạy lần thứ hai trước khi mọi ứng dụng khác chạy; <paramref name="warmupRounds"/> vòng đầu là khởi động (bỏ khỏi số liệu).
    /// </summary>
    public static IReadOnlyList<ProbeSlot> Interleave(IReadOnlyList<string> apps, int rounds, int warmupRounds, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rounds);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupRounds);
        var random = new Random(seed);
        var slots = new List<ProbeSlot>();
        var total = rounds + warmupRounds;
        for (var round = 0; round < total; round++)
        {
            var order = apps.ToArray();
            for (var i = order.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            foreach (var app in order)
            {
                slots.Add(new ProbeSlot(round, app, round < warmupRounds));
            }
        }

        return slots;
    }
}
