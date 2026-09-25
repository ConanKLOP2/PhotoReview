using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;

namespace PhotoReview.Benchmarking;

/// <summary>Arithmetic on cumulative <see cref="ReviewMetricsSnapshot"/>s for benchmark reports.</summary>
internal static class BenchmarkMetrics
{
    /// <summary>
    /// What was recorded after <paramref name="baseline"/> was taken (e.g. excluding warm-up iterations).
    /// <see cref="ReviewMetricsSnapshot.TopSourceOpens"/> only lists the top 10 paths, so a path's count is reduced by
    /// its baseline count when the baseline listed it; paths with nothing left are dropped.
    /// </summary>
    public static ReviewMetricsSnapshot Since(ReviewMetricsSnapshot current, ReviewMetricsSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        var fallbacks = new Dictionary<DecoderBackend, long>();
        foreach (var (backend, count) in current.DecoderFallbacks)
        {
            var delta = count - baseline.DecoderFallbacks.GetValueOrDefault(backend);
            if (delta > 0) fallbacks[backend] = delta;
        }
        var baselineOpens = baseline.TopSourceOpens.ToDictionary(e => e.Path, e => e.Count, StringComparer.OrdinalIgnoreCase);
        var baselineBuckets = baseline.PresentHistogram.ToDictionary(b => b.Label, b => b.Count, StringComparer.Ordinal);
        return new ReviewMetricsSnapshot(
            current.CacheHits - baseline.CacheHits,
            current.CacheMisses - baseline.CacheMisses,
            current.SourceBytesRead - baseline.SourceBytesRead,
            current.SourceReads - baseline.SourceReads,
            current.DecodeMilliseconds - baseline.DecodeMilliseconds,
            current.PresentedImages - baseline.PresentedImages,
            current.PresentMilliseconds - baseline.PresentMilliseconds)
        {
            PreloadHits = current.PreloadHits - baseline.PreloadHits,
            InflightJoins = current.InflightJoins - baseline.InflightJoins,
            DiskCacheHits = current.DiskCacheHits - baseline.DiskCacheHits,
            QueueWaitMilliseconds = current.QueueWaitMilliseconds - baseline.QueueWaitMilliseconds,
            UiAssignMilliseconds = current.UiAssignMilliseconds - baseline.UiAssignMilliseconds,
            DecoderFallbacks = fallbacks,
            SourceOpenCount = current.SourceOpenCount - baseline.SourceOpenCount,
            TopSourceOpens = current.TopSourceOpens
                .Select(e => new SourceOpenEntry(e.Path, e.Count - baselineOpens.GetValueOrDefault(e.Path)))
                .Where(e => e.Count > 0)
                .OrderByDescending(e => e.Count).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StatCount = current.StatCount - baseline.StatCount,
            SessionWriteCount = current.SessionWriteCount - baseline.SessionWriteCount,
            CrossThreadPresentCount = current.CrossThreadPresentCount - baseline.CrossThreadPresentCount,
            PresentHistogram = current.PresentHistogram
                .Select(b => new PresentLatencyBucket(b.Label, b.Count - baselineBuckets.GetValueOrDefault(b.Label)))
                .ToArray(),
        };
    }
}
