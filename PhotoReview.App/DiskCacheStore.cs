using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoReview.App;

/// <summary>
/// Shared atomic-write/quota-prune primitives for on-disk PNG caches (thumbnails, previews).
/// Callers own their directory, quota and logging context; this type has no instance state.
/// </summary>
public static class DiskCacheStore
{
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
