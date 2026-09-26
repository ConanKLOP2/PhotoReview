using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Imaging.Caching;

/// <summary>
/// Shared "temp file -&gt; write -&gt; atomic rename" primitive behind both
/// <see cref="DiskCacheStore.WriteAtomicallyAsync(System.Windows.Media.Imaging.BitmapSource, string, ILog?, CancellationToken)"/>
/// and <see cref="PreviewCacheFile"/>'s atomic write.
/// </summary>
internal static class AtomicCacheFile
{
    /// <summary>
    /// Writes <paramref name="writePayload"/>'s output to a temp file next to <paramref name="cachePath"/>
    /// (name matching <see cref="DiskCacheStore.TempFilePattern"/>) and atomically renames it into place.
    /// perf(cache): this is a disposable cache, not a durability-critical journal -- the
    /// temp-file-then-atomic-rename here is the only guarantee that matters (a crash never
    /// leaves a half-written file at <paramref name="cachePath"/>). WriteThrough/Flush(true) would
    /// force every write through to physical disk before the rename, which only slows down cache
    /// writes for a durability guarantee this data doesn't need (a lost write is just a future cache miss).
    /// </summary>
    internal static Task WriteAsync(string cachePath, Action<Stream> writePayload, ILog? log, CancellationToken cancellationToken)
        => WriteAsync(cachePath, writePayload, log, temporaryPathOverride: null, cancellationToken);

    /// <summary>
    /// Test seam: <paramref name="temporaryPathOverride"/> lets a test pin the temp file name (instead of
    /// the random GUID every real caller gets) to deterministically exercise the create/cleanup guarantees
    /// (e.g. a pre-existing file at that path must make the write fail, not silently overwrite it).
    /// </summary>
    internal static async Task WriteAsync(string cachePath, Action<Stream> writePayload, ILog? log, string? temporaryPathOverride, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = temporaryPathOverride ?? cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp"; // matches DiskCacheStore.TempFilePattern
        try
        {
            var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                writePayload(stream);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            DiskCacheStore.TryDelete(temporaryPath, log);
        }
    }
}
