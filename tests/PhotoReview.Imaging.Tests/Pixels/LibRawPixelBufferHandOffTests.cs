using PhotoReview.Core.Model;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Raw;
using PhotoReview.TestSupport.Windows;

namespace PhotoReview.Imaging.Tests.Pixels;

/// <summary>
/// WP-05: the last step of LibRawDecoder (HandOff) and the PixelBuffer-backed BgraBuffer, without native LibRaw.
/// The native decode body is covered by the Native corpus test at the end of this file.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "HotPath")]
public sealed class LibRawPixelBufferHandOffTests
{
    private static RgbBgraResampler.BgraBuffer Resampled(int sw, int sh, int tw, int th, int channels)
    {
        var rgb = new byte[sw * sh * channels];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 31 % 251);
        return RgbBgraResampler.ResizeToBuffer(rgb, sw, sh, tw, th, channels, CancellationToken.None);
    }

    [Theory]
    [InlineData(40, 30, 40, 30, 3)]  // 1:1 convert
    [InlineData(40, 30, 25, 20, 3)]  // bilinear
    [InlineData(90, 60, 30, 20, 3)]  // box average
    [InlineData(64, 48, 16, 12, 1)]  // monochrome sensor
    public void HandOff_WithCodec_GivesTheSamePixelsAsTheWpfPath(int sw, int sh, int tw, int th, int channels)
    {
        using var forWpf = Resampled(sw, sh, tw, th, channels);
        using var forCodec = Resampled(sw, sh, tw, th, channels);
        var legacy = Assert.IsType<WpfDecodedImage>(new LibRawDecoder().HandOff(forWpf, downscaled: true, sw, sh, () => true));
        var stages = new List<string>();

        var image = Assert.IsType<DecodedImage>(new LibRawDecoder(PixelBufferImageCodec.Instance, stages.Add).HandOff(forCodec, downscaled: true, sw, sh, () => true));

        var buffer = Assert.IsType<PixelBuffer>(image.PlatformImage);
        PixelAssert.Equal(legacy.Source, buffer);
        Assert.Equal((tw, th), (image.PixelWidth, image.PixelHeight));
        Assert.Equal((sw, sh), (image.OriginalWidth, image.OriginalHeight));
        Assert.Equal(DecoderBackend.LibRaw, image.ActualBackend);
        Assert.True(image.Downscaled);
        Assert.Equal(1, image.Orientation);
        Assert.Equal(legacy.EstimatedBytes, image.EstimatedBytes);
        Assert.Equal(["source-released", "bitmap-created"], stages);
        buffer.Dispose();
    }

    [Fact]
    public void HandOff_WithCodec_MovesOwnershipWithoutCopy_AndTheEmptiedBufferFreesNothing()
    {
        var before = NativePixelMemory.LiveCount;
        var staging = Resampled(20, 10, 20, 10, 3);
        var address = staging.Pointer;
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);

        var image = new LibRawDecoder(PixelBufferImageCodec.Instance).HandOff(staging, false, 20, 10, () => false);
        staging.Dispose();

        var buffer = Assert.IsType<PixelBuffer>(image.PlatformImage);
        Assert.Equal(address, buffer.Address);
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);
        Assert.False(buffer.IsDisposed);
        buffer.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void HandOff_CodecThrows_BufferIsFreed_AndTheErrorPropagates()
    {
        var before = NativePixelMemory.LiveCount;
        var staging = Resampled(8, 8, 8, 8, 3);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new LibRawDecoder(new ThrowingCodec()).HandOff(staging, false, 8, 8, () => true));

        Assert.Equal("codec failed", ex.Message);
        staging.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void HandOff_WithoutCodec_StillReportsSourceHeldWhenTheRgbBufferIsOpen()
    {
        var stages = new List<string>();
        using var staging = Resampled(4, 4, 4, 4, 3);

        _ = new LibRawDecoder(stages.Add).HandOff(staging, false, 4, 4, () => false);

        Assert.Equal(["source-held", "bitmap-created"], stages);
    }

    [Fact]
    public void BgraBuffer_Dispose_FreesTheNativeMemoryExactlyOnce()
    {
        var before = NativePixelMemory.LiveCount;
        var staging = new RgbBgraResampler.BgraBuffer(16, 8);
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);

        staging.Dispose();
        staging.Dispose();

        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void HandOff_ReportsSourceReleased_WhileTheStagingBufferIsStillOwned()
    {
        using var staging = Resampled(6, 6, 6, 6, 3);
        IntPtr pointerAtStage = IntPtr.Zero;
        var decoder = new LibRawDecoder(PixelBufferImageCodec.Instance, stage =>
        {
            if (stage == "source-released") pointerAtStage = staging.Pointer;
        });

        var image = decoder.HandOff(staging, false, 6, 6, () => true);

        Assert.NotEqual(IntPtr.Zero, pointerAtStage);
        ((PixelBuffer)image.PlatformImage).Dispose();
    }

    [Fact]
    public void BgraBuffer_TakePixels_TransfersOwnershipOnce()
    {
        var staging = new RgbBgraResampler.BgraBuffer(3, 2);

        var pixels = staging.TakePixels();

        Assert.Equal((3, 2, 12), (pixels.Width, pixels.Height, pixels.Stride));
        Assert.Equal(PixelLayout.Bgr32, pixels.Layout);
        Assert.Equal(IntPtr.Zero, staging.Pointer);
        Assert.Throws<ObjectDisposedException>(staging.TakePixels);
        Assert.Throws<ObjectDisposedException>(() => staging.AsSpan());
        staging.Dispose();
        Assert.False(pixels.IsDisposed);
        pixels.Dispose();
    }

    [Fact]
    public void BgraBuffer_Alignment_IsThePixelBufferContract()
    {
        using var staging = new RgbBgraResampler.BgraBuffer(7, 3);

        Assert.Equal(0, (long)staging.Pointer % 64);
        Assert.Equal(7 * 4 * 3, staging.AsSpan().Length);
    }

    private sealed class ThrowingCodec : IPlatformImageCodec
    {
        public string Name => "throwing";
        public object FromPixels(PixelBuffer pixels) => throw new InvalidOperationException("codec failed");
        public PixelLease ToPixels(object platformImage) => throw new NotSupportedException();
    }
}

/// <summary>Real LibRaw decode through both paths on a corpus sample (skipped, like the other corpus tests, when it is absent).</summary>
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawPixelBufferNativeParityTests
{
    private const string SampleName = "Canon - EOS 350D - RAW (3_2).CR2";

    [Theory]
    [InlineData(0, 0)]
    [InlineData(320, 240)]
    public void Decode_CodecPath_EqualsWpfPath_ByteForByte(int boxWidth, int boxHeight)
    {
        if (RawCorpus.TryGetFile(SampleName) is not { } path) return;
        var box = boxWidth == 0 ? DecodeBox.Unbounded : new DecodeBox(boxWidth, boxHeight);
        var request = new DecodeRequest(path, box);

        var expected = Assert.IsType<WpfDecodedImage>(new LibRawDecoder().Decode(request));
        var actual = Assert.IsType<DecodedImage>(new LibRawDecoder(PixelBufferImageCodec.Instance).Decode(request));

        var buffer = Assert.IsType<PixelBuffer>(actual.PlatformImage);
        PixelAssert.Equal(expected.Source, buffer);
        Assert.Equal((expected.OriginalWidth, expected.OriginalHeight), (actual.OriginalWidth, actual.OriginalHeight));
        Assert.Equal(expected.Downscaled, actual.Downscaled);
        buffer.Dispose();
    }
}
