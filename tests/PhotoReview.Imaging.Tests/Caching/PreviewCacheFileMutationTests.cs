using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// Mutation-testing follow-ups for <see cref="PreviewCacheFile"/> (docs/MUTATION-TESTING.md): header validation edges
/// (orientation, dimensions, EXIF block bounds, payload size, EOI marker), the share mode of the read, the alpha
/// detection and the opaque-pixel scan. The original tests of these paths are Slow and so absent from mutation runs.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewCacheFileMutationTests : IDisposable
{
    private const int HeaderSize = 24;
    private readonly TempRoot _root = new("pv4-mut");

    public void Dispose() => _root.Dispose();

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static BitmapSource Gradient(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                pixels[o] = (byte)(x * 13);
                pixels[o + 1] = (byte)(y * 17);
                pixels[o + 2] = (byte)(x + y);
                pixels[o + 3] = 255;
            }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private string NewPath(string name) => _root.Combine(name + ".pv4");

    /// <summary>A valid v7 entry's JPEG payload (16x8), taken from the real writer.</summary>
    private byte[] SamplePayload()
    {
        var path = NewPath("sample-" + Guid.NewGuid().ToString("N"));
        PreviewCacheFileWpf.WriteAtomicallyAsync(Gradient(16, 8), DecoderBackend.Wpf, 1, 16, 8, path).GetAwaiter().GetResult();
        return File.ReadAllBytes(path)[(HeaderSize + 2)..];
    }

    private static byte[] Header(int version = PreviewCacheFile.CurrentVersion, byte orientation = 1, byte alpha = 0,
        int width = 16, int height = 8, int originalWidth = 16, int originalHeight = 8)
    {
        var h = new byte[HeaderSize];
        "PRVC"u8.CopyTo(h);
        h[4] = (byte)version;
        h[5] = (byte)DecoderBackend.Wpf;
        h[6] = orientation;
        h[7] = alpha;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), originalWidth);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(20), originalHeight);
        return h;
    }

    private static byte[] ExifBlock(int declaredLength, int actualBytes)
    {
        var block = new byte[2 + actualBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)declaredLength);
        return block;
    }

    private string WriteEntry(string name, byte[] header, byte[]? exifBlock, byte[]? payload)
    {
        var path = NewPath(name);
        File.WriteAllBytes(path, [.. header, .. exifBlock ?? [], .. payload ?? []]);
        return path;
    }

    private static InvalidDataException Rejected(string path) =>
        Assert.Throws<InvalidDataException>(() => PreviewCacheFileWpf.Read(path));

    // ---- write: orientation range ---------------------------------------------------------------------------------

    [Fact(DisplayName = "Writing accepts orientation 1 through 8 inclusive and rejects 0 and 9")]
    public async Task Write_OrientationRange()
    {
        var bmp = Gradient(16, 8);
        await PreviewCacheFileWpf.WriteAtomicallyAsync(bmp, DecoderBackend.Wpf, 8, 16, 8, NewPath("o8"));
        await PreviewCacheFileWpf.WriteAtomicallyAsync(bmp, DecoderBackend.Wpf, 1, 16, 8, NewPath("o1"));
        Assert.Equal(8, PreviewCacheFileWpf.Read(NewPath("o8")).Orientation);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PreviewCacheFileWpf.WriteAtomicallyAsync(bmp, DecoderBackend.Wpf, 0, 16, 8, NewPath("o0")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PreviewCacheFileWpf.WriteAtomicallyAsync(bmp, DecoderBackend.Wpf, 9, 16, 8, NewPath("o9")));
        Assert.False(File.Exists(NewPath("o0")));
        Assert.False(File.Exists(NewPath("o9")));
    }

    // ---- read: header validation ----------------------------------------------------------------------------------

    [Fact(DisplayName = "Reading accepts orientation 8 and rejects 0 and 9 as an invalid orientation byte")]
    public void Read_OrientationByteRange()
    {
        var payload = SamplePayload();
        var exif = ExifBlock(0, 0);

        Assert.Equal(8, PreviewCacheFileWpf.Read(WriteEntry("o8", Header(orientation: 8), exif, payload)).Orientation);
        Assert.Equal(1, PreviewCacheFileWpf.Read(WriteEntry("o1", Header(orientation: 1), exif, payload)).Orientation);
        foreach (var bad in new byte[] { 0, 9, 255 })
            Assert.Contains("orientation", Rejected(WriteEntry("bad" + bad, Header(orientation: bad), exif, payload)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The header's alpha flag selects the render format: Pbgra32 when set, Bgr32 when clear")]
    public void Read_AlphaFlag_SelectsOutputFormat()
    {
        var payload = SamplePayload();
        var exif = ExifBlock(0, 0);

        Assert.Equal(PixelFormats.Pbgra32, PreviewCacheFileWpf.Read(WriteEntry("alpha", Header(alpha: 1), exif, payload)).Bitmap.Format);
        Assert.Equal(PixelFormats.Pbgra32, PreviewCacheFileWpf.Read(WriteEntry("alpha255", Header(alpha: 255), exif, payload)).Bitmap.Format);
        Assert.Equal(PixelFormats.Bgr32, PreviewCacheFileWpf.Read(WriteEntry("opaque", Header(alpha: 0), exif, payload)).Bitmap.Format);
    }

    [Theory(DisplayName = "A zero or negative pixel width or height in the header is rejected as invalid pixel dimensions")]
    [InlineData(0, 8)]
    [InlineData(16, 0)]
    [InlineData(-16, 8)]
    [InlineData(16, -8)]
    [InlineData(0, 0)]
    public void Read_NonPositivePixelDimensions_AreRejectedByTheHeaderCheck(int width, int height)
    {
        var path = WriteEntry("dims", Header(width: width, height: height), ExifBlock(0, 0), SamplePayload());

        Assert.Contains("pixel dimensions", Rejected(path).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A header with matching dimensions but a payload of different size is rejected as a mismatch")]
    public void Read_PayloadDimensionMismatch_IsRejected()
    {
        var path = WriteEntry("mismatch", Header(width: 17, height: 8), ExifBlock(0, 0), SamplePayload());

        Assert.Contains("do not match", Rejected(path).Message, StringComparison.Ordinal);
    }

    // ---- read: EXIF block and payload bounds ----------------------------------------------------------------------

    [Fact(DisplayName = "A v7 file that ends one byte after the header is a clean 'no payload' rejection, not an end-of-stream failure")]
    public void Read_HeaderPlusOneByte_IsNoPayload()
    {
        var path = WriteEntry("h1", Header(), [0], null);

        Assert.Contains("no payload", Rejected(path).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A v7 file with an empty EXIF block and no payload, or a v6 file with only a header, is rejected as having no payload")]
    public void Read_NoPayload_IsRejected()
    {
        Assert.Contains("no payload", Rejected(WriteEntry("v7-empty", Header(), ExifBlock(0, 0), null)).Message, StringComparison.Ordinal);
        Assert.Contains("no payload", Rejected(WriteEntry("v6-empty", Header(PreviewCacheFile.NoExifVersion), null, null)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An EXIF block that fills the rest of the file exactly leaves no payload; one claiming even one byte more is oversized")]
    public void Read_ExifBlockBounds()
    {
        // 5 EXIF bytes declared and present, nothing after: the block fits, the payload is missing.
        Assert.Contains("no payload", Rejected(WriteEntry("exact", Header(), ExifBlock(5, 5), null)).Message, StringComparison.Ordinal);
        // 5 declared, only 4 present: oversized (must not surface as an end-of-stream failure).
        Assert.Contains("oversized", Rejected(WriteEntry("over1", Header(), ExifBlock(5, 4), null)).Message, StringComparison.Ordinal);
        // 5 declared, nothing present.
        Assert.Contains("oversized", Rejected(WriteEntry("over5", Header(), ExifBlock(5, 0), null)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An EXIF block of exactly the codec's maximum size is accepted (unreadable summary = no EXIF); one byte more is rejected")]
    public void Read_ExifBlockAtMaximum()
    {
        var max = PhotoReview.Imaging.Metadata.ExifSummaryCodec.MaxEncodedLength;
        var payload = SamplePayload();

        var ok = PreviewCacheFileWpf.Read(WriteEntry("max", Header(), ExifBlock(max, max), payload));
        Assert.Null(ok.Exif);
        Assert.Equal(16, ok.Bitmap.PixelWidth);

        Assert.Contains("oversized", Rejected(WriteEntry("max1", Header(), ExifBlock(max + 1, max + 1), payload)).Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A payload of exactly four bytes is not judged truncated by the end-marker check (it fails later, in the decoder)")]
    public void Read_FourBytePayloadEndingInEoi_IsNotReportedAsTruncated()
    {
        var path = WriteEntry("four", Header(), ExifBlock(0, 0), [0x00, 0x00, 0xFF, 0xD9]);

        var ex = Record.Exception(() => PreviewCacheFileWpf.Read(path));

        Assert.NotNull(ex);
        Assert.False(ex is InvalidDataException ide && ide.Message.Contains("truncated", StringComparison.Ordinal),
            "a 4-byte payload that ends in the EOI marker must reach the decoder");
    }

    [Fact(DisplayName = "A payload cut short (no end-of-image marker) or shorter than four bytes is rejected as truncated")]
    public void Read_TruncatedPayload_IsRejected()
    {
        var payload = SamplePayload();

        Assert.Contains("truncated", Rejected(WriteEntry("cut", Header(), ExifBlock(0, 0), payload[..^3])).Message, StringComparison.Ordinal);
        Assert.Contains("truncated", Rejected(WriteEntry("tiny", Header(), ExifBlock(0, 0), [0xFF, 0xD9, 0xFF])).Message, StringComparison.Ordinal);
    }

    // ---- read: share mode -----------------------------------------------------------------------------------------

    [Fact(DisplayName = "Read opens the entry with Read|Delete sharing: it works while another handle holds it open for reading with delete access")]
    public void Read_WhileAnotherHandleHoldsTheFileOpen_Succeeds()
    {
        var path = WriteEntry("shared", Header(), ExifBlock(0, 0), SamplePayload());

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
        {
            var read = PreviewCacheFileWpf.Read(path);

            Assert.Equal(16, read.Bitmap.PixelWidth);
        }
        Assert.False(File.Exists(path));
    }

    // ---- HasAlpha -------------------------------------------------------------------------------------------------

    private static BitmapSource Create(PixelFormat format, BitmapPalette? palette = null, int width = 8, int height = 2)
    {
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var bmp = BitmapSource.Create(width, height, 96, 96, format, palette, new byte[stride * height], stride);
        bmp.Freeze();
        return bmp;
    }

    public static TheoryData<string> AlphaFormats() => new() { "Bgra32", "Pbgra32", "Rgba64", "Prgba64", "Rgba128Float", "Prgba128Float" };

    private static PixelFormat FormatByName(string name) => name switch
    {
        "Bgra32" => PixelFormats.Bgra32,
        "Pbgra32" => PixelFormats.Pbgra32,
        "Rgba64" => PixelFormats.Rgba64,
        "Prgba64" => PixelFormats.Prgba64,
        "Rgba128Float" => PixelFormats.Rgba128Float,
        "Prgba128Float" => PixelFormats.Prgba128Float,
        "Indexed1" => PixelFormats.Indexed1,
        "Indexed2" => PixelFormats.Indexed2,
        "Indexed4" => PixelFormats.Indexed4,
        "Indexed8" => PixelFormats.Indexed8,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory(DisplayName = "HasAlpha is true for each alpha-carrying pixel format, and such a format that is not Bgra32/Pbgra32 is never reported fully opaque")]
    [MemberData(nameof(AlphaFormats))]
    public void HasAlpha_AlphaFormats_AreTrue(string name)
    {
        var bmp = Create(FormatByName(name));

        Assert.True(WpfBitmapSourceCodec.HasAlpha(bmp));
        Assert.True(WpfBitmapSourceCodec.HasAlpha(new WpfDecodedImage(bmp)));
        if (name is not ("Bgra32" or "Pbgra32"))
            Assert.False(WpfBitmapSourceCodec.IsFullyOpaque(bmp));
    }

    [Theory(DisplayName = "HasAlpha is false for opaque pixel formats")]
    [InlineData("Bgr32")]
    [InlineData("Bgr24")]
    [InlineData("Gray8")]
    [InlineData("Rgb48")]
    public void HasAlpha_OpaqueFormats_AreFalse(string name)
    {
        var format = name switch
        {
            "Bgr32" => PixelFormats.Bgr32,
            "Bgr24" => PixelFormats.Bgr24,
            "Gray8" => PixelFormats.Gray8,
            _ => PixelFormats.Rgb48,
        };
        var bmp = Create(format);

        Assert.False(WpfBitmapSourceCodec.HasAlpha(bmp));
        Assert.True(WpfBitmapSourceCodec.IsFullyOpaque(bmp));
    }

    [Theory(DisplayName = "HasAlpha for an indexed format depends on whether its palette has a non-opaque colour (254 counts, 255 does not)")]
    [InlineData("Indexed1")]
    [InlineData("Indexed2")]
    [InlineData("Indexed4")]
    [InlineData("Indexed8")]
    public void HasAlpha_IndexedFormats_FollowThePalette(string name)
    {
        var format = FormatByName(name);
        var opaque = new BitmapPalette([Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 255, 255, 255)]);
        var nearlyOpaque = new BitmapPalette([Color.FromArgb(255, 0, 0, 0), Color.FromArgb(254, 255, 255, 255)]);
        var translucent = new BitmapPalette([Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 255, 255, 255)]);

        Assert.False(WpfBitmapSourceCodec.HasAlpha(Create(format, opaque)));
        Assert.True(WpfBitmapSourceCodec.HasAlpha(Create(format, nearlyOpaque)));
        Assert.True(WpfBitmapSourceCodec.HasAlpha(Create(format, translucent)));
        Assert.False(WpfBitmapSourceCodec.IsFullyOpaque(Create(format, translucent)));
        Assert.True(WpfBitmapSourceCodec.IsFullyOpaque(Create(format, opaque)));
    }

    [Fact(DisplayName = "HasAlpha(IDecodedImage) rejects null and is false for a platform image that is not a BitmapSource")]
    public void HasAlpha_DecodedImageOverload()
    {
        Assert.Throws<ArgumentNullException>(() => WpfBitmapSourceCodec.HasAlpha((IDecodedImage)null!));
        Assert.False(WpfBitmapSourceCodec.HasAlpha(new OpaqueObjectImage()));
    }

    private sealed class OpaqueObjectImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage => new object();
    }

    // ---- IsFullyOpaque / AllAlphaOpaque ---------------------------------------------------------------------------

    private static BitmapSource Bgra(PixelFormat format, int width, int height, int? translucentPixel)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = 10;
            pixels[i * 4 + 1] = 20;
            pixels[i * 4 + 2] = 5;
            pixels[i * 4 + 3] = 255;
        }
        if (translucentPixel is { } p) pixels[p * 4 + 3] = 254;
        var bmp = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    public static TheoryData<int, int> OpaqueScanSizes() => new()
    {
        { 1, 1 }, { 3, 2 }, { 7, 5 }, { 8, 8 }, { 9, 3 }, { 33, 3 }, { 64, 256 }, { 4096, 10 },
    };

    [Theory(DisplayName = "IsFullyOpaque of Bgra32/Pbgra32: all-opaque is true; one alpha-254 pixel at the first, middle, last or last-row position is false (any width/band layout)")]
    [MemberData(nameof(OpaqueScanSizes))]
    public void IsFullyOpaque_ScansEveryPixelOfEveryBand(int width, int height)
    {
        foreach (var format in new[] { PixelFormats.Bgra32, PixelFormats.Pbgra32 })
        {
            Assert.True(WpfBitmapSourceCodec.IsFullyOpaque(Bgra(format, width, height, null)), $"{format} {width}x{height} opaque");
            var total = width * height;
            foreach (var position in new[] { 0, total / 2, total - 1, (height - 1) * width })
                Assert.False(WpfBitmapSourceCodec.IsFullyOpaque(Bgra(format, width, height, position)), $"{format} {width}x{height} pixel {position}");
        }
    }

    [Fact(DisplayName = "AllAlphaOpaque finds a single non-opaque pixel at every index of every length around the vector width, and accepts all-opaque input")]
    public void AllAlphaOpaque_EveryIndexAndLength()
    {
        var max = System.Numerics.Vector<uint>.Count * 3 + 3;
        for (var length = 0; length <= max; length++)
        {
            var pixels = new uint[length];
            Array.Fill(pixels, 0xFF123456u); // colour bits set: the check must look at alpha only
            Assert.True(WpfBitmapSourceCodec.AllAlphaOpaque(MemoryMarshal.AsBytes(pixels.AsSpan())), $"all opaque, length {length}");
            for (var index = 0; index < length; index++)
            {
                var copy = (uint[])pixels.Clone();
                copy[index] = 0xFE123456u;
                Assert.False(WpfBitmapSourceCodec.AllAlphaOpaque(MemoryMarshal.AsBytes(copy.AsSpan())), $"length {length}, index {index}");
            }
        }
    }
}
