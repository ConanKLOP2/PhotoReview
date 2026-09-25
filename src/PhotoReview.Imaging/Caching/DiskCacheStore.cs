using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;

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
    private readonly string? _companionSuffix;

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

    public string Directory => _directory;
    public string SearchPattern => _searchPattern;
    public long MaxBytes => _maxBytes;
    public ILog Log => _log;

    public DiskCacheStore(string directory, string searchPattern = "*.png", long maxBytes = 0, ILog? log = null, string? companionSuffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        _directory = directory;
        _searchPattern = searchPattern;
        _maxBytes = Math.Max(0, maxBytes);
        _log = log ?? NullLog.Instance;
        _companionSuffix = companionSuffix;
    }

    /// <summary>
    /// Atomically writes a decoded image as a PNG to <paramref name="cachePath"/> using a temporary file and atomic replace.
    /// </summary>
    public Task WriteAtomicallyAsync(IDecodedImage image, string cachePath, CancellationToken cancellationToken = default)
        => WriteAtomicallyAsync(image, cachePath, _log, cancellationToken);

    /// <summary>
    /// Static atomic write helper for IDecodedImage.
    /// </summary>
    public static Task WriteAtomicallyAsync(IDecodedImage image, string cachePath, ILog? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.PlatformImage is not BitmapSource bitmap)
            throw new ArgumentException("PlatformImage must be a BitmapSource for PNG encoding.", nameof(image));
        return WriteAtomicallyAsync(bitmap, cachePath, log, cancellationToken);
    }

    /// <summary>
    /// Internal atomic write helper for BitmapSource.
    /// </summary>
    internal Task WriteAtomicallyAsync(BitmapSource image, string cachePath, CancellationToken cancellationToken = default)
        => WriteAtomicallyAsync(image, cachePath, _log, cancellationToken);

    /// <summary>
    /// Internal static atomic write helper for BitmapSource.
    /// </summary>
    internal static async Task WriteAtomicallyAsync(BitmapSource image, string cachePath, ILog? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            // perf(cache): this is a disposable cache, not a durability-critical journal -- the
            // temp-file-then-atomic-rename below is the only guarantee that matters (a crash never
            // leaves a half-written file at cachePath). WriteThrough/Flush(true) forced every write
            // through to physical disk before the rename, which only slows down cache writes for a
            // durability guarantee this data doesn't need (a lost write is just a future cache miss).
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath, log);
        }
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

    private void RunPruneLoop()
    {
        _ = Task.Run(() =>
        {
            do
            {
                Volatile.Write(ref _prunePending, 0);
                try
                {
                    RunPrunePass();
                }
                catch (IOException ex)
                {
                    _log.Error($"Prune failed: {_directory}", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _log.Error($"Prune failed: {_directory}", ex);
                }
            } while (Volatile.Read(ref _prunePending) == 1);

            Interlocked.Exchange(ref _pruneScheduled, 0);

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
        var remaining = PruneDirectoryCore(_directory, _searchPattern, _maxBytes, _log, _companionSuffix);
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
    /// Synchronously prunes least-recently-used files in this store's directory down to <see cref="MaxBytes"/>.
    /// LRU order is <c>LastAccessTimeUtc</c> then <c>CreationTimeUtc</c>: NTFS last-access updates are
    /// disabled by default on many installs, in which case this degrades to creation-order eviction
    /// (cache hits do not touch files; a metadata write per hit would cost the hot path).
    /// </summary>
    public void Prune() => PruneDirectory(_directory, _searchPattern, _maxBytes, _log, _companionSuffix);

    /// <summary>
    /// Static helper: deletes least-recently-used files matching <paramref name="searchPattern"/> until directory size is at or under <paramref name="maxBytes"/>.
    /// </summary>
    public static void PruneDirectory(string directory, string searchPattern, long maxBytes, ILog? log = null, string? companionSuffix = null)
        => PruneDirectoryCore(directory, searchPattern, maxBytes, log, companionSuffix);

    /// <summary>Prunes and returns the bytes remaining, or -1 when the directory does not exist.</summary>
    private static long PruneDirectoryCore(string directory, string searchPattern, long maxBytes, ILog? log, string? companionSuffix)
    {
        if (!System.IO.Directory.Exists(directory)) return -1;

        var files = System.IO.Directory.EnumerateFiles(directory, searchPattern)
            .Select(path => new FileInfo(path)).Where(info => info.Exists)
            .OrderBy(info => info.LastAccessTimeUtc).ThenBy(info => info.CreationTimeUtc).ToList();

        var total = files.Sum(info => info.Length);
        foreach (var info in files)
        {
            if (total <= maxBytes) break;
            if (TryDelete(info.FullName, log))
            {
                total -= info.Length;
                if (companionSuffix is not null) TryDelete(info.FullName + companionSuffix, log);
            }
        }

        // A crash between the two atomic writes can leave metadata without its PNG.
        // Remove those orphans during the same maintenance pass.
        if (companionSuffix is not null)
        {
            foreach (var companion in System.IO.Directory.EnumerateFiles(directory, searchPattern + companionSuffix))
            {
                var imagePath = companion[..^companionSuffix.Length];
                if (!File.Exists(imagePath)) TryDelete(companion, log);
            }
        }

        return total;
    }

    public static void PruneDirectory(string directory, string searchPattern, long maxBytes, string? logContext, string? companionSuffix = null)
        => PruneDirectory(directory, searchPattern, maxBytes, log: null, companionSuffix);

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
    /// Temp files of <see cref="WriteAtomicallyAsync(BitmapSource, string, ILog?, CancellationToken)"/> and
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
        catch (IOException ex) { log?.Error($"Stale temp file cleanup failed: {directory}", ex); }
        catch (UnauthorizedAccessException ex) { log?.Error($"Stale temp file cleanup failed: {directory}", ex); }
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

    public static void ClearDirectory(string directory, string searchPattern, string? logContext)
        => ClearDirectory(directory, searchPattern, log: null);

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

    public static bool TryDelete(string path, string? logContext)
        => TryDelete(path, log: null);
}
