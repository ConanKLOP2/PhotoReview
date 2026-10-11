using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.Pixels;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// Atomic-write and quota-prune primitives for on-disk PNG caches (thumbnails, previews).
/// Each instance encapsulates the directory, file pattern, quota and prune coalescing state.
/// </summary>
public sealed class DiskCacheStore
{
    private readonly string _directory;
    private readonly string _searchPattern;
    private readonly long _maxBytes;
    private readonly ILog _log;
    private readonly Func<DateTime> _utcNow;

    // F-IMG-3: prune orders by LastAccessTimeUtc, but NTFS does not update it on reads by default and nothing else did,
    // so the "LRU" was really FIFO. A cache hit reports NoteAccessed, which stamps the file at most once per
    // AccessTouchInterval per entry (a metadata write, no file read) so a hot entry is not touched on every navigation.
    public static readonly TimeSpan AccessTouchInterval = TimeSpan.FromMinutes(10);
    private const int MaxTouchEntries = 8192;
    private readonly ConcurrentDictionary<string, DateTime> _lastTouched = new(StringComparer.OrdinalIgnoreCase);

    // Prune coalescing state per store instance
    private int _pruneScheduled;
    private int _prunePending;

    // IMG-10: incremental size tracking so a prune pass does not re-enumerate (and stat) every file
    // of a large cache directory when it is clearly under quota. -1 = unknown (forces a full scan,
    // which is also what happens for a store nobody reports writes to). Guarded by _sizeGate.
    // Errors always err on the high side (an overwrite or an external delete is counted as growth),
    // which only costs one extra full scan; a full scan is also forced every FullScanEvery skipped
    // passes so drift from other processes/writers cannot accumulate unbounded.
    private const int FullScanEvery = 256;
    private readonly object _sizeGate = new();
    private long _trackedBytes = -1;
    private long _notedDuringScan;
    private int _skippedPasses;
    private int _fullScanCount;

    /// <summary>Number of full directory enumerations performed so far (test/diagnostic).</summary>
    internal int FullScanCount => Volatile.Read(ref _fullScanCount);

    public DiskCacheStore(string directory, string searchPattern = "*.png", long maxBytes = 0, ILog? log = null, Func<DateTime>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        _directory = directory;
        _searchPattern = searchPattern;
        _maxBytes = Math.Max(0, maxBytes);
        _log = log ?? NullLog.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Records that <paramref name="path"/> was just read (cache hit) by stamping its last-access time so the quota
    /// prune evicts truly least-recently-used entries. Throttled per entry; best effort, never throws.
    /// </summary>
    public void NoteAccessed(string path)
    {
        var now = _utcNow();
        if (_lastTouched.TryGetValue(path, out var last) && now - last < AccessTouchInterval) return;
        if (_lastTouched.Count >= MaxTouchEntries) _lastTouched.Clear();
        _lastTouched[path] = now;
        try { File.SetLastAccessTimeUtc(path, now); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Static atomic PNG write helper for an <see cref="IDecodedImage"/> whose platform image <paramref name="codec"/> understands
    /// (C-02; WP-06: the codec is a required argument, there is no default). A platform image the codec does not understand is an
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static async Task WriteAtomicallyAsync(IDecodedImage image, IPlatformImageCodec codec, string cachePath, ILog? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        using var lease = codec.ToPixels(image.PlatformImage);
        await WriteAtomicallyAsync(lease.Pixels, cachePath, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Internal static atomic PNG write helper for pixels (WIC PNG encoder, the one WPF's PngBitmapEncoder wrapped, same
    /// default options). <paramref name="pixels"/> is only read, never disposed.
    /// </summary>
    internal static Task WriteAtomicallyAsync(PixelBuffer pixels, string cachePath, ILog? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);

        return AtomicCacheFile.WriteAsync(cachePath, stream => WicImageEncoder.EncodePng(pixels, stream), log, cancellationToken);
    }

    /// <summary>
    /// Reports that a file was just persisted into this store's directory so the quota check can
    /// track size incrementally instead of re-scanning the directory on every pass.
    /// </summary>
    public void NoteWritten(string path)
    {
        long length;
        try { length = new FileInfo(path).Length; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        lock (_sizeGate)
        {
            _notedDuringScan += length;
            if (_trackedBytes >= 0) _trackedBytes += length;
        }
    }

    /// <summary>
    /// Schedules an asynchronous prune pass for this store.
    /// Coalesced: concurrent calls will not spawn multiple background passes; at most one additional pass will run if requested while in flight.
    /// </summary>
    public void SchedulePrune()
    {
        if (Interlocked.Exchange(ref _pruneScheduled, 1) == 1)
        {
            Volatile.Write(ref _prunePending, 1);
            return;
        }

        RunPruneLoop();
    }

    /// <summary>Test seam: runs on the prune loop's exit path after its last pending check, right before the scheduled flag is cleared (null in production).</summary>
    internal Action? BeforeClearPruneScheduledForTests { get; set; }

    private void RunPruneLoop()
    {
        _ = Task.Run(() =>
        {
            try
            {
                do
                {
                    Volatile.Write(ref _prunePending, 0);
                    try
                    {
                        RunPrunePass();
                    }
                    catch (Exception ex)
                    {
                        // Any failure (I/O, access, or an unexpected one) costs only this pass: an exception escaping this task would
                        // leave _pruneScheduled at 1 forever, so every later SchedulePrune would just set "pending" and the cache
                        // would never be pruned again.
                        _log.Error($"Prune failed: {_directory}", ex);
                    }
                } while (Volatile.Read(ref _prunePending) == 1);
            }
            finally
            {
                BeforeClearPruneScheduledForTests?.Invoke();
                Interlocked.Exchange(ref _pruneScheduled, 0);
            }

            // If a prune was requested right before or during clearing the flag, ensure it is serviced.
            if (Volatile.Read(ref _prunePending) == 1 && Interlocked.Exchange(ref _pruneScheduled, 1) == 0)
            {
                RunPruneLoop();
            }
        });
    }

    private void RunPrunePass()
    {
        lock (_sizeGate)
        {
            if (_trackedBytes >= 0 && _trackedBytes <= _maxBytes && ++_skippedPasses < FullScanEvery) return;
            _skippedPasses = 0;
            _notedDuringScan = 0;
        }

        Interlocked.Increment(ref _fullScanCount);
        var remaining = PruneDirectory(_directory, _searchPattern, _maxBytes, _log);
        lock (_sizeGate)
        {
            // Writes noted while the scan ran may or may not have been enumerated; counting them again
            // only over-estimates (safe), never under-estimates.
            _trackedBytes = remaining < 0 ? -1 : remaining + _notedDuringScan;
        }
    }

    /// <summary>
    /// Waits until no prune pass is scheduled or running for this store, or <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task<bool> WaitForPruneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((Volatile.Read(ref _pruneScheduled) == 1 || Volatile.Read(ref _prunePending) == 1) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }
        return Volatile.Read(ref _pruneScheduled) == 0;
    }

    /// <summary>
    /// Static helper: deletes least-recently-used files matching <paramref name="searchPattern"/> until directory size is at or under <paramref name="maxBytes"/>.
    /// </summary>
    /// <returns>The bytes remaining, or -1 when the directory does not exist.</returns>
    public static long PruneDirectory(string directory, string searchPattern, long maxBytes, ILog? log = null)
    {
        if (!System.IO.Directory.Exists(directory)) return -1;

        // DirectoryInfo.EnumerateFiles yields FileInfo objects already filled from the directory scan (length, times), so no
        // per-file stat is needed afterwards.
        var files = new DirectoryInfo(directory).EnumerateFiles(searchPattern)
            .OrderBy(info => info.LastAccessTimeUtc).ThenBy(info => info.CreationTimeUtc).ToList();

        var total = files.Sum(info => info.Length);
        foreach (var info in files)
        {
            if (total <= maxBytes) break;
            if (TryDelete(info.FullName, log))
            {
                total -= info.Length;
            }
        }

        return total;
    }

    /// <summary>
    /// Removes every file matching this store's pattern in its directory, plus leftover atomic-write temp files
    /// (<see cref="TempFilePattern"/>), which the store pattern never matches.
    /// </summary>
    public void ClearDirectory()
    {
        ClearDirectory(_directory, _searchPattern, _log);
        // No log: a temp file a writer still holds open fails to delete, and that writer removes it itself.
        ClearDirectory(_directory, TempFilePattern, log: null);
        lock (_sizeGate) { _trackedBytes = -1; _notedDuringScan = 0; }
    }

    /// <summary>
    /// Temp files of <see cref="WriteAtomicallyAsync(PixelBuffer, string, ILog?, CancellationToken)"/> and
    /// PreviewCacheFile's atomic write (<c>X.png.&lt;guid&gt;.tmp</c>, <c>X.pv4.&lt;guid&gt;.tmp</c>). A process killed
    /// between creating one and the rename leaves it behind; no prune or clear pattern of the store matches it.
    /// </summary>
    public const string TempFilePattern = "*.tmp";

    /// <summary>A live writer's temp file exists for milliseconds; older ones were left by a killed process.</summary>
    public static readonly TimeSpan StaleTempFileAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deletes up to <paramref name="maxFiles"/> <see cref="TempFilePattern"/> files in <paramref name="directory"/>
    /// last written more than <see cref="StaleTempFileAge"/> before <paramref name="utcNow"/>. Best effort, never throws.
    /// </summary>
    /// <returns>Number of files deleted.</returns>
    public static int DeleteStaleTempFiles(string directory, DateTime utcNow, int maxFiles, ILog? log = null)
    {
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(directory)) return 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, TempFilePattern).Take(maxFiles))
            {
                DateTime written;
                try { written = File.GetLastWriteTimeUtc(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (utcNow - written > StaleTempFileAge && TryDelete(path, log)) removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { log?.Error($"Stale temp file cleanup failed: {directory}", ex); }
        return removed;
    }

    /// <summary>
    /// Static helper to remove matching files in a directory.
    /// </summary>
    public static void ClearDirectory(string directory, string searchPattern, ILog? log = null)
    {
        if (!System.IO.Directory.Exists(directory)) return;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, searchPattern))
        {
            TryDelete(path, log);
        }
    }

    /// <summary>
    /// True for the failures that reading or decoding a disk-cache entry can raise for a damaged, unreadable or foreign
    /// file: I/O and access errors, our own header validation (<see cref="InvalidDataException"/>), and whatever WPF/WIC
    /// throws for a well-framed but broken image (<see cref="FormatException"/> -- the base of WPF's <c>FileFormatException</c> -- and <see cref="ArgumentException"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="OverflowException"/>, <see cref="InvalidCastException"/> or
    /// <see cref="COMException"/> for damaged metadata). Such an entry is a cache miss (delete it, decode from source);
    /// anything else (cancellation, out-of-memory, programming errors) must still propagate.
    /// </summary>
    internal static bool IsCacheEntryFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or InvalidDataException
            or ArgumentException or InvalidOperationException or OverflowException or InvalidCastException or COMException;

    /// <summary>
    /// Safely deletes a file, catching IOException and UnauthorizedAccessException.
    /// </summary>
    public static bool TryDelete(string path, ILog? log = null)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException ex)
        {
            log?.Error($"Delete failed: {path}", ex);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Error($"Delete failed: {path}", ex);
            return false;
        }
    }
}
