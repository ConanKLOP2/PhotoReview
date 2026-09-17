using System.Text.Json;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.App;

public enum BenchmarkWorkload { FirstFrame, Sequential, Random, WarmNext, Preload, FileAction, Correctness }
public enum BenchmarkResultStatus { Pass, Warn, Fail, InsufficientData }

public sealed record BenchmarkProfile(
    string Id, string Name, string Description, string LoadingMode,
    int Workers, int NextWindow, int PreviousWindow, bool FullFolder,
    long MemoryReserveBytes, bool DiskCache, bool DetailedLogging,
    BenchmarkWorkload Workload, int WarmupCount = 1, int Iterations = 30,
    bool CorrectnessOnly = false);

public static class BenchmarkProfileExtensions
{
    public static int TargetWidth(this BenchmarkProfile profile) => profile.Id switch
    {
        "preview-light" => 960,
        "preview-balanced" => 1600,
        "preview-quality" => 2400,
        "preview-high-quality" => 3200,
        "original-correctness" => 0,
        _ => 1920
    };
}

public static class BenchmarkProfileValidation
{
    public static void Validate(BenchmarkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.LoadingMode))
            throw new ArgumentException("Profile id and loading mode are required");
        if (profile.Workers < 1 || profile.NextWindow < 0 || profile.PreviousWindow < 0 || profile.Iterations < 1)
            throw new ArgumentException($"Invalid benchmark settings for profile '{profile.Id}'");
    }
}

/// The executor must call the production decode/cache/navigation path and return
/// false when it only measured file I/O without presenting a decoded image.
public delegate Task<(bool Correct, ReviewMetricsSnapshot? Metrics)> BenchmarkWorkloadExecutor(
    BenchmarkProfile profile, BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);

public sealed record BenchmarkSample(string ProfileId, BenchmarkWorkload Workload,
    double ElapsedMilliseconds, bool Correct, string? Error = null);

public sealed record BenchmarkPhaseResult(string ProfileId, BenchmarkWorkload Workload,
    IReadOnlyList<double> Samples, BenchmarkResultStatus Status, string? Message = null)
{
    private readonly Lazy<double[]> _sorted = new(() => Samples.OrderBy(x => x).ToArray());

    public int Count => Samples.Count;
    public double P50 => BenchmarkStatistics.Percentile(_sorted.Value, .50);
    public double P95 => BenchmarkStatistics.Percentile(_sorted.Value, .95);
    public double P99 => BenchmarkStatistics.Percentile(_sorted.Value, .99);
    public double Max => Samples.Count == 0 ? 0 : _sorted.Value[^1];
}

public sealed record BenchmarkReport(string RunId, DateTimeOffset StartedUtc,
    string Folder, IReadOnlyList<BenchmarkPhaseResult> Phases,
    ReviewMetricsSnapshot? Metrics = null, string? Machine = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public static class BenchmarkRanking
{
    public static IReadOnlyList<BenchmarkPhaseResult> Rank(IEnumerable<BenchmarkPhaseResult> phases, BenchmarkWorkload workload)
        => phases.Where(x => x.Workload == workload && x.Status is not BenchmarkResultStatus.Fail
                             && x.Status is not BenchmarkResultStatus.InsufficientData)
                 .OrderBy(x => x.P95).ThenBy(x => x.P50).ToArray();
}
