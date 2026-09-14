using System.Diagnostics;

namespace PhotoReview.App;

public sealed class ReviewMetrics
{
    private long _cacheHits;
    private long _cacheMisses;
    private long _sourceBytesRead;
    private long _sourceReads;
    private long _decodeMilliseconds;
    private long _presentedImages;
    private long _presentMilliseconds;

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);
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
        Interlocked.Read(ref _presentedImages), Interlocked.Read(ref _presentMilliseconds));
}

public sealed record ReviewMetricsSnapshot(long CacheHits, long CacheMisses, long SourceBytesRead, long SourceReads, long DecodeMilliseconds, long PresentedImages, long PresentMilliseconds);
