using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        DecoderFallbacks = new Dictionary<DecoderBackend, long>(_decoderFallbacks)
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
}
