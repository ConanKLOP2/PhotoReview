using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

/// <summary>
/// Shared atomic-write/quota-prune primitives for on-disk PNG caches (thumbnails, previews).
/// Callers own their directory, quota and logging context; the only shared instance state
/// is <see cref="_pruneScheduled"/>, which coalesces concurrent prune requests per directory.
/// </summary>
public static class DiskCacheStore
{
    // Keyed by directory only: today every caller (ThumbnailCache, PreviewImageService)
    // uses a distinct directory with its own fixed quota/pattern, so coalescing on the
    // directory alone is safe. If a future caller ever shares a directory with a
    // different maxBytes/searchPattern, this key must widen to include them, or one
    // caller's quota would silently win the race for both.
    private static readonly ConcurrentDictionary<string, byte> _pruneScheduled = new(StringComparer.OrdinalIgnoreCase);
    // Set when a SchedulePrune call is coalesced away while a pass for that directory
    // is already in flight, so the running worker knows to loop again instead of the
    // request being silently dropped until some unrelated future write happens to land.
    private static readonly ConcurrentDictionary<string, byte> _prunePending = new(StringComparer.OrdinalIgnoreCase);


    public static async Task WriteAtomicallyAsync(BitmapSource image, string cachePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
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
        finally { TryDelete(temporaryPath, logContext: null); }
    }

    /// <summary>Deletes least-recently-used files matching <paramref name="searchPattern"/> until the directory is at or under <paramref name="maxBytes"/>.</summary>
    public static void PruneDirectory(string directory, string searchPattern, long maxBytes, string logContext)
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory, searchPattern)
            .Select(path => new FileInfo(path)).Where(info => info.Exists)
            .OrderBy(info => info.LastAccessTimeUtc).ThenBy(info => info.CreationTimeUtc).ToList();
        var total = files.Sum(info => info.Length);
        foreach (var info in files)
        {
            if (total <= maxBytes) break;
            if (TryDelete(info.FullName, logContext)) total -= info.Length;
        }
    }

    /// <summary>
    /// Fire-and-forget prune, coalesced per directory: many near-simultaneous writes
    /// (preload decoding several images at once, say) would otherwise each spawn their
    /// own full enumerate/sort pass over the same directory. If a write lands while a
    /// pass for this directory is already running, it doesn't spawn a second pass, but
    /// it does mark that one more pass is needed once the running one finishes — so a
    /// burst of writes that all land mid-pass can't leave the directory over quota
    /// indefinitely; at worst it lags by one extra pass.
    /// </summary>
    public static void SchedulePrune(string directory, string searchPattern, long maxBytes, string logContext)
    {
        if (!_pruneScheduled.TryAdd(directory, 0))
        {
            _prunePending[directory] = 0;
            return;
        }
        RunPruneLoop(directory, searchPattern, maxBytes, logContext);
    }

    private static void RunPruneLoop(string directory, string searchPattern, long maxBytes, string logContext)
    {
        _ = Task.Run(() =>
        {
            do
            {
                _prunePending.TryRemove(directory, out _);
                try { PruneDirectory(directory, searchPattern, maxBytes, logContext); }
                catch (IOException ex) { AppLog.Error($"{logContext} (prune): {directory}", ex); }
                catch (UnauthorizedAccessException ex) { AppLog.Error($"{logContext} (prune): {directory}", ex); }
            } while (_prunePending.ContainsKey(directory));
            _pruneScheduled.TryRemove(directory, out _);
            // A request can race exactly between the loop's exit check above and the
            // marker removal on the line before this comment: it would see the
            // (about to be removed) scheduled-marker as present, defer to
            // _prunePending, and return without anyone left to service it. Re-check
            // after removing the marker and restart the loop ourselves if that
            // happened, so that request isn't silently dropped.
            if (_prunePending.ContainsKey(directory) && _pruneScheduled.TryAdd(directory, 0))
                RunPruneLoop(directory, searchPattern, maxBytes, logContext);
        });
    }

    /// <summary>
    /// Waits until no prune pass is scheduled or running for <paramref name="directory"/>, or
    /// <paramref name="timeout"/> elapses. SchedulePrune is fire-and-forget by design for the
    /// production singletons (ThumbnailCache/PreviewImageService), which never delete their own
    /// cache directory out from under a pending prune -- but a short-lived caller that does
    /// (e.g. a benchmark run cleaning up its scratch disk cache) must wait for every prune it
    /// scheduled to actually finish first, or the delete can race a worker still enumerating or
    /// deleting files in that directory.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if no prune remained scheduled or running for the directory when
    /// this returned; <see langword="false"/> if <paramref name="timeout"/> elapsed first, which
    /// callers must treat as "still unsafe to delete", not as success.
    /// </returns>
    public static async Task<bool> WaitForPruneAsync(string directory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_pruneScheduled.ContainsKey(directory) && DateTime.UtcNow < deadline)
            await Task.Delay(20).ConfigureAwait(false);
        return !_pruneScheduled.ContainsKey(directory);
    }

    public static void ClearDirectory(string directory, string searchPattern, string logContext)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern)) TryDelete(path, logContext);
    }

    public static bool TryDelete(string path, string? logContext)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException ex) { if (logContext is not null) AppLog.Error($"{logContext}: {path}", ex); return false; }
        catch (UnauthorizedAccessException ex) { if (logContext is not null) AppLog.Error($"{logContext}: {path}", ex); return false; }
    }
}
