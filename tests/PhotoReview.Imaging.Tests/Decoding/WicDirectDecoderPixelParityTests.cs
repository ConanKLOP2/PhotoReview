using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Pixels;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;
using PhotoReview.TestSupport.Windows;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// WP-03: <see cref="WicDirectDecoder"/> now copies WIC's output into a <see cref="PixelBuffer"/> and hands it to the
/// <see cref="IPlatformImageCodec"/>. Over a corpus of 30 synthetic images (JPEG incl. gray/CMYK/odd/1-pixel sizes, PNG
/// 8/16-bit, gray, palette with transparency, TIFF incl. CMYK and 16-bit, BMP, GIF) x 8 orientations x {full, DecodeBox}:
/// <list type="number">
/// <item>the pixels are byte-identical (including the X byte of Bgr32) to the pre-WP-03 path, recorded inside the test as a
/// <see cref="BitmapSource"/> built exactly as the old decoder did (same WIC chain, <c>BitmapSource.Create</c> from the scratch
/// buffer, <c>Freeze</c>);</item>
/// <item>the WPF codec (<see cref="WpfBitmapSourceCodec"/>, the app's) and the pixel codec (<see cref="PixelBufferImageCodec"/>,
/// the future shell's) give the same bytes, format and metadata;</item>
/// <item>at full size the result also equals an independent WPF reference (<c>BitmapDecoder</c> + <c>FormatConvertedBitmap</c> +
/// <c>ExifOrientation.Apply</c>/<c>TransformedBitmap</c>).</item>
/// </list>
/// WebP lives in <see cref="WicDirectDecoderWebpParityTests"/> (needs the Windows codec: Category=Native). No HEIC: no encoder
/// to make a fixture with on this machine.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class WicDirectDecoderPixelParityTests : IClassFixture<WicDirectDecoderPixelParityTests.Corpus>
{
    private readonly Corpus _corpus;

    public WicDirectDecoderPixelParityTests(Corpus corpus) => _corpus = corpus;

    public static TheoryData<string, int, bool> Cases()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var name in Corpus.Names)
        {
            for (var orientation = 1; orientation <= 8; orientation++)
            {
                data.Add(name, orientation, false);
                data.Add(name, orientation, true);
            }
        }

        return data;
    }

    [Fact]
    public void Corpus_HasThirtyImages()
    {
        Assert.Equal(30, Corpus.Names.Count);
        Assert.Equal(30, _corpus.Paths.Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decode_MatchesThePreWp03PathByteForByte_WithBothCodecs(string name, int orientation, bool downscale)
    {
        var path = _corpus.Paths[name];
        var request = Request(path, orientation, downscale);

        var wpf = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(request);
        var pix = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(request);
        var legacy = LegacyWicDirect.Decode(path, request);

        var wpfBitmap = Assert.IsAssignableFrom<BitmapSource>(wpf.PlatformImage);
        Assert.True(wpfBitmap.IsFrozen);
        Assert.Equal(legacy.Bitmap.Format, wpfBitmap.Format);
        Assert.Equal((96.0, 96.0), (wpfBitmap.DpiX, wpfBitmap.DpiY));
        var pixels = Assert.IsType<PixelBuffer>(pix.PlatformImage);
        try
        {
            PixelAssert.Equal(legacy.Bitmap, pixels);
            PixelAssert.Equal(wpfBitmap, pixels);
            Assert.Equal(legacy.Bitmap.Format == PixelFormats.Bgr32 ? PixelLayout.Bgr32 : PixelLayout.Pbgra32, pixels.Layout);

            foreach (var image in new[] { wpf, pix })
            {
                Assert.Equal((legacy.Bitmap.PixelWidth, legacy.Bitmap.PixelHeight), (image.PixelWidth, image.PixelHeight));
                Assert.Equal(legacy.Downscaled, image.Downscaled);
                Assert.Equal(orientation, image.Orientation);
                Assert.Equal((legacy.OriginalWidth, legacy.OriginalHeight), (image.OriginalWidth, image.OriginalHeight));
                Assert.Equal((long)image.PixelWidth * image.PixelHeight * 4, image.EstimatedBytes);
                Assert.Equal(DecoderBackend.WicDirect, image.ActualBackend);
                Assert.False(image.IsDegradedFallback);
            }
        }
        finally
        {
            pixels.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(FullSizeCases))]
    public void FullDecode_EqualsAnIndependentWpfReference(string name, int orientation)
    {
        var path = _corpus.Paths[name];
        var decoded = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(Request(path, orientation, downscale: false));
        using var pixels = Assert.IsType<PixelBuffer>(decoded.PlatformImage);

        PixelAssert.Equal(WpfReference(path, pixels.Layout, orientation), pixels);
        Assert.False(decoded.Downscaled);
    }

    public static TheoryData<string, int> FullSizeCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in Corpus.Names)
            for (var orientation = 1; orientation <= 8; orientation++)
                data.Add(name, orientation);
        return data;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void EmbeddedExifOrientation_IsReadAndAppliedTheSameAsTheOldPath(int orientation)
    {
        var path = _corpus.Root.Combine("exif-" + orientation + ".jpg");
        if (!File.Exists(path)) FixtureGenerator.GenerateJpegWithOrientation(path, 45, 30, (ushort)orientation);
        var request = new DecodeRequest(path, new DecodeBox(20, 20));

        var decoded = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(request);
        var legacy = LegacyWicDirect.Decode(path, request);
        using var pixels = Assert.IsType<PixelBuffer>(decoded.PlatformImage);

        Assert.Equal(orientation, decoded.Orientation);
        Assert.Equal(ExifOrientation.IsTransposed(orientation) ? (30, 45) : (45, 30), (decoded.OriginalWidth, decoded.OriginalHeight));
        PixelAssert.Equal(legacy.Bitmap, pixels);
    }

    [Fact]
    public void ApplyOrientationFalse_KeepsTheStoredPixelGrid()
    {
        var path = _corpus.Root.Combine("exif-noapply.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 45, 30, 6);
        var request = new DecodeRequest(path, 0, ApplyOrientation: false);

        var decoded = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(request);
        using var pixels = Assert.IsType<PixelBuffer>(decoded.PlatformImage);

        Assert.Equal((45, 30), (pixels.Width, pixels.Height));
        PixelAssert.Equal(LegacyWicDirect.Decode(path, request).Bitmap, pixels);
    }

    internal static DecodeRequest Request(string path, int orientation, bool downscale)
    {
        // The box applies to the displayed image; roughly 60 % of each side forces a real resample (and, for the larger
        // JPEGs, the DCT pre-scale stage) while leaving 1-pixel images untouched.
        var box = downscale ? new DecodeBox(24, 18) : new DecodeBox(0, 0);
        return new DecodeRequest(path, box, applyOrientation: true, sourceOrientation: orientation);
    }

    /// <summary>The image as WPF itself decodes it at full size: frame -> format converter -> TransformedBitmap.</summary>
    private static BitmapSource WpfReference(string path, PixelLayout layout, int orientation)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        var format = layout == PixelLayout.Bgr32 ? PixelFormats.Bgr32 : PixelFormats.Pbgra32;
        BitmapSource converted = new FormatConvertedBitmap(frame, format, null, 0);
        converted.Freeze();
        return WpfExifOrientation.Apply(converted, orientation);
    }

    /// <summary>Thirty synthetic images, written once per test class.</summary>
    public sealed class Corpus : IDisposable
    {
        public static readonly IReadOnlyList<string> Names =
        [
            "jpeg-64x48-q90", "jpeg-61x37-q75", "jpeg-1x1", "jpeg-1x17", "jpeg-17x1", "jpeg-200x120-q95", "jpeg-gray-33x31",
            "jpeg-cmyk-40x30",
            "png-bgr24", "png-bgra32-alpha", "png-gray8", "png-gray16", "png-rgb48", "png-rgba64-alpha", "png-indexed8-transparent",
            "png-indexed4", "png-blackwhite", "png-1x1-alpha",
            "tiff-bgr32", "tiff-bgra32-alpha", "tiff-cmyk32", "tiff-rgb48", "tiff-gray16", "tiff-indexed8", "tiff-1x9",
            "bmp-bgr24", "bmp-bgr32", "bmp-1x1",
            "gif-indexed8-transparent", "gif-indexed8-opaque",
        ];

        public Corpus()
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in Names) paths[name] = Write(name);
            Paths = paths;
        }

        public TempRoot Root { get; } = new("wp03-parity");

        public IReadOnlyDictionary<string, string> Paths { get; }

        public void Dispose() => Root.Dispose();

        private string Write(string name)
        {
            (BitmapSource Source, BitmapEncoder Encoder, string Ext) entry = name switch
            {
                "jpeg-64x48-q90" => (Pattern(64, 48, 1), Jpeg(90), "jpg"),
                "jpeg-61x37-q75" => (Pattern(61, 37, 2), Jpeg(75), "jpg"),
                "jpeg-1x1" => (Pattern(1, 1, 3), Jpeg(90), "jpg"),
                "jpeg-1x17" => (Pattern(1, 17, 4), Jpeg(90), "jpg"),
                "jpeg-17x1" => (Pattern(17, 1, 5), Jpeg(90), "jpg"),
                "jpeg-200x120-q95" => (Pattern(200, 120, 6), Jpeg(95), "jpg"),
                "jpeg-gray-33x31" => (Convert(Pattern(33, 31, 7), PixelFormats.Gray8), Jpeg(90), "jpg"),
                "jpeg-cmyk-40x30" => (Convert(Pattern(40, 30, 8), PixelFormats.Cmyk32), Jpeg(90), "jpg"),
                "png-bgr24" => (Convert(Pattern(37, 29, 9), PixelFormats.Bgr24), new PngBitmapEncoder(), "png"),
                "png-bgra32-alpha" => (Convert(Pattern(37, 29, 10, alpha: true), PixelFormats.Bgra32), new PngBitmapEncoder(), "png"),
                "png-gray8" => (Convert(Pattern(31, 23, 11), PixelFormats.Gray8), new PngBitmapEncoder(), "png"),
                "png-gray16" => (Convert(Pattern(31, 23, 12), PixelFormats.Gray16), new PngBitmapEncoder(), "png"),
                "png-rgb48" => (Convert(Pattern(31, 23, 13), PixelFormats.Rgb48), new PngBitmapEncoder(), "png"),
                "png-rgba64-alpha" => (Convert(Pattern(31, 23, 14, alpha: true), PixelFormats.Rgba64), new PngBitmapEncoder(), "png"),
                "png-indexed8-transparent" => (Indexed(29, 21, 15, PixelFormats.Indexed8, transparent: true), new PngBitmapEncoder(), "png"),
                "png-indexed4" => (Indexed(29, 21, 16, PixelFormats.Indexed4, transparent: false), new PngBitmapEncoder(), "png"),
                "png-blackwhite" => (Convert(Pattern(29, 21, 17), PixelFormats.BlackWhite), new PngBitmapEncoder(), "png"),
                "png-1x1-alpha" => (Convert(Pattern(1, 1, 18, alpha: true), PixelFormats.Bgra32), new PngBitmapEncoder(), "png"),
                "tiff-bgr32" => (Convert(Pattern(35, 27, 19), PixelFormats.Bgr32), Tiff(), "tif"),
                "tiff-bgra32-alpha" => (Convert(Pattern(35, 27, 20, alpha: true), PixelFormats.Bgra32), Tiff(), "tif"),
                "tiff-cmyk32" => (Convert(Pattern(35, 27, 21), PixelFormats.Cmyk32), Tiff(), "tif"),
                "tiff-rgb48" => (Convert(Pattern(35, 27, 22), PixelFormats.Rgb48), Tiff(), "tif"),
                "tiff-gray16" => (Convert(Pattern(35, 27, 23), PixelFormats.Gray16), Tiff(), "tif"),
                "tiff-indexed8" => (Indexed(35, 27, 24, PixelFormats.Indexed8, transparent: false), Tiff(), "tif"),
                "tiff-1x9" => (Convert(Pattern(1, 9, 25), PixelFormats.Bgr24), Tiff(), "tif"),
                "bmp-bgr24" => (Convert(Pattern(33, 25, 26), PixelFormats.Bgr24), new BmpBitmapEncoder(), "bmp"),
                "bmp-bgr32" => (Convert(Pattern(33, 25, 27), PixelFormats.Bgr32), new BmpBitmapEncoder(), "bmp"),
                "bmp-1x1" => (Convert(Pattern(1, 1, 28), PixelFormats.Bgr24), new BmpBitmapEncoder(), "bmp"),
                "gif-indexed8-transparent" => (Indexed(27, 19, 29, PixelFormats.Indexed8, transparent: true), new GifBitmapEncoder(), "gif"),
                "gif-indexed8-opaque" => (Indexed(27, 19, 30, PixelFormats.Indexed8, transparent: false), new GifBitmapEncoder(), "gif"),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown corpus entry."),
            };

            var (source, encoder, ext) = entry;
            encoder.Frames.Add(BitmapFrame.Create(source));
            var path = Root.Combine(name + "." + ext);
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }

        private static JpegBitmapEncoder Jpeg(int quality) => new() { QualityLevel = quality };

        private static TiffBitmapEncoder Tiff() => new() { Compression = TiffCompressOption.None };

        /// <summary>Seeded noise (every pixel different, so a wrong flip/rotation shows), optionally with varying alpha.</summary>
        private static BitmapSource Pattern(int width, int height, int seed, bool alpha = false)
        {
            using var pixels = PixelAssert.CreatePattern(width, height, alpha ? PixelLayout.Pbgra32 : PixelLayout.Bgr32, seed);
            return PixelAssert.ToBitmapSource(pixels);
        }

        private static FormatConvertedBitmap Convert(BitmapSource source, PixelFormat format)
        {
            var converted = new FormatConvertedBitmap(source, format, null, 0);
            converted.Freeze();
            return converted;
        }

        /// <summary>Random palette indices over a palette whose entry 0 is fully transparent when <paramref name="transparent"/>.</summary>
        private static BitmapSource Indexed(int width, int height, int seed, PixelFormat format, bool transparent)
        {
            var rng = new Random(seed);
            var entries = format == PixelFormats.Indexed4 ? 16 : 256;
            var colors = new List<Color>(entries);
            for (var i = 0; i < entries; i++)
                colors.Add(Color.FromArgb(transparent && i == 0 ? (byte)0 : (byte)255, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
            var bitsPerPixel = format.BitsPerPixel;
            var stride = (width * bitsPerPixel + 7) / 8;
            var bytes = new byte[stride * height];
            rng.NextBytes(bytes);
            var bitmap = BitmapSource.Create(width, height, 96, 96, format, new BitmapPalette(colors), bytes, stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}

/// <summary>
/// The pre-WP-03 WicDirect output, rebuilt in the test as the reference ("BitmapSource cũ ghi làm tham chiếu ngay trong test"):
/// the same WIC chain (optional DCT pre-scale, HighQualityCubic resample, Bgr32/Pbgra32 converter, flip/rotator), then
/// <c>CopyPixels</c> into a scratch buffer and <c>BitmapSource.Create</c> + <c>Freeze</c>, exactly as the decoder did on
/// origin/master before WP-03. No ICC stage: the corpus has no colour profile.
/// </summary>
/// <summary>WebP half of the parity corpus: needs the Windows WebP codec (Category=Native, returns early without it).</summary>
[Trait("Category", "Native")]
public sealed class WicDirectDecoderWebpParityTests : IDisposable
{
    private readonly TempRoot _root = new("wp03-webp");

    public void Dispose() => _root.Dispose();

    public static TheoryData<bool, int, bool> Cases()
    {
        var data = new TheoryData<bool, int, bool>();
        foreach (var alpha in new[] { false, true })
            for (var orientation = 1; orientation <= 8; orientation++)
            {
                data.Add(alpha, orientation, false);
                data.Add(alpha, orientation, true);
            }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Webp_MatchesThePreWp03PathByteForByte(bool alpha, int orientation, bool downscale)
    {
        if (!WicCodecAvailability.Current.WebP) return;
        var path = _root.Combine((alpha ? "alpha" : "opaque") + ".webp");
        if (!File.Exists(path))
        {
            var rng = new Random(alpha ? 41 : 42);
            var colours = new uint[37 * 29];
            // The test encoder only writes simple prefix codes: at most two values per channel, picked at random per pixel
            // so every flip/rotation still moves distinguishable pixels.
            for (var i = 0; i < colours.Length; i++)
            {
                uint a = alpha && rng.Next(3) == 0 ? 0x40u : 0xFFu;
                uint r = rng.Next(2) == 0 ? 0x30u : 0xA0u, g = rng.Next(2) == 0 ? 0x20u : 0xC0u, b = rng.Next(2) == 0 ? 0x10u : 0xE0u;
                colours[i] = a << 24 | r << 16 | g << 8 | b;
            }
            File.WriteAllBytes(path, WebPTestImage.Lossless(37, 29, (x, y) => colours[y * 37 + x]));
        }

        var request = WicDirectDecoderPixelParityTests.Request(path, orientation, downscale);
        var decoded = new WicDirectDecoder(PixelBufferImageCodec.Instance).Decode(request);
        using var pixels = Assert.IsType<PixelBuffer>(decoded.PlatformImage);
        var wpf = Assert.IsAssignableFrom<BitmapSource>(new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(request).PlatformImage);

        var legacy = LegacyWicDirect.Decode(path, request);
        PixelAssert.Equal(legacy.Bitmap, pixels);
        PixelAssert.Equal(wpf, pixels);
        Assert.Equal(PixelLayout.Pbgra32, pixels.Layout); // the WebP codec reports 32bppBGRA even for opaque files
        Assert.Equal(legacy.Downscaled, decoded.Downscaled);
    }
}

internal static class LegacyWicDirect
{
    public sealed record Result(BitmapSource Bitmap, bool Downscaled, int OriginalWidth, int OriginalHeight);

    public static Result Decode(string path, DecodeRequest request)
    {
        var factory = WicDirectDecoder.CreateFactory();
        using var file = File.OpenRead(path);
        using var stream = new ManagedIStream(file);
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        var chain = new List<object>();
        try
        {
            factory.CreateDecoderFromStream(stream, IntPtr.Zero, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, out decoder);
            decoder.GetFrame(0, out frame);
            frame.GetSize(out uint origW, out uint origH);
            var orientation = request.ApplyOrientation ? request.SourceOrientation ?? WicExifReader.ReadOrientation(frame) : 1;
            var transposed = request.ApplyOrientation && ExifOrientation.IsTransposed(orientation);
            IWICBitmapSource current = (IWICBitmapSource)frame;
            var downscaled = false;
            if (request.IsDownscaleRequested)
            {
                var (fitW, fitH) = request.Box.FitStored((int)origW, (int)origH, transposed);
                uint targetW = (uint)fitW, targetH = (uint)fitH;
                if (targetW < origW || targetH < origH)
                {
                    if (frame is IWICBitmapSourceTransform transform)
                    {
                        uint nativeW = targetW, nativeH = targetH;
                        try
                        {
                            transform.GetClosestSize(ref nativeW, ref nativeH);
                            if (nativeW >= targetW && nativeH >= targetH && (nativeW < origW || nativeH < origH))
                            {
                                factory.CreateBitmapScaler(out var pre);
                                chain.Add(pre);
                                pre.Initialize(current, nativeW, nativeH, WICBitmapInterpolationMode.Fant);
                                current = (IWICBitmapSource)pre;
                            }
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                        }
                    }

                    factory.CreateBitmapScaler(out var scaler);
                    chain.Add(scaler);
                    scaler.Initialize(current, targetW, targetH, WICBitmapInterpolationMode.HighQualityCubic);
                    current = (IWICBitmapSource)scaler;
                    downscaled = true;
                }
            }

            current.GetPixelFormat(out Guid sourceFormat);
            var opaque = WicDirectDecoder.IsOpaqueFormat(sourceFormat);
            factory.CreateFormatConverter(out var converter);
            chain.Add(converter);
            var output = opaque ? WicGuids.GUID_WICPixelFormat32bppBGR : WicGuids.GUID_WICPixelFormat32bppPBGRA;
            converter.Initialize(current, ref output, WICBitmapDitherType.None, IntPtr.Zero, 0.0, WICBitmapPaletteType.Custom);
            current = (IWICBitmapSource)converter;
            if (request.ApplyOrientation && orientation > 1)
            {
                factory.CreateBitmapFlipRotator(out var rotator);
                chain.Add(rotator);
                rotator.Initialize(current, orientation switch
                {
                    2 => WICBitmapTransformOptions.FlipHorizontal,
                    3 => WICBitmapTransformOptions.Rotate180,
                    4 => WICBitmapTransformOptions.FlipVertical,
                    5 => WICBitmapTransformOptions.FlipHorizontal | WICBitmapTransformOptions.Rotate270,
                    6 => WICBitmapTransformOptions.Rotate90,
                    7 => WICBitmapTransformOptions.FlipHorizontal | WICBitmapTransformOptions.Rotate90,
                    _ => WICBitmapTransformOptions.Rotate270,
                });
                current = (IWICBitmapSource)rotator;
            }

            current.GetSize(out uint finalW, out uint finalH);
            var stride = (int)finalW * 4;
            var size = stride * (int)finalH;
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            BitmapSource bitmap;
            try
            {
                current.CopyPixels(IntPtr.Zero, (uint)stride, (uint)size, buffer);
                bitmap = BitmapSource.Create((int)finalW, (int)finalH, 96, 96, opaque ? PixelFormats.Bgr32 : PixelFormats.Pbgra32, null,
                    buffer, size, stride);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }

            bitmap.Freeze();
            return new Result(bitmap, downscaled, transposed ? (int)origH : (int)origW, transposed ? (int)origW : (int)origH);
        }
        finally
        {
            for (var i = chain.Count - 1; i >= 0; i--) WicDirectDecoder.SafeReleaseCom(chain[i]);
            WicDirectDecoder.SafeReleaseCom(frame);
            WicDirectDecoder.SafeReleaseCom(decoder);
            WicDirectDecoder.SafeReleaseCom(factory);
        }
    }
}
