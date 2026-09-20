using System;
using System.Collections.Generic;
using System.Linq;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.TestSupport;

/// <summary>
/// TC02: Captures source-read metrics before/after a test scenario to verify disk I/O bounds.
/// Wraps CountingFileSystem and ReviewMetrics.Snapshot() to detect unmeasured read paths.
/// </summary>
public sealed class ReadBudgetProbe
{
    public struct Snapshot
    {
        public long SourceReads { get; set; }
        public long SourceBytesRead { get; set; }
        public long SourceOpenCount { get; set; }
        public long StatCount { get; set; }
        public long PreloadHits { get; set; }
        public long DiskCacheHits { get; set; }
        public DateTime CapturedAt { get; set; }

        public long SourceReadsDelta(Snapshot before) => SourceReads - before.SourceReads;
        public long StatCountDelta(Snapshot before) => StatCount - before.StatCount;

        public override string ToString() =>
            $"reads={SourceReads} bytes={SourceBytesRead} opens={SourceOpenCount} " +
            $"stat={StatCount} preloadHit={PreloadHits} diskCacheHit={DiskCacheHits}";
    }

    private readonly IFileSystem _fileSystem;
    private readonly dynamic _metrics; // ReviewMetrics via reflection to avoid coupling

    public ReadBudgetProbe(IFileSystem fileSystem, object metricsObject)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _metrics = metricsObject ?? throw new ArgumentNullException(nameof(metricsObject));
    }

    /// <summary>Capture current read metrics as a baseline.</summary>
    public Snapshot Capture()
    {
        var snap = (dynamic)_metrics.Snapshot();
        return new Snapshot
        {
            SourceReads = snap.SourceReads ?? 0,
            SourceBytesRead = snap.SourceBytesRead ?? 0,
            SourceOpenCount = snap.SourceOpenCount ?? 0,
            StatCount = snap.StatCount ?? 0,
            PreloadHits = snap.PreloadHits ?? 0,
            DiskCacheHits = snap.DiskCacheHits ?? 0,
            CapturedAt = DateTime.UtcNow
        };
    }

    /// <summary>Assert source reads are bounded within a scenario (no regression).</summary>
    public void AssertSourceReadsDelta(Snapshot before, Snapshot after, long maxDelta, string context = "")
    {
        var delta = after.SourceReadsDelta(before);
        if (delta > maxDelta)
            throw new InvalidOperationException(
                $"{context}: source reads delta {delta} exceeds max {maxDelta}.\n" +
                $"Before: {before}\nAfter: {after}");
    }

    /// <summary>Assert stat calls are bounded (no pathological re-scanning).</summary>
    public void AssertStatCountDelta(Snapshot before, Snapshot after, long maxDelta, string context = "")
    {
        var delta = after.StatCountDelta(before);
        if (delta > maxDelta)
            throw new InvalidOperationException(
                $"{context}: stat count delta {delta} exceeds max {maxDelta}.\n" +
                $"Before: {before}\nAfter: {after}");
    }
}
