using System.Diagnostics;
using System.IO;

namespace PhotoReview.App;

public sealed record BenchmarkProgress(string ProfileId, BenchmarkWorkload Workload, int Completed, int Total, string Message);

/// <summary>Runs measurable workloads through injected operations so the UI and CLI share one contract.</summary>
public sealed class BenchmarkEngine
{
    public async Task<BenchmarkReport> RunAsync(string folder, BenchmarkProfile profile,
        Func<BenchmarkProfile, BenchmarkWorkload, int, CancellationToken, Task<(bool Correct, ReviewMetricsSnapshot? Metrics)>> operation,
        IProgress<BenchmarkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Folder is required", nameof(folder));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var started = DateTimeOffset.UtcNow;
        var samples = new List<double>();
        ReviewMetricsSnapshot? metrics = null;
        var total = Math.Max(1, profile.Iterations);
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var result = await operation(profile, profile.Workload, i, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            metrics ??= result.Metrics;
            progress?.Report(new(profile.Id, profile.Workload, i + 1, total, result.Correct ? "OK" : "Correctness failed"));
        }
        var correct = samples.Count > 0;
        var phase = new BenchmarkPhaseResult(profile.Id, profile.Workload, samples,
            correct ? BenchmarkResultStatus.Pass : BenchmarkResultStatus.InsufficientData);
        return new BenchmarkReport(Guid.NewGuid().ToString("N"), started, Path.GetFullPath(folder), [phase], metrics, Environment.MachineName);
    }
}
