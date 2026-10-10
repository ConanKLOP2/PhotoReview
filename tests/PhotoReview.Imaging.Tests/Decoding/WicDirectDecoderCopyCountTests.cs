using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-03 copy count (thẻ: "đường WIC -> PixelBuffer -> BitmapSource đúng 2 copy (như cũ)"). A counting codec sees every
/// buffer the decoder produces: exactly one <see cref="PixelBuffer"/> per decode, already at the final (scaled, oriented) size,
/// filled by WIC's single <c>CopyPixels</c>. With the WPF codec that buffer is copied once more into the BitmapSource and freed
/// (2 copies, as before); with the pixel codec it IS the result (1 copy). Native pixel memory is counted process-wide, so the
/// class runs in the non-parallel GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class WicDirectDecoderCopyCountTests : IDisposable
{
    private readonly TempRoot _root = new("wp03-copies");

    public void Dispose() => _root.Dispose();

    /// <summary>Live native buffers after every unreachable one (leaked by an earlier test) has been finalized, so the count
    /// cannot drop under the test's feet.</summary>
    private static long SettledLiveCount()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return NativePixelMemory.LiveCount;
    }

    private sealed class CountingCodec(IPlatformImageCodec inner) : IPlatformImageCodec
    {
        public List<(PixelBuffer Buffer, int Width, int Height, PixelLayout Layout)> Received { get; } = [];

        public string Name => "counting-" + inner.Name;

        public object FromPixels(PixelBuffer pixels)
        {
            Received.Add((pixels, pixels.Width, pixels.Height, pixels.Layout));
            return inner.FromPixels(pixels);
        }

        public PixelLease ToPixels(object platformImage) => inner.ToPixels(platformImage);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(3, true)]
    public void WpfCodec_OneBufferPerDecode_CopiedIntoTheBitmapAndFreed(ushort orientation, bool downscale)
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("w" + orientation + downscale + ".jpg"), 120, 80, orientation);
        var codec = new CountingCodec(WpfBitmapSourceCodec.Instance);
        var before = SettledLiveCount();

        var decoded = new WicDirectDecoder(codec).Decode(new DecodeRequest(path, downscale ? new DecodeBox(50, 50) : new DecodeBox(0, 0)));

        var received = Assert.Single(codec.Received);
        Assert.Equal((decoded.PixelWidth, decoded.PixelHeight), (received.Width, received.Height));
        Assert.Equal(PixelLayout.Bgr32, received.Layout);
        Assert.True(received.Buffer.IsDisposed); // copy 2 went into MIL; the scratch buffer is gone
        Assert.IsAssignableFrom<BitmapSource>(decoded.PlatformImage);
        Assert.Equal(before, NativePixelMemory.LiveCount); // nothing else allocated and left behind
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, true)]
    public void PixelCodec_TheDecodedImageIsTheVeryBufferWicFilled(ushort orientation, bool downscale)
    {
        var path = FixtureGenerator.GenerateJpegWithOrientation(_root.Combine("p" + orientation + ".jpg"), 120, 80, orientation);
        var codec = new CountingCodec(PixelBufferImageCodec.Instance);
        var before = SettledLiveCount();

        var decoded = new WicDirectDecoder(codec).Decode(new DecodeRequest(path, downscale ? new DecodeBox(50, 50) : new DecodeBox(0, 0)));

        var received = Assert.Single(codec.Received);
        Assert.Same(received.Buffer, decoded.PlatformImage);
        Assert.False(received.Buffer.IsDisposed);
        Assert.Equal(before + 1, NativePixelMemory.LiveCount);
        received.Buffer.Dispose();
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void FailedDecode_LeavesNoNativeBufferBehind_AndNeverReachesTheCodec()
    {
        var truncated = FixtureGenerator.GenerateTruncatedJpeg(
            FixtureGenerator.GenerateGradientJpeg(_root.Combine("src.jpg"), 400, 300), _root.Combine("cut.jpg"), 0.3);
        var codec = new CountingCodec(PixelBufferImageCodec.Instance);
        var before = SettledLiveCount();

        var error = Record.Exception(() => new WicDirectDecoder(codec).Decode(new DecodeRequest(truncated, 0)));

        // WIC either refuses the truncated stream (no buffer reaches the codec) or decodes the readable part; never a leak.
        if (error is not null)
        {
            Assert.Empty(codec.Received);
        }
        else
        {
            Assert.Single(codec.Received).Buffer.Dispose();
        }

        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void EmbeddedThumbnail_WpfCodec_LeavesNoNativeBufferBehind(int orientation)
    {
        var path = _root.Combine("thumb" + orientation + ".jpg");
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)orientation);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(48, 32),
            FixtureGenerator.CreateGradientCheckerboard(16, 8), metadata, null));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var before = SettledLiveCount();

        // Scratch buffer and (orientation 5) the pre-rotation buffer ApplyOrientation replaced are both freed.
        var image = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);

        Assert.NotNull(image);
        Assert.IsAssignableFrom<BitmapSource>(image.PlatformImage);
        Assert.Equal(before, NativePixelMemory.LiveCount);
    }

    [Fact]
    public void Constructor_RequiresACodec()
    {
        Assert.Throws<ArgumentNullException>(() => new WicDirectDecoder(null!));
        Assert.Throws<ArgumentNullException>(() => EmbeddedThumbnailReader.TryRead(_root.Combine("x.jpg"), null!));
    }

    [Fact]
    public void BytesRequest_AlsoProducesExactlyOneBuffer()
    {
        var bytes = File.ReadAllBytes(FixtureGenerator.GenerateGradientJpeg(_root.Combine("b.jpg"), 64, 48));
        var codec = new CountingCodec(PixelBufferImageCodec.Instance);

        var decoded = new WicDirectDecoder(codec).Decode(new DecodeRequest("label.jpg", 0, Bytes: bytes));

        Assert.Same(Assert.Single(codec.Received).Buffer, decoded.PlatformImage);
        ((PixelBuffer)decoded.PlatformImage).Dispose();
    }
}
