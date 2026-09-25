using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Benchmarking;

public enum BenchmarkWorkload { FirstFrame, Sequential, Random, WarmNext, Preload, FileAction, Correctness }
public enum BenchmarkResultStatus { Pass, Warn, Fail, InsufficientData }

public sealed record BenchmarkProfile(
    string Id, string Name, string Description, LoadingMode LoadingMode,
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

/// <summary>What is wrong with a benchmark profile (stable code; the WPF window maps it to catalog text).</summary>
public enum BenchmarkProfileProblem
{
    MissingIdOrMode,
    InvalidSettings,
    /// <summary>The profile has no real check yet and refuses to run (a decode-only stand-in would misreport).</summary>
    NotImplemented,
}

/// <summary>A profile that cannot run. <see cref="Exception.Message"/> stays English (CLI, logs, reports);
/// <see cref="Problem"/> and <see cref="ProfileId"/> let the WPF window show translated text.</summary>
public sealed class BenchmarkProfileException(BenchmarkProfileProblem problem, string profileId, string message)
    : ArgumentException(message)
{
    public BenchmarkProfileProblem Problem { get; } = problem;
    public string ProfileId { get; } = profileId;
}

public static class BenchmarkProfileValidation
{
    public static void Validate(BenchmarkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id) || !Enum.IsDefined(profile.LoadingMode))
            throw new BenchmarkProfileException(BenchmarkProfileProblem.MissingIdOrMode, profile?.Id ?? string.Empty, "Profile id and loading mode are required");
        if (profile.Workers < 1 || profile.NextWindow < 0 || profile.PreviousWindow < 0 || profile.Iterations < 1)
            throw new BenchmarkProfileException(BenchmarkProfileProblem.InvalidSettings, profile.Id, $"Invalid benchmark settings for profile '{profile.Id}'");
    }
}

/// The executor must call the production decode/cache/navigation path and return
/// false when it only measured file I/O without presenting a decoded image.
public delegate Task<(bool Correct, ReviewMetricsSnapshot? Metrics)> BenchmarkWorkloadExecutor(
    BenchmarkProfile profile, BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);

/// <summary>
/// Two-step executor (R2-F-14): the returned task prepares one iteration (untimed setup such as copying a scratch file or
/// evicting caches) and yields the measure step; only that step is timed.
/// </summary>
public delegate Task<Func<Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>>> BenchmarkPreparedWorkloadExecutor(
    BenchmarkProfile profile, BenchmarkWorkload workload, int iteration, CancellationToken cancellationToken);

public sealed record BenchmarkSample(string ProfileId, BenchmarkWorkload Workload,
    double ElapsedMilliseconds, bool Correct, string? Error = null);

public sealed record BenchmarkPhaseResult(string ProfileId, BenchmarkWorkload Workload,
    IReadOnlyList<double> Samples, BenchmarkResultStatus Status, string? Message = null, int? ImagesPerSample = null)
{
    private readonly Lazy<double[]> _sorted = new(() => Samples.OrderBy(x => x).ToArray());

    public int Count => Samples.Count;
    public double P50 => BenchmarkStatistics.Percentile(_sorted.Value, .50);
    public double P95 => BenchmarkStatistics.Percentile(_sorted.Value, .95);
    public double P99 => BenchmarkStatistics.Percentile(_sorted.Value, .99);
    public double Max => Samples.Count == 0 ? 0 : _sorted.Value[^1];

    /// <summary>Images one sample decodes (see <see cref="BenchmarkWorkloadRunner.ImagesPerSample"/>): stamped by the runner,
    /// otherwise derived from the profile registry so a phase built elsewhere is still comparable.</summary>
    public int EffectiveImagesPerSample => Math.Max(1, ImagesPerSample
        ?? (BenchmarkProfiles.Find(ProfileId) is { } profile ? BenchmarkWorkloadRunner.ImagesPerSample(profile, Workload, int.MaxValue) : 1));

    /// <summary>P95 wall time divided by the images in a sample: the like-for-like unit for ranking.</summary>
    public double P95PerImage => P95 / EffectiveImagesPerSample;

    /// <summary>P50 wall time divided by the images in a sample.</summary>
    public double P50PerImage => P50 / EffectiveImagesPerSample;
}

public sealed record BenchmarkReport(string RunId, DateTimeOffset StartedUtc,
    string Folder, IReadOnlyList<BenchmarkPhaseResult> Phases,
    ReviewMetricsSnapshot? Metrics = null, string? Machine = null)
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, DefaultOptions);
}

/// <summary>Stable description of the input set used by a benchmark batch.</summary>
public sealed record BenchmarkDatasetManifest(
    string Folder,
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, int> UnsupportedExtensions,
    int FileLimit)
{
    public int SupportedFileCount => Files.Count;
    public int UnsupportedFileCount => UnsupportedExtensions.Values.Sum();
}

/// <summary>Batch summary that retains failed and unsupported profile outcomes.</summary>
public sealed record BenchmarkBatchSummary(
    BenchmarkDatasetManifest Dataset,
    IReadOnlyList<BenchmarkReport> Reports,
    IReadOnlyList<BenchmarkPhaseResult> ProfileOutcomes)
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, DefaultOptions);
}

/// <summary>Ranks by per-image latency: one sample of a parallel workload decodes Workers images, others decode one.</summary>
public static class BenchmarkRanking
{
    public static IReadOnlyList<BenchmarkPhaseResult> Rank(IEnumerable<BenchmarkPhaseResult> phases, BenchmarkWorkload workload)
        => phases.Where(x => x.Workload == workload && x.Status is not BenchmarkResultStatus.Fail
                             && x.Status is not BenchmarkResultStatus.InsufficientData)
                 .OrderBy(x => x.P95PerImage).ThenBy(x => x.P50PerImage).ToArray();
}
