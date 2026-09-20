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
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath, log);
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
                    PruneDirectory(_directory, _searchPattern, _maxBytes, _log, _companionSuffix);
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
    /// </summary>
    public void Prune() => PruneDirectory(_directory, _searchPattern, _maxBytes, _log, _companionSuffix);

    /// <summary>
    /// Static helper: deletes least-recently-used files matching <paramref name="searchPattern"/> until directory size is at or under <paramref name="maxBytes"/>.
    /// </summary>
    public static void PruneDirectory(string directory, string searchPattern, long maxBytes, ILog? log = null, string? companionSuffix = null)
    {
        if (!System.IO.Directory.Exists(directory)) return;

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
    }

    public static void PruneDirectory(string directory, string searchPattern, long maxBytes, string? logContext, string? companionSuffix = null)
        => PruneDirectory(directory, searchPattern, maxBytes, log: null, companionSuffix);

    /// <summary>
    /// Removes every file matching this store's pattern in its directory.
    /// </summary>
    public void ClearDirectory() => ClearDirectory(_directory, _searchPattern, _log);

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
