using System.Diagnostics;
using System.Globalization;
using System.IO;
using PhotoReview.Core.Abstractions;
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

    /// <summary>
    /// R2-F-14: each iteration is prepared first (untimed: temp-file copies, cache eviction) and only the returned
    /// measure step is timed, so samples no longer contain their own setup I/O. <paramref name="timeProvider"/> exists
    /// for tests; production uses <see cref="TimeProvider.System"/>.
    /// </summary>
    public static async Task<BenchmarkReport> RunPreparedAsync(string folder, BenchmarkProfile profile,
        BenchmarkPreparedWorkloadExecutor operation,
        IProgress<BenchmarkProgress>? progress = null, TimeProvider? timeProvider = null,
        ILog? log = null, CancellationToken cancellationToken = default)
    {
        timeProvider ??= TimeProvider.System;
        log ??= FileLog.Default;
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Folder is required", nameof(folder));
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        BenchmarkProfileValidation.Validate(profile);
        ArgumentNullException.ThrowIfNull(operation);
        var started = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("N");
        log.Info($"Benchmark start runId={runId} profile={profile.Id} workload={profile.Workload} folder={Path.GetFullPath(folder)} iterations={profile.Iterations} workers={profile.Workers}");
        var samples = new List<double>();
        ReviewMetricsSnapshot? metrics = null;
        var allCorrect = true;
        var total = profile.Iterations;
        // Executors report cumulative metrics; the last warm-up's snapshot is the baseline subtracted from the
        // final one, so the report's reads/hits cover the measured iterations only.
        ReviewMetricsSnapshot? warmupBaseline = null;
        for (var w = 0; w < profile.WarmupCount; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var warmup = await operation(profile, profile.Workload, w, cancellationToken).ConfigureAwait(false);
                warmupBaseline = (await warmup().ConfigureAwait(false)).Metrics ?? warmupBaseline;
            }
            catch (OperationCanceledException)
            {
                log.Info($"Benchmark canceled runId={runId} profile={profile.Id} warmup={w}");
                throw;
            }
            catch (Exception ex)
            {
                log.Error($"Benchmark warmup failed runId={runId} profile={profile.Id} warmup={w}", ex);
                throw;
            }
        }
        // Iterations counts only timed samples; WarmupCount runs are additional and excluded from samples.
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (bool Correct, ReviewMetricsSnapshot? Metrics) result;
            TimeSpan elapsed;
            try
            {
                var measure = await operation(profile, profile.Workload, i, cancellationToken).ConfigureAwait(false);
                var started0 = timeProvider.GetTimestamp();
                result = await measure().ConfigureAwait(false);
                elapsed = timeProvider.GetElapsedTime(started0);
            }
            catch (OperationCanceledException)
            {
                log.Info($"Benchmark canceled runId={runId} profile={profile.Id} iteration={i}");
                throw;
            }
            catch (Exception ex)
            {
                log.Error($"Benchmark phase failed runId={runId} profile={profile.Id} iteration={i}", ex);
                throw;
            }
            samples.Add(elapsed.TotalMilliseconds);
            metrics = result.Metrics ?? metrics;
            allCorrect &= result.Correct;
            log.Info($"Benchmark sample runId={runId} profile={profile.Id} phase={profile.Workload} iteration={i + 1}/{total} elapsedMs={Ms(elapsed.TotalMilliseconds)} correct={result.Correct}");
            progress?.Report(new(profile.Id, profile.Workload, i + 1, total, result.Correct ? ProgressOk : ProgressCorrectnessFailed));
        }
        if (metrics is not null && warmupBaseline is not null) metrics = BenchmarkMetrics.Since(metrics, warmupBaseline);
        var correct = samples.Count > 0 && allCorrect;
        var phase = new BenchmarkPhaseResult(profile.Id, profile.Workload, samples,
            samples.Count == 0 ? BenchmarkResultStatus.InsufficientData : correct ? BenchmarkResultStatus.Pass : BenchmarkResultStatus.Fail,
            correct ? null : ResultCorrectnessFailed);
        log.Info($"Benchmark complete runId={runId} profile={profile.Id} phase={profile.Workload} status={phase.Status} p50Ms={Ms(phase.P50)} p95Ms={Ms(phase.P95)} maxMs={Ms(phase.Max)}");
        return new BenchmarkReport(runId, started, Path.GetFullPath(folder), [phase], metrics, Environment.MachineName);
    }

    // Log lines are diagnostic files: never the user's decimal separator (AGENTS rule 4).
    private static string Ms(double value) => value.ToString("F1", CultureInfo.InvariantCulture);
}
