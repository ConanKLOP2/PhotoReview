using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// RV-I01 guard: an EXIF thumbnail (tiny APP1 payload) must never drive a header-sized buffer allocation. Measured on this
/// machine: WIC itself refuses embedded thumbnails above roughly 400-500 px (Thumbnail is null), and a thumbnail whose SOF
/// claims a huge size over truncated data is rejected too, so the unchecked buffer in the reader is unreachable in practice
/// (the planned MaxThumbnailSide cap was therefore not added). These tests pin that behaviour.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class EmbeddedThumbnailReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbReader-" + Guid.NewGuid().ToString("N"));

    public EmbeddedThumbnailReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Patches the first SOF (the one inside the APP1 thumbnail) to claim the given size.</summary>
    private static byte[] WithClaimedThumbnailSize(int width, int height)
    {
        var jpeg = EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 48, thumbnailSize: 16);
        for (var i = 2; i + 9 < jpeg.Length; i++)
        {
            if (jpeg[i] != 0xFF || (jpeg[i + 1] != 0xC0 && jpeg[i + 1] != 0xC2)) continue;
            jpeg[i + 5] = (byte)(height >> 8); jpeg[i + 6] = (byte)height;
            jpeg[i + 7] = (byte)(width >> 8); jpeg[i + 8] = (byte)width;
            return jpeg;
        }
        throw new InvalidOperationException("no SOF found");
    }

    [Theory(DisplayName = "TryRead rejects a thumbnail whose header claims a huge size without allocating for it")]
    [InlineData(20000, 20000)]
    [InlineData(65535, 65535)]
    public void HugeClaimedThumbnail_ReturnsNull_WithoutBigAllocation(int width, int height)
    {
        var path = Path.Combine(_dir, "bomb.jpg");
        File.WriteAllBytes(path, WithClaimedThumbnailSize(width, height));

        EmbeddedThumbnailReader.TryRead(path); // warm-up (WIC/WPF one-time init)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = EmbeddedThumbnailReader.TryRead(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(result);
        Assert.True(allocated < 64L * 1024 * 1024, $"allocated {allocated} bytes");
    }

    /// <summary>A small main JPEG whose APP1 carries a valid, highly compressible (solid black) thumbnail of the given size (must fit in 64 KB).</summary>
    private static byte[] WithRealThumbnail(int thumbSide)
    {
        var pixels = new byte[thumbSide * thumbSide * 3];
        var bmp = BitmapSource.Create(thumbSide, thumbSide, 96, 96, PixelFormats.Bgr24, null, pixels, thumbSide * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 1 };
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var thumbStream = new MemoryStream();
        encoder.Save(thumbStream);
        var thumb = thumbStream.ToArray();
        Assert.True(thumb.Length < 60000, "thumbnail must fit one APP1 segment");

        // Splice into a WIC-written fixture: its APP1 ends with the thumbnail, so only the thumbnail bytes,
        // the IFD1 JPEGInterchangeFormatLength (0x0202) value and the APP1 length need patching.
        var seed = EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 480, thumbnailSize: 16);
        var app1 = 2;
        while (seed[app1 + 1] != 0xE1) app1 += 2 + ((seed[app1 + 2] << 8) | seed[app1 + 3]);
        var app1Length = (seed[app1 + 2] << 8) | seed[app1 + 3];
        var app1End = app1 + 2 + app1Length;
        var lengthEntry = -1;
        for (var i = app1 + 10; i + 12 <= app1End; i++)
        {
            if (seed[i] == 0x02 && seed[i + 1] == 0x02 && seed[i + 2] == 0 && seed[i + 3] == 4 && seed[i + 7] == 1) { lengthEntry = i; break; }
        }
        Assert.True(lengthEntry > 0);
        var oldThumbLength = (seed[lengthEntry + 8] << 24) | (seed[lengthEntry + 9] << 16) | (seed[lengthEntry + 10] << 8) | seed[lengthEntry + 11];
        var oldThumbStart = app1End - oldThumbLength;
        Assert.True(seed[oldThumbStart] == 0xFF && seed[oldThumbStart + 1] == 0xD8);

        var output = new List<byte>(seed.AsSpan(0, oldThumbStart).ToArray());
        output.AddRange(thumb);
        output.AddRange(seed.AsSpan(app1End));
        var newApp1Length = app1Length - oldThumbLength + thumb.Length;
        output[app1 + 2] = (byte)(newApp1Length >> 8); output[app1 + 3] = (byte)newApp1Length;
        output[lengthEntry + 8] = (byte)(thumb.Length >> 24); output[lengthEntry + 9] = (byte)(thumb.Length >> 16);
        output[lengthEntry + 10] = (byte)(thumb.Length >> 8); output[lengthEntry + 11] = (byte)thumb.Length;
        return [.. output];
    }

    [Fact(DisplayName = "A valid but oversized (1100x1100) embedded thumbnail is rejected before its pixel buffer is allocated")]
    public void OversizedValidThumbnail_ReturnsNull_WithoutBigAllocation()
    {
        var path = Path.Combine(_dir, "big-thumb.jpg");
        File.WriteAllBytes(path, WithRealThumbnail(1100));

        EmbeddedThumbnailReader.TryRead(path); // warm-up
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = EmbeddedThumbnailReader.TryRead(path);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(result);
        Assert.True(allocated < 3L * 1024 * 1024, $"allocated {allocated} bytes (a 1100x1100 BGRA buffer alone is 4.8 MB)");
    }

    [Fact(DisplayName = "Hand-built APP1/IFD1 thumbnail fixture is read by WIC (guards the oversized-thumbnail test)")]
    public void HandBuiltThumbnailFixture_IsReadable()
    {
        var path = Path.Combine(_dir, "small-thumb.jpg");
        File.WriteAllBytes(path, WithRealThumbnail(160));

        var image = EmbeddedThumbnailReader.TryRead(path);

        Assert.NotNull(image);
        Assert.Equal(160, image.PixelWidth);
    }

    [Fact(DisplayName = "A normal 160x120-class embedded thumbnail still decodes")]
    public void NormalThumbnail_StillDecodes()
    {
        var path = Path.Combine(_dir, "ok.jpg");
        File.WriteAllBytes(path, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 480, thumbnailSize: 160));

        var image = EmbeddedThumbnailReader.TryRead(path);

        Assert.NotNull(image);
        Assert.Equal(160, image.PixelWidth);
    }
}
