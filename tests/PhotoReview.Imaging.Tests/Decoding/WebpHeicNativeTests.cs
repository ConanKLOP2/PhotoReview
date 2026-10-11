using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Q-FMT-WEBP-HEIC against the real Windows codecs, on small WebP files generated in memory (<see cref="WebPTestImage"/>;
/// Windows has no WebP encoder). Native: each test returns early (skips) on a PC without the codec it needs.
/// HEIC: no sample can be generated without an HEVC encoder, so the HEIC test needs <c>PHOTOREVIEW_HEIC_SAMPLE</c> (a path
/// to a real .heic) plus the HEIF + HEVC codecs; otherwise it skips.
/// </summary>
[Trait("Category", "Native")]
public sealed class WebpHeicNativeTests : IDisposable
{
    private const uint Red = 0xFFFF0000u;
    private const uint Blue = 0xFF0000FFu;
    private const uint Green = 0xFF00FF00u;
    private const uint TransparentBlue = 0x000000FFu;

    private readonly TempRoot _root = new("webp-native");

    public void Dispose() => _root.Dispose();

    private static bool HasWebp => WicCodecAvailability.Current.WebP;

    private string Write(string name, byte[] bytes)
    {
        var path = _root.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Pixels(IDecodedImage image)
    {
        var bitmap = (BitmapSource)image.PlatformImage;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>BGRA bytes of pixel (x, y) of a 32-bit decode.</summary>
    private static uint BgraAt(IDecodedImage image, int x, int y)
    {
        var pixels = Pixels(image);
        var o = (y * image.PixelWidth + x) * 4;
        return (uint)(pixels[o] | pixels[o + 1] << 8 | pixels[o + 2] << 16 | pixels[o + 3] << 24); // = 0xAARRGGBB
    }

    [Fact]
    public void Probe_OnThisPc_EnumeratesTheBuiltInDecoders()
    {
        var decoders = WicCodecAvailability.EnumerateDecoders();

        Assert.Contains(decoders, d => d.ContainerFormat == new Guid("19e4a5aa-5662-4fc5-a0c0-1758028e1057")); // JPEG, always built in
        Assert.Equal(decoders.Any(d => d.ContainerFormat == WicCodecAvailability.ContainerFormatWebp), WicCodecAvailability.Current.WebP);
        Assert.False(string.IsNullOrEmpty(WicCodecAvailability.Current.Detail));
    }

    [Fact]
    public void Webp_LosslessWithTransparency_DecodesAsPremultipliedBgraWithItsAlpha()
    {
        if (!HasWebp) return;
        var path = Write("alpha.webp", WebPTestImage.Lossless(4, 2, (x, y) => x == 0 && y == 0 ? Red : x == 3 && y == 1 ? TransparentBlue : Blue));

        var info = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);
        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, TargetWidth: 0));

        Assert.Equal((4, 2, 1), (info.Width, info.Height, info.Orientation));
        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.Equal(PixelFormats.Pbgra32, ((BitmapSource)decoded.PlatformImage).Format);
        Assert.Equal(Red, BgraAt(decoded, 0, 0));
        Assert.Equal(Blue, BgraAt(decoded, 1, 0));
        Assert.Equal(0u, BgraAt(decoded, 3, 1)); // fully transparent, premultiplied: shown over the viewer background like a PNG
        Assert.True(WpfBitmapSourceCodec.HasAlpha(decoded));
    }

    [Fact]
    public async Task Webp_TransparentPreview_IsRefusedByTheJpegDiskCache_OpaqueOneRoundTrips()
    {
        if (!HasWebp) return;
        var transparent = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(
            Write("t.webp", WebPTestImage.Lossless(8, 8, (x, _) => x < 4 ? Blue : TransparentBlue)), TargetWidth: 0));
        var opaque = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(
            Write("o.webp", WebPTestImage.Lossless(8, 8, (x, _) => x < 4 ? Blue : Red)), TargetWidth: 0));

        // Transparency is never flattened into the JPEG disk cache (IMG-01/Q-R7): the write is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => PreviewCacheFile.WriteAtomicallyAsync(transparent, WpfBitmapSourceCodec.Instance, _root.Combine("t.prvc")));

        var cachePath = _root.Combine("o.prvc");
        await PreviewCacheFile.WriteAtomicallyAsync(opaque, WpfBitmapSourceCodec.Instance, cachePath);
        var reread = PreviewCacheFile.ReadAsDecodedImage(cachePath, WpfBitmapSourceCodec.Instance);
        Assert.Equal((8, 8), (reread.PixelWidth, reread.PixelHeight));
        Assert.Equal(DecoderBackend.WicDirect, reread.ActualBackend);
        var left = BgraAt(reread, 1, 4);
        Assert.True((left & 0xFF) > 200 && ((left >> 16) & 0xFF) < 60, $"left half should stay blue, got {left:X8}");
    }

    [Fact]
    public void Webp_ExifOrientation6_IsReportedAndApplied()
    {
        if (!HasWebp) return;
        // Stored 4x2, red marker top-left; orientation 6 = rotate 90 degrees clockwise for display.
        var path = Write("exif6.webp", WebPTestImage.WithExifOrientation(4, 2, (x, y) => x == 0 && y == 0 ? Red : Blue, exifOrientation: 6));

        var info = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);
        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, TargetWidth: 0));

        Assert.Equal(6, info.Orientation);
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal((2, 4), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((2, 4), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal(Red, BgraAt(decoded, 1, 0)); // top-left of the stored image ends up top-right
        Assert.Equal(Blue, BgraAt(decoded, 0, 0));
    }

    [Fact]
    public void Webp_ExifOrientation6_NotAppliedWhenTheCallerAsksForStoredPixels()
    {
        if (!HasWebp) return;
        var path = Write("exif6raw.webp", WebPTestImage.WithExifOrientation(4, 2, (x, y) => x == 0 && y == 0 ? Red : Blue, exifOrientation: 6));

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: false));

        Assert.Equal((4, 2), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(Red, BgraAt(decoded, 0, 0));
    }

    [Fact]
    public void Webp_Animated_ShowsTheFirstFrameOnly()
    {
        if (!HasWebp) return;
        var path = Write("anim.webp", WebPTestImage.Animated(3, 3, Green, Red, Blue));

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, TargetWidth: 0));

        Assert.Equal((3, 3), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.All(Enumerable.Range(0, 9), i => Assert.Equal(Green, BgraAt(decoded, i % 3, i / 3)));
    }

    [Fact]
    public void Webp_DecodeBox_Downscales()
    {
        if (!HasWebp) return;
        var path = Write("big.webp", WebPTestImage.Lossless(400, 200, (x, _) => x < 200 ? Red : Blue));

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, new DecodeBox(100, 100)));

        Assert.True(decoded.Downscaled);
        Assert.Equal((100, 50), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((400, 200), (decoded.OriginalWidth, decoded.OriginalHeight));
    }

    [Fact]
    public void Webp_ThroughTheRouter_DecodesWithWicWhateverTheSelectedBackend()
    {
        if (!HasWebp) return;
        var path = Write("routed.webp", WebPTestImage.Lossless(4, 4, (_, _) => Blue));
        var factory = new ImageDecoderFactory(
            [(DecoderBackend.TurboJpeg, () => new JpegOnlyDecoder())],
            decoderDecorator: (_, standard) => new WebpHeicRoutingDecoder(standard,
                new FallbackImageDecoder(new WicDirectDecoder(WpfBitmapSourceCodec.Instance), DecoderBackend.WicDirect, new WpfBitmapImageDecoder()),
                () => true, () => WicCodecAvailability.Current));

        var decoded = factory.Create(DecoderBackend.TurboJpeg).Decode(new DecodeRequest(path, TargetWidth: 0));

        Assert.Equal(DecoderBackend.WicDirect, decoded.ActualBackend);
        Assert.Equal(Blue, BgraAt(decoded, 2, 2));
    }

    [Fact]
    public void Heic_RealSample_DecodesWhenTheCodecsAndASampleArePresent()
    {
        var sample = Environment.GetEnvironmentVariable("PHOTOREVIEW_HEIC_SAMPLE");
        if (!WicCodecAvailability.Current.Heif || string.IsNullOrEmpty(sample) || !File.Exists(sample)) return;

        var info = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(sample);
        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(sample, new DecodeBox(800, 800)));

        Assert.True(info.Width > 0 && info.Height > 0);
        Assert.InRange(info.Orientation, 1, 8);
        var transposed = ExifOrientation.IsTransposed(decoded.Orientation);
        Assert.Equal(transposed ? info.Height : info.Width, decoded.OriginalWidth);
    }

    /// <summary>A backend that, like TurboJPEG, cannot read WebP at all.</summary>
    private sealed class JpegOnlyDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw new InvalidDataException("not a JPEG");

        public ImageInfo ReadInfo(string path) => throw new InvalidDataException("not a JPEG");
    }
}
