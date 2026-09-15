using System.Text.Json;

namespace PhotoReview.App;

public enum BenchmarkWorkload { FirstFrame, Sequential, Random, WarmNext, Preload, FileAction, Correctness }
public enum BenchmarkResultStatus { Pass, Warn, Fail, InsufficientData }

public sealed record BenchmarkProfile(
    string Id, string Name, string Description, string LoadingMode,
    int Workers, int NextWindow, int PreviousWindow, bool FullFolder,
    long MemoryReserveBytes, bool DiskCache, bool DetailedLogging,
    BenchmarkWorkload Workload, int WarmupCount = 1, int Iterations = 30,
    bool CorrectnessOnly = false);

public sealed record BenchmarkSample(string ProfileId, BenchmarkWorkload Workload,
    double ElapsedMilliseconds, bool Correct, string? Error = null);

public sealed record BenchmarkPhaseResult(string ProfileId, BenchmarkWorkload Workload,
    IReadOnlyList<double> Samples, BenchmarkResultStatus Status, string? Message = null)
{
    public int Count => Samples.Count;
    public double P50 => BenchmarkStatistics.Percentile(Samples, .50);
    public double P95 => BenchmarkStatistics.Percentile(Samples, .95);
    public double P99 => BenchmarkStatistics.Percentile(Samples, .99);
    public double Max => Samples.Count == 0 ? 0 : Samples.Max();
}

public sealed record BenchmarkReport(string RunId, DateTimeOffset StartedUtc,
    string Folder, IReadOnlyList<BenchmarkPhaseResult> Phases,
    ReviewMetricsSnapshot? Metrics = null, string? Machine = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public static class BenchmarkStatistics
{
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        if (percentile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        var ordered = values.OrderBy(x => x).ToArray();
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }
}

public static class BenchmarkRanking
{
    public static IReadOnlyList<BenchmarkPhaseResult> Rank(IEnumerable<BenchmarkPhaseResult> phases, BenchmarkWorkload workload)
        => phases.Where(x => x.Workload == workload && x.Status is not BenchmarkResultStatus.Fail
                             && x.Status is not BenchmarkResultStatus.InsufficientData)
                 .OrderBy(x => x.P95).ThenBy(x => x.P50).ToArray();
}
