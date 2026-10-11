using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Mutation-testing gap closers for the small decoding helpers: <see cref="ExifOrientation"/> range checks and freeze guards,
/// <see cref="WpfDecodedImage"/> size estimate, <see cref="WpfImageAdapter.Materialize"/> on non byte-aligned rows,
/// <see cref="DecodedImageSize"/>, <see cref="EmbeddedThumbnailReader"/> pixel conversion and failure handling,
/// <see cref="ManagedIStream"/> write side, <see cref="FallbackImageDecoder"/> identity and logging, the factory's provider list.
/// </summary>
public sealed class DecodingHelpersMutationTests : IDisposable
{
    private readonly TempRoot _root = new("decoding-helpers");

    public void Dispose() => _root.Dispose();

    // ---- ExifOrientation.Read ----

    private static BitmapMetadata? RoundTripMetadata(string container, ushort orientation)
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(16, 8);
        var metadata = new BitmapMetadata(container);
        BitmapEncoder encoder;
        if (container == "jpg")
        {
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
            encoder = new JpegBitmapEncoder();
        }
        else
        {
            metadata.SetQuery("/ifd/{ushort=274}", orientation);
            encoder = new TiffBitmapEncoder();
        }

        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0].Metadata as BitmapMetadata;
    }

    [Theory]
    [InlineData("jpg", 0, 1)]
    [InlineData("jpg", 1, 1)]
    [InlineData("jpg", 2, 2)]
    [InlineData("jpg", 6, 6)]
    [InlineData("jpg", 8, 8)]
    [InlineData("jpg", 9, 1)]
    [InlineData("tiff", 0, 1)]
    [InlineData("tiff", 1, 1)]
    [InlineData("tiff", 3, 3)]
    [InlineData("tiff", 8, 8)]
    [InlineData("tiff", 9, 1)]
    public void Read_StoredOrientationTag_IsAcceptedOnlyInOneToEight(string container, int tag, int expected)
    {
        var metadata = RoundTripMetadata(container, (ushort)tag);

        Assert.Equal(expected, WpfExifOrientation.Read(metadata));
    }

    [Fact]
    public void Read_WithoutMetadata_IsOne()
    {
        Assert.Equal(1, WpfExifOrientation.Read(null));
    }

    // ---- freeze guards (a bitmap that cannot be frozen must be left alone, not throw) ----

    private static WriteableBitmap LockedBitmap()
    {
        var bitmap = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Lock();
        Assert.False(bitmap.CanFreeze);
        return bitmap;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void Apply_UnfreezableSourceWithNoRotation_IsReturnedAsIs(int orientation)
    {
        var bitmap = LockedBitmap();

        var result = WpfExifOrientation.Apply(bitmap, orientation);

        Assert.Same(bitmap, result);
        Assert.False(result.IsFrozen);
    }

    [Fact]
    public void Apply_FreezableSourceWithNoRotation_ComesBackFrozen()
    {
        var bitmap = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Bgra32, null);

        var result = WpfExifOrientation.Apply(bitmap, 1);

        Assert.Same(bitmap, result);
        Assert.True(result.IsFrozen);
    }

    [Fact]
    public void WpfDecodedImage_UnfreezableSource_IsWrappedWithoutFreezing()
    {
        var bitmap = LockedBitmap();

        var image = new WpfDecodedImage(bitmap);

        Assert.Same(bitmap, image.Source);
        Assert.False(bitmap.IsFrozen);
    }

    [Fact]
    public void WpfDecodedImage_FreezableSource_IsFrozen()
    {
        var bitmap = new WriteableBitmap(4, 3, 96, 96, PixelFormats.Bgra32, null);

        _ = new WpfDecodedImage(bitmap);

        Assert.True(bitmap.IsFrozen);
    }

    // ---- WpfDecodedImage.EstimatedBytes ----

    private static BitmapSource Blank(PixelFormat format, int width, int height)
    {
        var stride = (width * format.BitsPerPixel + 7) / 8;
        BitmapPalette? palette = format == PixelFormats.Indexed8 || format == PixelFormats.BlackWhite
            ? (format == PixelFormats.BlackWhite ? BitmapPalettes.BlackAndWhite : BitmapPalettes.Gray256)
            : null;
        var bitmap = BitmapSource.Create(width, height, 96, 96, format, palette, new byte[stride * height], stride);
        bitmap.Freeze();
        return bitmap;
    }

    [Theory]
    [InlineData(32, 4)]   // Bgra32
    [InlineData(24, 4)]   // Bgr24: rounded up to the 4 bytes of the display format
    [InlineData(8, 4)]    // Gray8
    [InlineData(1, 4)]    // BlackWhite
    [InlineData(48, 6)]   // Rgb48
    [InlineData(64, 8)]   // Rgba64
    [InlineData(128, 16)] // Prgba128Float
    public void EstimatedBytes_UsesTheBytesPerPixelOfTheFormatWithAFloorOfFour(int bitsPerPixel, int bytesPerPixel)
    {
        var format = bitsPerPixel switch
        {
            32 => PixelFormats.Bgra32,
            24 => PixelFormats.Bgr24,
            8 => PixelFormats.Gray8,
            1 => PixelFormats.BlackWhite,
            48 => PixelFormats.Rgb48,
            64 => PixelFormats.Rgba64,
            _ => PixelFormats.Prgba128Float,
        };
        var image = new WpfDecodedImage(Blank(format, 5, 3));

        Assert.Equal(5L * 3 * bytesPerPixel, image.EstimatedBytes);
    }

    // ---- WpfImageAdapter.Materialize on rows that are not byte aligned ----

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(48)]
    public void Materialize_NonByteAlignedRows_KeepsEveryPixel(int bitsPerPixel)
    {
        var format = bitsPerPixel switch
        {
            1 => PixelFormats.BlackWhite,
            8 => PixelFormats.Gray8,
            24 => PixelFormats.Bgr24,
            _ => PixelFormats.Rgb48,
        };
        const int width = 13, height = 5;
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var data = new byte[stride * height];
        new Random(bitsPerPixel).NextBytes(data);
        BitmapPalette? palette = format == PixelFormats.BlackWhite ? BitmapPalettes.BlackAndWhite : null;
        var source = BitmapSource.Create(width, height, 96, 96, format, palette, data, stride);

        var result = WpfImageAdapter.Materialize(source);

        Assert.True(result.IsFrozen);
        Assert.Equal((width, height, format), (result.PixelWidth, result.PixelHeight, result.Format));
        var copied = new byte[stride * height];
        result.CopyPixels(copied, stride, 0);
        if (format == PixelFormats.BlackWhite)
        {
            // Padding bits at the end of each row are not part of the image.
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    Assert.Equal((data[(y * stride) + (x / 8)] >> (7 - (x % 8))) & 1, (copied[(y * stride) + (x / 8)] >> (7 - (x % 8))) & 1);
        }
        else
        {
            Assert.Equal(data, copied);
        }
    }

    // ---- DecodedImageSize.HasKnownOriginal ----

    private sealed class FakeImage(bool downscaled, int pixelW, int pixelH, int origW, int origH) : IDecodedImage
    {
        public int PixelWidth => pixelW;
        public int PixelHeight => pixelH;
        public bool Downscaled => downscaled;
        public int Orientation => 1;
        public long EstimatedBytes => 0;
        public object PlatformImage => new object();
        public int OriginalWidth => origW;
        public int OriginalHeight => origH;
    }

    [Theory]
    [InlineData(false, 10, 10, 10, 10, true)]   // a full decode: the pixels are the original
    [InlineData(false, 10, 10, 5, 5, true)]     // not downscaled: whatever the original says
    [InlineData(true, 10, 10, 20, 20, true)]    // genuine downscale
    [InlineData(true, 10, 10, 20, 10, true)]    // shrunk on the width only
    [InlineData(true, 10, 10, 10, 20, true)]    // shrunk on the height only
    [InlineData(true, 10, 10, 10, 10, false)]   // the "original" is just the small size: unknown
    [InlineData(true, 10, 10, 5, 5, false)]     // smaller than the pixels: not an original
    [InlineData(true, 10, 10, 5, 10, false)]
    [InlineData(true, 10, 10, 10, 5, false)]
    [InlineData(true, 10, 10, 5, 20, true)]     // one axis larger is enough
    [InlineData(true, 10, 10, 20, 5, true)]
    public void HasKnownOriginal_FullDecodeOrOriginalLargerThanThePixels_IsTrue(
        bool downscaled, int pixelW, int pixelH, int origW, int origH, bool expected)
    {
        Assert.Equal(expected, DecodedImageSize.HasKnownOriginal(new FakeImage(downscaled, pixelW, pixelH, origW, origH)));
    }

    // ---- EmbeddedThumbnailReader ----

    private string JpegWithThumbnail(ushort? orientation, int mainW = 48, int mainH = 32, int thumbW = 16, int thumbH = 8, Color? thumbColor = null)
    {
        var path = _root.Combine("thumb-" + Guid.NewGuid().ToString("N") + ".jpg");
        var main = FixtureGenerator.CreateGradientCheckerboard(mainW, mainH);
        var color = thumbColor ?? Colors.Blue;
        var pixels = new byte[thumbW * thumbH * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        var thumb = BitmapSource.Create(thumbW, thumbH, 96, 96, PixelFormats.Bgra32, null, pixels, thumbW * 4);
        thumb.Freeze();
        BitmapMetadata? metadata = null;
        if (orientation.HasValue)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", orientation.Value);
        }

        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(main, thumb, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    [Fact]
    public void TryRead_Thumbnail_IsConvertedToCorrectBgraPixels()
    {
        var path = JpegWithThumbnail(orientation: null, thumbColor: Colors.Blue);

        var image = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);

        Assert.NotNull(image);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
        // WP-03: premultiplied BGRA from the WIC reader (was straight Bgra32 from WPF); A = 255 so the bytes are the same.
        Assert.Equal(PixelFormats.Pbgra32, bitmap.Format);
        var px = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(8, 4, 1, 1), px, 4, 0);
        Assert.InRange(px[0], 240, 255); // B
        Assert.InRange(px[1], 0, 16);    // G
        Assert.InRange(px[2], 0, 16);    // R
        Assert.Equal(255, px[3]);        // A
    }

    [Fact]
    public void TryRead_RotatedSource_AppliesTheOrientationAndSwapsTheOriginalSize()
    {
        var path = JpegWithThumbnail(orientation: 6);

        var image = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);

        Assert.NotNull(image);
        Assert.Equal((8, 16), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(6, image.Orientation);
        Assert.Equal((32, 48), (image.OriginalWidth, image.OriginalHeight));
        Assert.True(image.Downscaled);
    }

    [Fact]
    public void TryRead_NoThumbnailInTheFile_IsNull()
    {
        var path = _root.Combine("plain.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 48, 32);

        Assert.Null(EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance));
    }

    [Fact]
    public void TryRead_MissingFile_IsNull()
    {
        Assert.Null(EmbeddedThumbnailReader.TryRead(_root.Combine("missing.jpg"), WpfBitmapSourceCodec.Instance));
    }

    [Fact]
    public void TryRead_PathOfADirectory_IsNull()
    {
        Assert.Null(EmbeddedThumbnailReader.TryRead(_root.Dir("a-folder"), WpfBitmapSourceCodec.Instance));
    }

    [Fact]
    public void TryRead_FileThatIsNotAnImage_IsNull()
    {
        var garbage = _root.File("garbage.jpg", [.. Enumerable.Range(0, 400).Select(i => (byte)((i * 31) + 7))]);
        var text = FixtureGenerator.GenerateTextFile(_root.Combine("text.jpg"));
        var empty = FixtureGenerator.GenerateZeroByteFile(_root.Combine("empty.jpg"));

        Assert.Null(EmbeddedThumbnailReader.TryRead(garbage, WpfBitmapSourceCodec.Instance));
        Assert.Null(EmbeddedThumbnailReader.TryRead(text, WpfBitmapSourceCodec.Instance));
        Assert.Null(EmbeddedThumbnailReader.TryRead(empty, WpfBitmapSourceCodec.Instance));
    }

    [Fact]
    public void TryRead_EveryTruncationOfAThumbnailJpeg_NeverThrows()
    {
        var bytes = File.ReadAllBytes(JpegWithThumbnail(orientation: 6));
        var path = _root.Combine("cut.jpg");

        for (var length = 0; length < bytes.Length; length += length < 700 ? 1 : 9)
        {
            File.WriteAllBytes(path, bytes.AsSpan(0, length).ToArray());
            var result = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance); // null or an image, never an exception
            if (length == 0) Assert.Null(result);
        }
    }

    // ---- ManagedIStream write side ----

    private static unsafe void Write(ManagedIStream com, byte[] data, int cb, nint pcbWritten)
    {
        fixed (byte* p = data) com.Write(p, cb, pcbWritten);
    }

    [Fact]
    public void ManagedIStream_Write_WritesAndReportsTheCountThroughThePointer()
    {
        using var stream = new MemoryStream();
        using var com = new ManagedIStream(stream);
        var pcb = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pcb, -1);
            Write(com, [1, 2, 3, 4, 5], 3, pcb);

            Assert.Equal(3, Marshal.ReadInt32(pcb));
            Assert.Equal(new byte[] { 1, 2, 3 }, stream.ToArray());
            Write(com, [9], 1, IntPtr.Zero);
            Assert.Equal(new byte[] { 1, 2, 3, 9 }, stream.ToArray());
        }
        finally { Marshal.FreeHGlobal(pcb); }
    }

    [Fact]
    public void ManagedIStream_WriteAndSetSizeAfterDispose_ThrowObjectDisposed()
    {
        using var stream = new MemoryStream();
        var com = new ManagedIStream(stream);
        com.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Write(com, [1], 1, IntPtr.Zero));
        Assert.Throws<ObjectDisposedException>(() => com.SetSize(4));
    }

    [Fact]
    public void ManagedIStream_SetSizeCommitAndNoOps_BehaveLikeAStream()
    {
        using var stream = new MemoryStream();
        using var com = new ManagedIStream(stream);

        com.SetSize(7);
        com.Commit(0);
        com.Revert();
        com.LockRegion(0, 1, 0);
        com.UnlockRegion(0, 1, 0);

        Assert.Equal(7, stream.Length);
    }

    // ---- FallbackImageDecoder identity and logging ----

    private sealed class CannedDecoder(Func<DecodeRequest, IDecodedImage> decode) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => decode(request);
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
    }

    private sealed class BackendImage(DecoderBackend backend) : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage => new object();
        public DecoderBackend ActualBackend => backend;
    }

    [Fact]
    public void FallbackDecoder_FallbackImageAlreadyOnTheFallbackBackend_IsReturnedAsIs()
    {
        var wpfImage = new BackendImage(DecoderBackend.Wpf);
        var decoder = new FallbackImageDecoder(
            new CannedDecoder(_ => throw new NotSupportedException("primary cannot")), DecoderBackend.TurboJpeg,
            new CannedDecoder(_ => wpfImage));

        var result = decoder.Decode(new DecodeRequest("x.jpg", 0));

        Assert.Same(wpfImage, result);
    }

    [Fact]
    public void FallbackDecoder_FallbackImageReportingAnotherBackend_IsRelabelledAsTheFallbackBackend()
    {
        var decoder = new FallbackImageDecoder(
            new CannedDecoder(_ => throw new NotSupportedException("primary cannot")), DecoderBackend.TurboJpeg,
            new CannedDecoder(_ => new BackendImage(DecoderBackend.WicDirect)));

        var result = decoder.Decode(new DecodeRequest("x.jpg", 0));

        Assert.Equal(DecoderBackend.Wpf, result.ActualBackend);
    }

    [Fact]
    public void FallbackDecoder_PrimaryImageOnAnotherBackend_IsRelabelledAsThePrimaryBackend()
    {
        var decoder = new FallbackImageDecoder(
            new CannedDecoder(_ => new BackendImage(DecoderBackend.Wpf)), DecoderBackend.TurboJpeg,
            new CannedDecoder(_ => throw new InvalidOperationException("must not be used")));

        var result = decoder.Decode(new DecodeRequest("x.jpg", 0));

        Assert.Equal(DecoderBackend.TurboJpeg, result.ActualBackend);
    }

    [Fact]
    public void FallbackDecoder_WarnsThroughTheInjectedLogWhenItFallsBack()
    {
        var log = new RecordingLog();
        var decoder = new FallbackImageDecoder(
            new CannedDecoder(_ => throw new NotSupportedException("primary cannot")), DecoderBackend.TurboJpeg,
            new CannedDecoder(_ => new BackendImage(DecoderBackend.Wpf)), log);

        _ = decoder.Decode(new DecodeRequest("x.jpg", 0));

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("x.jpg", warning, StringComparison.Ordinal);
    }

    // ---- ImageDecoderFactory provider list and log ----

    [Fact]
    public void Factory_CustomProvider_IsTheBackendThatGetsBuilt()
    {
        var calls = 0;
        var factory = new ImageDecoderFactory(
            [(DecoderBackend.WicDirect, () => new CannedDecoder(_ => { calls++; return new BackendImage(DecoderBackend.WicDirect); }))]);

        var decoded = factory.Create(DecoderBackend.WicDirect).Decode(new DecodeRequest("x.jpg", 0));

        Assert.True(factory.IsRegistered(DecoderBackend.WicDirect));
        Assert.Equal(1, calls);
        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
    }

    [Fact]
    public void Factory_UnregisteredBackend_WarnsThroughTheInjectedLogAndGivesWpf()
    {
        var log = new RecordingLog();
        var factory = new ImageDecoderFactory([], log, wpfDecoderFactory: () => new WpfBitmapImageDecoder());

        var decoder = factory.Create(DecoderBackend.TurboJpeg);

        Assert.IsType<WpfBitmapImageDecoder>(decoder);
        Assert.Contains(log.Warnings, w => w.Contains("not registered", StringComparison.Ordinal));
    }

    // ---- MemoryHeadroom production reader ----

    [Fact]
    public void ReadGcMemoryInfo_ReportsThePhysicalBudgetAndANonNegativeLoad()
    {
        var (total, load) = MemoryHeadroom.ReadGcMemoryInfo();

        Assert.True(total > 0);
        Assert.True(load >= 0);
    }
}