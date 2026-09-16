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
    private static readonly ConcurrentDictionary<string, byte> _pruneScheduled = new(StringComparer.OrdinalIgnoreCase);


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
    /// own full enumerate/sort pass over the same directory. If one is already running
    /// or queued for this directory, this call is a no-op; quota enforcement can lag by
    /// the size of a few files until the next write triggers another pass, which is an
    /// acceptable trade against redundant concurrent directory scans.
    /// </summary>
    public static void SchedulePrune(string directory, string searchPattern, long maxBytes, string logContext)
    {
        if (!_pruneScheduled.TryAdd(directory, 0)) return;
        _ = Task.Run(() =>
        {
            try { PruneDirectory(directory, searchPattern, maxBytes, logContext); }
            catch (IOException ex) { AppLog.Error($"{logContext} (prune): {directory}", ex); }
            catch (UnauthorizedAccessException ex) { AppLog.Error($"{logContext} (prune): {directory}", ex); }
            finally { _pruneScheduled.TryRemove(directory, out _); }
        });
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
