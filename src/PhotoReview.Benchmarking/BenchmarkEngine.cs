using System.Diagnostics;
using System.IO;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Benchmarking;

public sealed record BenchmarkProgress(string ProfileId, BenchmarkWorkload Workload, int Completed, int Total, string Message);

/// <summary>Runs measurable workloads through injected operations so the UI and CLI share one contract.</summary>
public sealed class BenchmarkEngine
{
    // Fixed English (Q-L4): these also go to the report JSON and the CLI. The BenchmarkWindow maps them to
    // benchmark.progress.* / benchmark.result.* catalog keys for display.
    public const string ProgressOk = "OK";
    public const string ProgressCorrectnessFailed = "Correctness failed";
    public const string ResultCorrectnessFailed = "One or more samples failed correctness";

    public static async Task<BenchmarkReport> RunAsync(string folder, BenchmarkProfile profile,
        BenchmarkWorkloadExecutor operation,
        IProgress<BenchmarkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Folder is required", nameof(folder));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        BenchmarkProfileValidation.Validate(profile);
        ArgumentNullException.ThrowIfNull(operation);
        var started = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        FileLog.Default.Info($"Benchmark start runId={runId} profile={profile.Id} workload={profile.Workload} folder={Path.GetFullPath(folder)} iterations={profile.Iterations} workers={profile.Workers}");
        var samples = new List<double>();
        ReviewMetricsSnapshot? metrics = null;
        var allCorrect = true;
        var total = profile.Iterations;
        for (var w = 0; w < profile.WarmupCount; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await operation(profile, profile.Workload, w, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                FileLog.Default.Info($"Benchmark canceled runId={runId} profile={profile.Id} warmup={w}");
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Default.Error($"Benchmark warmup failed runId={runId} profile={profile.Id} warmup={w}", ex);
                throw;
            }
        }
        // Iterations counts only timed samples; WarmupCount runs are additional and excluded from samples.
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            (bool Correct, ReviewMetricsSnapshot? Metrics) result;
            try
            {
                result = await operation(profile, profile.Workload, i, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                FileLog.Default.Info($"Benchmark canceled runId={runId} profile={profile.Id} iteration={i}");
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Default.Error($"Benchmark phase failed runId={runId} profile={profile.Id} iteration={i}", ex);
                throw;
            }
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            metrics = result.Metrics ?? metrics;
            allCorrect &= result.Correct;
            FileLog.Default.Info($"Benchmark sample runId={runId} profile={profile.Id} phase={profile.Workload} iteration={i + 1}/{total} elapsedMs={sw.Elapsed.TotalMilliseconds:F1} correct={result.Correct}");
            progress?.Report(new(profile.Id, profile.Workload, i + 1, total, result.Correct ? ProgressOk : ProgressCorrectnessFailed));
        }
        var correct = samples.Count > 0 && allCorrect;
        var phase = new BenchmarkPhaseResult(profile.Id, profile.Workload, samples,
            samples.Count == 0 ? BenchmarkResultStatus.InsufficientData : correct ? BenchmarkResultStatus.Pass : BenchmarkResultStatus.Fail,
            correct ? null : ResultCorrectnessFailed);
        FileLog.Default.Info($"Benchmark complete runId={runId} profile={profile.Id} phase={profile.Workload} status={phase.Status} p50Ms={phase.P50:F1} p95Ms={phase.P95:F1} maxMs={phase.Max:F1}");
        return new BenchmarkReport(runId, started, Path.GetFullPath(folder), [phase], metrics, Environment.MachineName);
    }
}
