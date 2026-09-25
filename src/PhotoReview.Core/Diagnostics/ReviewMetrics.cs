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
    private long _decodeEwmaBits; // double bits of DecodeMillisecondsEwma (0 = no sample yet)
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
    private long _crossThreadPresentCount;
    // Per-path open counts behind TopSourceOpens. Bounded (see TrimSourceOpens): a long session over large folders
    // opens tens of thousands of distinct files, and Snapshot() copies and sorts this table.
    private readonly ConcurrentDictionary<string, long> _sourceOpens = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sourceOpensTrimGate = new();

    /// <summary>Most distinct paths the per-path open table keeps; past it the least-opened paths are dropped.</summary>
    public const int MaxTrackedSourceOpenPaths = 4096;

    /// <summary>Distinct paths currently tracked for <see cref="ReviewMetricsSnapshot.TopSourceOpens"/> (test/diagnostic).</summary>
    internal int TrackedSourceOpenPaths => _sourceOpens.Count;
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
        UpdateDecodeEwma(milliseconds);
    }

    /// <summary>Weight of the newest sample in <see cref="DecodeMillisecondsEwma"/>.</summary>
    public const double DecodeEwmaAlpha = 0.2;

    /// <summary>
    /// perf(preload): exponentially weighted moving average of source decode wall time (ms), as
    /// actually observed under the current load (contention included), or 0 before the first decode.
    /// PreloadScheduler combines it with the key rate to decide how far ahead a burst must preload.
    /// </summary>
    public double DecodeMillisecondsEwma => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _decodeEwmaBits));

    private void UpdateDecodeEwma(long milliseconds)
    {
        if (milliseconds <= 0) return;
        while (true)
        {
            var oldBits = Interlocked.Read(ref _decodeEwmaBits);
            var old = BitConverter.Int64BitsToDouble(oldBits);
            var next = old <= 0 ? milliseconds : old + DecodeEwmaAlpha * (milliseconds - old);
            if (Interlocked.CompareExchange(ref _decodeEwmaBits, BitConverter.DoubleToInt64Bits(next), oldBits) == oldBits) return;
        }
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
        if (_sourceOpens.Count > MaxTrackedSourceOpenPaths) TrimSourceOpens();
    }

    // Drops the least-opened paths (ties: arbitrary) down to 3/4 of the cap, so the sort runs once per
    // MaxTrackedSourceOpenPaths / 4 new paths, not on every open. Paths opened repeatedly -- the ones
    // TopSourceOpens exists to surface -- survive; SourceOpenCount (the total) is unaffected.
    private void TrimSourceOpens()
    {
        lock (_sourceOpensTrimGate)
        {
            if (_sourceOpens.Count <= MaxTrackedSourceOpenPaths) return; // another thread trimmed already
            var excess = _sourceOpens.Count - MaxTrackedSourceOpenPaths * 3 / 4;
            // TryRemove(pair) skips a path whose count changed meanwhile: it was just opened again.
            foreach (var entry in _sourceOpens.ToArray().OrderBy(p => p.Value).Take(excess))
                _sourceOpens.TryRemove(entry);
        }
    }

    /// <summary>Counts one file-metadata query (stat / exists) issued through the counting file system.</summary>
    public void RecordStat() => Interlocked.Increment(ref _statCount);

    public void RecordSessionWrite() => Interlocked.Increment(ref _sessionWriteCount);

    /// <summary>
    /// AR04 / ADR 0005: counts presentation-sink updates that arrived off the UI thread and had to be
    /// marshalled with a synchronous Dispatcher.Invoke. Expected to stay 0 once the App layer is UI-affine.
    /// </summary>
    public void RecordCrossThreadPresent() => Interlocked.Increment(ref _crossThreadPresentCount);

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
        // ConcurrentDictionary.ToArray() takes its own thread-safe snapshot; chaining Enumerable.OrderBy
        // directly on the dictionary instead would let LINQ's array-buffering optimization call
        // ICollection.CopyTo(array, 0) with a size from an earlier Count() -- if the dictionary grows
        // between those two calls (concurrent preload workers are still calling RecordSourceOpen), CopyTo
        // throws ArgumentException. Snapshot() is now polled much more often (perf harness key-settle,
        // ~every 10ms per key) while preload is actively writing to this dictionary, which made the race
        // easy to hit.
        TopSourceOpens = _sourceOpens.ToArray().OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(p => new SourceOpenEntry(p.Key, p.Value)).ToArray(),
        StatCount = Interlocked.Read(ref _statCount),
        SessionWriteCount = Interlocked.Read(ref _sessionWriteCount),
        CrossThreadPresentCount = Interlocked.Read(ref _crossThreadPresentCount),
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
    /// <summary>Sink updates marshalled via Dispatcher.Invoke because they arrived off the UI thread (AR04: expected 0).</summary>
    public long CrossThreadPresentCount { get; init; }
    public IReadOnlyList<PresentLatencyBucket> PresentHistogram { get; init; } = [];
}

public sealed record SourceOpenEntry(string Path, long Count);

public sealed record PresentLatencyBucket(string Label, long Count);
