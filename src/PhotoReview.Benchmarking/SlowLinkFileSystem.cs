using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Benchmarking;

/// <summary>
/// Q-R29 measurement harness (NOT a production default -- only wired in by
/// <c>tools/PhotoReview.Benchmark.Cli</c>'s <c>--perf-session</c> when <c>--slow-link-*</c> is passed).
/// Decorates a real <see cref="IFileSystem"/> to simulate a slow NAS/wifi link: every metadata-only
/// call (<see cref="FileExists"/>, <see cref="DirectoryExists"/>, <see cref="GetFileStat"/>, the
/// enumerate-with-stat overloads) sleeps a fixed extra latency before delegating, and every byte read
/// through <see cref="OpenReadShared"/> is metered against a <see cref="SharedBandwidthLimiter"/> shared
/// by every caller that was handed the *same* limiter instance -- modelling one wifi link's bandwidth
/// being split between the foreground read and whatever preload workers are reading concurrently.
/// </summary>
public sealed class SlowLinkFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly TimeSpan _metadataLatency;
    private readonly SharedBandwidthLimiter? _bandwidth;

    public SlowLinkFileSystem(IFileSystem inner, TimeSpan metadataLatency, SharedBandwidthLimiter? bandwidth)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfLessThan(metadataLatency, TimeSpan.Zero);
        _metadataLatency = metadataLatency;
        _bandwidth = bandwidth;
    }

    private void DelayMetadata()
    {
        if (_metadataLatency > TimeSpan.Zero) Thread.Sleep(_metadataLatency);
    }

    public bool FileExists(string path) { DelayMetadata(); return _inner.FileExists(path); }
    public bool DirectoryExists(string path) { DelayMetadata(); return _inner.DirectoryExists(path); }
    public FileStat? GetFileStat(string path) { DelayMetadata(); return _inner.GetFileStat(path); }

    // File mutations never happen on a slow-link read-review path (no scenario in this harness moves,
    // copies or deletes across the simulated link) -- forwarded untouched, same as CountingFileSystem.
    public void Move(string source, string destination) => _inner.Move(source, destination);
    public void Copy(string source, string destination) => _inner.Copy(source, destination);
    // Forwarded, not left to the interface default (exists-check + Copy, not atomic): the ownership proof of a create-new copy
    // must hold behind this decorator too.
    public bool TryCopyNew(string source, string destination) => _inner.TryCopyNew(source, destination);
    public bool TryCopyNew(string source, string destination, CopyCreationProof proof) => _inner.TryCopyNew(source, destination, proof);
    public void Delete(string path) => _inner.Delete(path);

    public Stream OpenReadShared(string path, int bufferSize = 65536)
    {
        var stream = _inner.OpenReadShared(path, bufferSize);
        return _bandwidth is null ? stream : new ThrottledReadStream(stream, _bandwidth);
    }

    public Stream OpenAppend(string path, bool durable) => _inner.OpenAppend(path, durable);
    public void WriteAllTextAtomic(string path, string text, bool durable = true) => _inner.WriteAllTextAtomic(path, text, durable);
    public string ReadAllText(string path) => _inner.ReadAllText(path);

    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => _inner.EnumerateFiles(directory, pattern);

    public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
    {
        foreach (var entry in _inner.EnumerateFilesWithStat(directory, include, onSkipped))
        {
            DelayMetadata();
            yield return entry;
        }
    }

    public bool TryProbeReadable(string path, out string? failure)
    {
        DelayMetadata();
        return _inner.TryProbeReadable(path, out failure);
    }

    public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(
        string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
    {
        foreach (var entry in _inner.EnumerateReadableFilesWithStat(directory, include, onSkipped))
        {
            DelayMetadata();
            yield return entry;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public string ResolveRealPath(string path) { DelayMetadata(); return _inner.ResolveRealPath(path); }
}

/// <summary>
/// Q-R29 harness: a simple token-bucket rate limiter shared by every reader that models one wifi
/// link's total bandwidth. Thread-safe; blocks the calling thread (via <see cref="Thread.Sleep(int)"/>)
/// until enough tokens have accumulated, exactly like real bandwidth contention would.
/// </summary>
public sealed class SharedBandwidthLimiter(long bytesPerSecond)
{
    private readonly long _bytesPerSecond = bytesPerSecond > 0
        ? bytesPerSecond
        : throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
    private readonly Lock _gate = new();
    private double _availableBytes;
    private long _lastTicks = DateTime.UtcNow.Ticks;

    /// <summary>Blocks the calling thread until <paramref name="bytes"/> worth of the shared budget is available.</summary>
    public void Consume(int bytes)
    {
        if (bytes <= 0) return;
        // A request larger than one second's budget can never be satisfied at once (the bucket is capped at
        // _bytesPerSecond), so meter it in chunks no larger than the bucket.
        long remaining = bytes;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, _bytesPerSecond);
            ConsumeChunk(chunk);
            remaining -= chunk;
        }
    }

    private void ConsumeChunk(int bytes)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                Refill();
                if (_availableBytes >= bytes)
                {
                    _availableBytes -= bytes;
                    return;
                }
                var missing = bytes - _availableBytes;
                wait = TimeSpan.FromSeconds(missing / _bytesPerSecond);
            }
            // Sleep outside the lock so other readers can refill/consume concurrently (that's the
            // "shared by every reader" part -- a single lock held across the sleep would serialize them).
            Thread.Sleep(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait);
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow.Ticks;
        var elapsedSeconds = (now - _lastTicks) / (double)TimeSpan.TicksPerSecond;
        _lastTicks = now;
        if (elapsedSeconds <= 0) return;
        _availableBytes = Math.Min(_bytesPerSecond, _availableBytes + elapsedSeconds * _bytesPerSecond);
    }
}

/// <summary>Wraps a stream so every byte read is metered against a <see cref="SharedBandwidthLimiter"/>.</summary>
internal sealed class ThrottledReadStream(Stream inner, SharedBandwidthLimiter limiter) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0) limiter.Consume(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        if (read > 0) limiter.Consume(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
