using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Diagnostics;

public sealed class ReviewMetrics
{
    private long _cacheHits;
    private long _cacheMisses;
    private long _sourceBytesRead;
    private long _sourceReads;
    private long _decodeMilliseconds;
    private long _presentedImages;
    private long _presentMilliseconds;
    private long _preloadHits;
    private long _inflightJoins;
    private long _diskCacheHits;
    private long _queueWaitMilliseconds;
    private long _uiAssignMilliseconds;
    private readonly ConcurrentDictionary<DecoderBackend, long> _decoderFallbacks = new();
    private long _sourceOpenCount;
    private long _statCount;
    private long _sessionWriteCount;
    private readonly ConcurrentDictionary<string, long> _sourceOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly long[] _presentBuckets = new long[PresentBucketLabels.Length];

    /// <summary>Upper bounds (ms, inclusive) of the present-latency histogram; the last bucket is unbounded.</summary>
    public static readonly IReadOnlyList<long> PresentBucketUpperBoundsMs = [8, 16, 33, 50, 100, 200, 500];
    private static readonly string[] PresentBucketLabels = ["<=8", "<=16", "<=33", "<=50", "<=100", "<=200", "<=500", ">500"];

    public void RecordPreloadHit() => Interlocked.Increment(ref _preloadHits);
    public void RecordInflightJoin() => Interlocked.Increment(ref _inflightJoins);
    public void RecordDiskCacheHit() => Interlocked.Increment(ref _diskCacheHits);
    public void RecordQueueWait(long milliseconds) => Interlocked.Add(ref _queueWaitMilliseconds, Math.Max(0, milliseconds));
    public void RecordUiAssign(long milliseconds) => Interlocked.Add(ref _uiAssignMilliseconds, Math.Max(0, milliseconds));

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public void RecordDecoderFallback(DecoderBackend backend) =>
        _decoderFallbacks.AddOrUpdate(backend, 1, (_, count) => count + 1);

    public void RecordSourceRead(long bytes, long milliseconds)
    {
        Interlocked.Increment(ref _sourceReads);
        Interlocked.Add(ref _sourceBytesRead, bytes);
        Interlocked.Add(ref _decodeMilliseconds, milliseconds);
    }

    public void RecordPresented(long milliseconds)
    {
        Interlocked.Increment(ref _presentedImages);
        Interlocked.Add(ref _presentMilliseconds, milliseconds);
        Interlocked.Increment(ref _presentBuckets[PresentBucketIndex(milliseconds)]);
    }

    /// <summary>Counts one open of a source image file (total and per path).</summary>
    public void RecordSourceOpen(string path)
    {
        Interlocked.Increment(ref _sourceOpenCount);
        _sourceOpens.AddOrUpdate(path, 1, (_, count) => count + 1);
    }

    /// <summary>Counts one file-metadata query (stat / exists) issued through the counting file system.</summary>
    public void RecordStat() => Interlocked.Increment(ref _statCount);

    public void RecordSessionWrite() => Interlocked.Increment(ref _sessionWriteCount);

    private static int PresentBucketIndex(long milliseconds)
    {
        for (var i = 0; i < PresentBucketUpperBoundsMs.Count; i++)
            if (milliseconds <= PresentBucketUpperBoundsMs[i]) return i;
        return PresentBucketUpperBoundsMs.Count;
    }

    public ReviewMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _cacheHits), Interlocked.Read(ref _cacheMisses),
        Interlocked.Read(ref _sourceBytesRead), Interlocked.Read(ref _sourceReads), Interlocked.Read(ref _decodeMilliseconds),
        Interlocked.Read(ref _presentedImages), Interlocked.Read(ref _presentMilliseconds))
    {
        PreloadHits = Interlocked.Read(ref _preloadHits),
        InflightJoins = Interlocked.Read(ref _inflightJoins),
        DiskCacheHits = Interlocked.Read(ref _diskCacheHits),
        QueueWaitMilliseconds = Interlocked.Read(ref _queueWaitMilliseconds),
        UiAssignMilliseconds = Interlocked.Read(ref _uiAssignMilliseconds),
        DecoderFallbacks = new Dictionary<DecoderBackend, long>(_decoderFallbacks),
        SourceOpenCount = Interlocked.Read(ref _sourceOpenCount),
        TopSourceOpens = _sourceOpens.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(p => new SourceOpenEntry(p.Key, p.Value)).ToArray(),
        StatCount = Interlocked.Read(ref _statCount),
        SessionWriteCount = Interlocked.Read(ref _sessionWriteCount),
        PresentHistogram = PresentBucketLabels.Select((label, i) => new PresentLatencyBucket(label, Interlocked.Read(ref _presentBuckets[i]))).ToArray()
    };
}

public sealed record ReviewMetricsSnapshot(long CacheHits, long CacheMisses, long SourceBytesRead, long SourceReads, long DecodeMilliseconds, long PresentedImages, long PresentMilliseconds)
{
    public long PreloadHits { get; init; }
    public long InflightJoins { get; init; }
    public long DiskCacheHits { get; init; }
    public long QueueWaitMilliseconds { get; init; }
    public long UiAssignMilliseconds { get; init; }
    public IReadOnlyDictionary<DecoderBackend, long> DecoderFallbacks { get; init; } = new Dictionary<DecoderBackend, long>();
    /// <summary>Total decoder fallbacks across all backends.</summary>
    public long DecoderFallbackCount => DecoderFallbacks.Values.Sum();
    public long SourceOpenCount { get; init; }
    public IReadOnlyList<SourceOpenEntry> TopSourceOpens { get; init; } = [];
    public long StatCount { get; init; }
    public long SessionWriteCount { get; init; }
    public IReadOnlyList<PresentLatencyBucket> PresentHistogram { get; init; } = [];
}

public sealed record SourceOpenEntry(string Path, long Count);

public sealed record PresentLatencyBucket(string Label, long Count);
