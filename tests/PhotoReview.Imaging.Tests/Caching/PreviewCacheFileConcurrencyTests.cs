using System.Collections.Concurrent;
using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Caching;

[Trait("Category", "HotPath")]
public sealed class PreviewCacheFileConcurrencyTests : IDisposable
{
    private readonly TempRoot _root = new("pv4-concurrency");

    public void Dispose() => _root.Dispose();

    private static WpfDecodedImage Image(int width, int height, int orientation, DecoderBackend backend)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(FixtureGenerator.CreateGradientCheckerboard(width, height), System.Windows.Media.PixelFormats.Bgr32, null, 0);
        converted.Freeze();
        return new WpfDecodedImage(converted, downscaled: true, orientation: orientation, actualBackend: backend);
    }

    [Fact(DisplayName = "Writers replacing one entry while readers read it: every read is a complete, self-consistent entry (or a clean I/O refusal), and no temp file is left")]
    public async Task ConcurrentReplaceAndRead_NeverYieldsATornEntry()
    {
        var path = _root.Combine("entry.pv4");
        // Two shapes whose header fields, dimensions and backend all differ: a torn read would mix them.
        var shapes = new[] { Image(8, 6, 1, DecoderBackend.Wpf), Image(16, 12, 6, DecoderBackend.TurboJpeg) };
        await PreviewCacheFile.WriteAtomicallyAsync(shapes[0], path);
        using var barrier = new Barrier(7);
        var stop = 0;
        var failures = new ConcurrentBag<string>();
        var reads = 0;

        var writers = Enumerable.Range(0, 3).Select(w => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 40; i++)
            {
                try { await PreviewCacheFile.WriteAtomicallyAsync(shapes[(w + i) % 2], path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* destination held open by a reader: the write is skipped */ }
            }
        })).ToArray();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    var read = PreviewCacheFile.Read(path);
                    Interlocked.Increment(ref reads);
                    var consistent = (read.Bitmap.PixelWidth, read.Bitmap.PixelHeight, read.Orientation, read.ActualBackend) is
                        (8, 6, 1, DecoderBackend.Wpf) or (16, 12, 6, DecoderBackend.TurboJpeg);
                    if (!consistent) failures.Add($"torn entry: {read.Bitmap.PixelWidth}x{read.Bitmap.PixelHeight} o{read.Orientation} {read.ActualBackend}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* replaced under the reader: a miss */ }
                catch (Exception ex) { failures.Add($"{ex.GetType().Name}: {ex.Message}"); }
            }
        })).ToArray();

        await Task.WhenAll(writers);
        Volatile.Write(ref stop, 1);
        await Task.WhenAll(readers);

        Assert.Empty(failures);
        Assert.True(reads > 0);
        Assert.Empty(Directory.GetFiles(_root.Path, "*.tmp"));
        Assert.True(PreviewCacheFile.Read(path).Bitmap.PixelWidth is 8 or 16);
    }
}
