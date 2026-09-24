using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.TestSupport.Windows.Fixtures;

/// <summary>
/// Builds synthetic JPEGs for testing the embedded-EXIF-thumbnail fast path (ThumbnailCache
/// miss -> EmbeddedThumbnailReader, perf(open) task 2): one with a real embedded APP1
/// thumbnail, one without, each with distinguishable, deterministic pixel sizes so a test can
/// tell which image (main vs. embedded thumbnail) was actually read back.
/// </summary>
public static class EmbeddedThumbnailJpegFixture
{
    /// <summary>
    /// Encodes a JPEG whose main frame is <paramref name="mainSize"/> px square and whose
    /// embedded EXIF thumbnail is <paramref name="thumbnailSize"/> px square. The two sizes
    /// differ deliberately: if a caller ever fell back to decoding the main frame instead of
    /// the embedded thumbnail, a size-based assertion would catch it immediately.
    /// </summary>
    public static byte[] CreateWithThumbnail(int mainSize = 64, int thumbnailSize = 16)
    {
        var main = CreateSolidBitmap(mainSize, mainSize, Colors.Red);
        var thumbnail = CreateSolidBitmap(thumbnailSize, thumbnailSize, Colors.Blue);

        var frame = BitmapFrame.Create(main, thumbnail, metadata: null, colorContexts: null);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(frame);

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Encodes a plain JPEG with no embedded EXIF thumbnail at all.</summary>
    public static byte[] CreateWithoutThumbnail(int size = 64)
    {
        var main = CreateSolidBitmap(size, size, Colors.Green);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(main));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static WriteableBitmap CreateSolidBitmap(int width, int height, Color color)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
