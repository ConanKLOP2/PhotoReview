using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-05: TurboJpegDecoder(codec) (decode straight into a PixelBuffer, PixelOps orientation) against the legacy
/// parameterless decoder (BitmapSource + WPF TransformedBitmap) on the same files. Byte for byte whenever no fine scale is
/// involved (full size, exact DCT factor). With fine scale WIC's Fant and the integer area filter differ by a few levels
/// (worst measured on this edge-heavy checkerboard fixture: MAE 3.6, PSNR 30.6 dB; sizes and orientation always identical).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TurboJpegPixelBufferParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-TjParity-" + Guid.NewGuid().ToString("N"));

    public TurboJpegPixelBufferParityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly TurboJpeg.TurboJpegDecoder Legacy = new();
    private static readonly TurboJpeg.TurboJpegDecoder Pixels = new(PixelBufferImageCodec.Instance);

    /// <summary>Ten source sizes: tiny, single row/column, odd, MCU-unaligned and a few "real" ones.</summary>
    public static TheoryData<int, int> Sizes() => new()
    {
        { 1, 1 }, { 1, 17 }, { 17, 1 }, { 8, 8 }, { 33, 31 }, { 64, 48 }, { 100, 75 }, { 257, 129 }, { 400, 300 }, { 640, 480 },
    };

    private string Jpeg(int width, int height, ushort orientation)
    {
        var path = Path.Combine(_dir, $"p_{width}x{height}_o{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, width, height, orientation);
        return path;
    }

    [Theory(DisplayName = "Full-size decode: PixelBuffer path equals the WPF path byte for byte, all 8 orientations")]
    [MemberData(nameof(Sizes))]
    public void FullSize_AllOrientations_ByteExact(int width, int height)
    {
        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            var path = Jpeg(width, height, orientation);
            var request = new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true);

            var expected = Assert.IsType<WpfDecodedImage>(Legacy.Decode(request));
            var actual = Assert.IsType<DecodedImage>(Pixels.Decode(request));
            var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);

            Assert.Equal((expected.PixelWidth, expected.PixelHeight), (buffer.Width, buffer.Height));
            Assert.Equal((expected.PixelWidth, expected.PixelHeight), (actual.PixelWidth, actual.PixelHeight));
            Assert.Equal((expected.OriginalWidth, expected.OriginalHeight), (actual.OriginalWidth, actual.OriginalHeight));
            Assert.Equal(expected.Orientation, actual.Orientation);
            Assert.Equal(expected.Downscaled, actual.Downscaled);
            Assert.Equal(expected.EstimatedBytes, actual.EstimatedBytes);
            Assert.Equal(DecoderBackend.TurboJpeg, actual.ActualBackend);
            Assert.Equal(PixelLayout.Bgr32, buffer.Layout);
            PixelAssert.Equal(expected.Source, buffer);
            buffer.Dispose();
        }
    }

    [Theory(DisplayName = "Exact DCT factor (no fine scale): byte exact with orientation")]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void ExactDctFactor_ByteExact(ushort orientation)
    {
        var path = Jpeg(400, 300, orientation);
        // 400x300 -> 200x150 is the 1/2 factor and 100x75 the 1/4 factor: the target is hit exactly, the second pass is skipped.
        // The box is in displayed pixels, so a transposing orientation asks for the same stored size with half the width.
        var targets = orientation >= 5 ? new[] { 150, 75 } : new[] { 200, 100 };
        foreach (var targetWidth in targets)
        {
            var request = new DecodeRequest(path, TargetWidth: targetWidth, ApplyOrientation: true);
            var expected = Assert.IsType<WpfDecodedImage>(Legacy.Decode(request));
            var actual = Assert.IsType<DecodedImage>(Pixels.Decode(request));
            var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);

            Assert.Equal((expected.PixelWidth, expected.PixelHeight), (buffer.Width, buffer.Height));
            Assert.Equal(expected.Downscaled, actual.Downscaled);
            PixelAssert.Equal(expected.Source, buffer);
            buffer.Dispose();
        }
    }

    [Theory(DisplayName = "Fine scale: same size and orientation as WPF, pixels within area-filter rounding")]
    [InlineData(1, 130)]
    [InlineData(1, 40)]
    [InlineData(6, 100)]
    [InlineData(6, 130)]
    [InlineData(5, 77)]
    [InlineData(3, 399)]
    [InlineData(8, 11)]
    public void FineScale_CloseToWpf(ushort orientation, int targetWidth)
    {
        var path = Jpeg(400, 300, orientation);
        var request = new DecodeRequest(path, TargetWidth: targetWidth, ApplyOrientation: true);

        var expected = Assert.IsType<WpfDecodedImage>(Legacy.Decode(request));
        var actual = Assert.IsType<DecodedImage>(Pixels.Decode(request));
        var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);
        using var reference = PixelAssert.FromBitmapSource(expected.Source, PixelLayout.Bgr32);

        Assert.Equal((expected.PixelWidth, expected.PixelHeight), (buffer.Width, buffer.Height));
        Assert.Equal((expected.OriginalWidth, expected.OriginalHeight), (actual.OriginalWidth, actual.OriginalHeight));
        Assert.Equal(expected.Downscaled, actual.Downscaled);
        var mae = PixelAssert.MeanAbsoluteError(reference, buffer);
        var psnr = PixelAssert.Psnr(reference, buffer);
        Assert.True(mae <= 4.0, $"mean absolute error {mae:F3} (PSNR {psnr:F1} dB)");
        Assert.True(psnr >= 29.0, $"PSNR {psnr:F1} dB (MAE {mae:F3})");
        buffer.Dispose();
    }

    [Fact(DisplayName = "In-memory bytes and the SourceOrientation override take the same pixel path")]
    public void MemoryBufferWithOverride_MatchesLegacy()
    {
        var bytes = Metadata.ExifTestData.EncodeJpegWithExif(64, 48, withExif: true, orientation: 6);
        var request = new DecodeRequest("memory.jpg", TargetWidth: 0, ApplyOrientation: true, Bytes: bytes, SourceOrientation: 3);

        var expected = Assert.IsType<WpfDecodedImage>(Legacy.Decode(request));
        var actual = Assert.IsType<DecodedImage>(Pixels.Decode(request));
        var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);

        Assert.Equal(3, actual.Orientation);
        Assert.Equal(expected.Exif, actual.Exif);
        PixelAssert.Equal(expected.Source, buffer);
        buffer.Dispose();
    }

    [Fact(DisplayName = "Unoriented decode keeps stored orientation and size")]
    public void ApplyOrientationFalse_NoRotation()
    {
        var path = Jpeg(64, 48, 6);
        var request = new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: false);

        var expected = Assert.IsType<WpfDecodedImage>(Legacy.Decode(request));
        var actual = Assert.IsType<DecodedImage>(Pixels.Decode(request));
        var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);

        Assert.Equal((64, 48), (buffer.Width, buffer.Height));
        PixelAssert.Equal(expected.Source, buffer);
        buffer.Dispose();
    }

    [Fact(DisplayName = "The codec constructor refuses null")]
    public void NullCodec_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new TurboJpeg.TurboJpegDecoder(null!));

    [Fact(DisplayName = "The codec decides the platform image: FromPixels receives the decoded buffer")]
    public void CustomCodec_ReceivesBuffer()
    {
        var codec = new RecordingCodec();
        var decoder = new TurboJpeg.TurboJpegDecoder(codec);
        var path = Jpeg(64, 48, 1);

        var image = decoder.Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));

        Assert.Same(codec.Received, image.PlatformImage);
        Assert.Equal((64, 48), (codec.Received!.Width, codec.Received.Height));
        Assert.Equal(1, codec.Calls);
        codec.Received.Dispose();
    }

    private sealed class RecordingCodec : IPlatformImageCodec
    {
        public PixelBuffer? Received { get; private set; }
        public int Calls { get; private set; }
        public string Name => "recording";

        public object FromPixels(PixelBuffer pixels)
        {
            Calls++;
            Received = pixels;
            return pixels;
        }

        public PixelLease ToPixels(object platformImage) => new((PixelBuffer)platformImage, owned: false);
    }
}

/// <summary>The live-buffer counter is process-wide, so the accounting tests run alone.</summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class TurboJpegPixelBufferLifetimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-TjLife-" + Guid.NewGuid().ToString("N"));

    public TurboJpegPixelBufferLifetimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact(DisplayName = "A decode failure after the output buffer exists leaves no PixelBuffer alive")]
    public void CorruptJpeg_DoesNotLeakPixelBuffer()
    {
        var path = Path.Combine(_dir, "ok.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 256, 256);
        var bytes = File.ReadAllBytes(path);
        // Keep the header (so the output buffer is allocated) and destroy the entropy-coded data.
        for (var i = bytes.Length / 2; i < bytes.Length - 2; i++) bytes[i] = 0x55;
        var decoder = new TurboJpeg.TurboJpegDecoder(PixelBufferImageCodec.Instance);

        var before = NativePixelMemory.LiveCount;
        var threw = false;
        try
        {
            _ = decoder.Decode(new DecodeRequest("corrupt.jpg", TargetWidth: 0, ApplyOrientation: true, Bytes: bytes));
        }
        catch (Exception)
        {
            threw = true;
        }

        Assert.True(threw, "the damaged entropy data must be reported (StopOnWarning)");
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact(DisplayName = "A successful decode holds exactly one buffer, the returned one")]
    public void Decode_HoldsExactlyOneBuffer_AllOrientationsAndFineScale()
    {
        var decoder = new TurboJpeg.TurboJpegDecoder(PixelBufferImageCodec.Instance);
        foreach (ushort orientation in new ushort[] { 1, 6 })
        {
            var path = Path.Combine(_dir, $"o{orientation}.jpg");
            FixtureGenerator.GenerateJpegWithOrientation(path, 400, 300, orientation);
            foreach (var targetWidth in new[] { 0, 130 })
            {
                var before = NativePixelMemory.LiveCount;
                var image = decoder.Decode(new DecodeRequest(path, TargetWidth: targetWidth, ApplyOrientation: true));

                Assert.Equal(before + 1, NativePixelMemory.LiveCount);
                ((PixelBuffer)image.PlatformImage).Dispose();
                Assert.Equal(before, NativePixelMemory.LiveCount);
            }
        }
    }
}