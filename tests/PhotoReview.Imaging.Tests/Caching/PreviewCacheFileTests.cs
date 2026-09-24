using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.Tests.Quality;
using Xunit;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>
/// perf(cache) v4: <see cref="PreviewCacheFile"/> is the single-file (header + JPEG payload)
/// replacement for the old PNG + ".meta" companion preview cache entry. Exercised directly here
/// (no <see cref="PreviewImageService"/> plumbing) via its public, framework-agnostic
/// <see cref="IDecodedImage"/>-based overloads -- the BitmapSource-based ones are internal (K-1:
/// PhotoReview.Imaging.Caching's public surface must not expose System.Windows.Media types) -- so
/// header round-trip, version handling and output pixel format are each pinned down in isolation.
/// </summary>
[Trait("Category", "Slow")]
public sealed class PreviewCacheFileTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "PhotoReview-PreviewCacheFile-" + Guid.NewGuid().ToString("N"));

    public PreviewCacheFileTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string CachePath([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        Path.Combine(_tempDir, name + ".pv4");

    // The cache only accepts opaque bitmaps (IMG-01); the gradient fixture is Bgra32 with every alpha 255,
    // so convert it to the opaque format real decoders produce for opaque sources.
    private static BitmapSource AsOpaque(BitmapSource bitmap)
    {
        if (!PreviewCacheFile.HasAlpha(new WpfDecodedImage(bitmap))) return bitmap;
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static WpfDecodedImage ToDecodedImage(BitmapSource bitmap, DecoderBackend backend, int orientation, int originalWidth = 0, int originalHeight = 0) =>
        new WpfDecodedImage(AsOpaque(bitmap), downscaled: true, orientation: orientation, actualBackend: backend, originalWidth: originalWidth, originalHeight: originalHeight);

    /// <summary>
    /// A synthetic gradient checkerboard is a torture test for JPEG (hard tile edges at high
    /// spatial frequency) that a real photo's much softer, noisier detail never presents -- PSNR
    /// and size comparisons below use this noisier variant instead, matching the "photo-like"
    /// image PreviewCacheFormatBenchmarkTests measured the format decision against.
    /// </summary>
    private static BitmapSource CreatePhotoLikeBitmap(int width, int height)
    {
        var bitmap = FixtureGenerator.CreateGradientCheckerboard(width, height);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var rng = new Random(2024);
        var noise = new byte[pixels.Length];
        rng.NextBytes(noise);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = AddNoise(pixels[i], noise[i]);
            pixels[i + 1] = AddNoise(pixels[i + 1], noise[i + 1]);
            pixels[i + 2] = AddNoise(pixels[i + 2], noise[i + 2]);
            pixels[i + 3] = 255;
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte AddNoise(byte channel, byte noiseByte) =>
        (byte)Math.Clamp(channel + ((noiseByte % 32) - 16), 0, 255);

    [Fact(DisplayName = "A written entry round-trips its backend, orientation and pixel dimensions")]
    public async Task WriteThenReadRoundTripsHeaderFields()
    {
        var source = CreatePhotoLikeBitmap(220, 160);
        var path = CachePath();

        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.TurboJpeg, orientation: 6), path);
        var result = PreviewCacheFile.ReadAsDecodedImage(path);

        Assert.Equal(DecoderBackend.TurboJpeg, result.ActualBackend);
        Assert.Equal(6, result.Orientation);
        Assert.Equal(source.PixelWidth, result.PixelWidth);
        Assert.Equal(source.PixelHeight, result.PixelHeight);
        var resultBitmap = Assert.IsAssignableFrom<BitmapSource>(result.PlatformImage);
        Assert.True(resultBitmap.IsFrozen);

        // JPEG q95 is lossy but must stay visually close to the source for a downscaled preview.
        // (PreviewCacheFormatBenchmarkTests measures ~34 dB on a full 2190x1460 preview; this
        // tiny fixture is noisier per pixel relative to its size, so the bar here is lower.)
        var compare = ImageCompare.Compare(source, resultBitmap);
        Assert.True(compare.Psnr > 20, $"Expected PSNR > 20 dB, got {compare.Psnr:F2}.");
    }

    [Fact(DisplayName = "A written entry round-trips original (pre-downscale) dimensions separately from its own pixel dimensions")]
    public async Task WriteThenReadRoundTripsOriginalDimensions()
    {
        // Simulates a downscaled preview: the persisted bitmap is smaller than the source photo
        // it was decoded from, so OriginalWidth/Height must differ from PixelWidth/Height.
        var source = CreatePhotoLikeBitmap(220, 160);
        var path = CachePath();

        await PreviewCacheFile.WriteAtomicallyAsync(
            ToDecodedImage(source, DecoderBackend.WicDirect, orientation: 1, originalWidth: 4400, originalHeight: 3200), path);
        var result = PreviewCacheFile.ReadAsDecodedImage(path);

        Assert.Equal(source.PixelWidth, result.PixelWidth);
        Assert.Equal(source.PixelHeight, result.PixelHeight);
        Assert.Equal(4400, result.OriginalWidth);
        Assert.Equal(3200, result.OriginalHeight);
    }

    [Fact(DisplayName = "The encoded entry is smaller than an equivalent 32-bit PNG of the same source")]
    public async Task EncodedEntryIsSmallerThanEquivalentPng()
    {
        // Not a strict format contract, just a sanity check that the measurement behind the
        // format decision (see PreviewCacheFormatBenchmarkTests) actually holds for a real write.
        var source = CreatePhotoLikeBitmap(512, 384);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var pngPath = Path.Combine(_tempDir, "reference.png");
        FixtureGenerator.SavePng(source, pngPath, is32Bit: true);

        Assert.True(new FileInfo(path).Length < new FileInfo(pngPath).Length);
    }

    [Fact(DisplayName = "A file with a version other than CurrentVersion is rejected")]
    public async Task MismatchedVersionIsRejected()
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(32, 32);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var bytes = File.ReadAllBytes(path);
        bytes[4] = unchecked((byte)(PreviewCacheFile.CurrentVersion + 1)); // offset 4 = version byte
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.ReadAsDecodedImage(path));
    }

    [Fact(DisplayName = "A legacy v5 entry (possibly alpha-flattened) is rejected so it gets re-decoded")]
    public async Task LegacyVersion5EntryIsRejected()
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(32, 32);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var bytes = File.ReadAllBytes(path);
        bytes[4] = 5; // offset 4 = version byte; v5 is what pre-IMG-01 builds wrote
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.ReadAsDecodedImage(path));
        Assert.True(PreviewCacheFile.CurrentVersion > 5);
    }

    [Fact(DisplayName = "A file with a bad magic is rejected")]
    public async Task BadMagicIsRejected()
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(32, 32);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.ReadAsDecodedImage(path));
    }

    [Fact(DisplayName = "A truncated file (header only, no payload) is rejected instead of decoding garbage")]
    public async Task TruncatedPayloadIsRejected()
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(32, 32);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..24]); // v5 header only (24 bytes), no JPEG payload

        Assert.Throws<InvalidDataException>(() => PreviewCacheFile.ReadAsDecodedImage(path));
    }

    [Fact(DisplayName = "Writing a bitmap with an alpha channel is rejected (JPEG cannot carry it)")]
    public async Task WriteAtomically_RejectsAlphaBitmap()
    {
        var pixels = new byte[4 * 4 * 4];
        var alpha = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, pixels, 16);
        var path = CachePath();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PreviewCacheFile.WriteAtomicallyAsync(
                new WpfDecodedImage(alpha, downscaled: true, orientation: 1, actualBackend: DecoderBackend.Wpf, originalWidth: 4, originalHeight: 4), path));
        Assert.False(File.Exists(path));
    }

    [Fact(DisplayName = "HasAlpha is true for alpha formats and translucent palettes, false for opaque formats")]
    public void HasAlpha_ClassifiesFormats()
    {
        var px = new byte[4 * 4 * 4];
        Assert.True(PreviewCacheFile.HasAlpha(new WpfDecodedImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, px, 16))));
        Assert.False(PreviewCacheFile.HasAlpha(new WpfDecodedImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, px, 16))));
        var translucent = new BitmapPalette([System.Windows.Media.Color.FromArgb(0, 0, 0, 0), System.Windows.Media.Colors.White]);
        var opaque = new BitmapPalette([System.Windows.Media.Colors.Black, System.Windows.Media.Colors.White]);
        Assert.True(PreviewCacheFile.HasAlpha(new WpfDecodedImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Indexed1, translucent, new byte[4], 1))));
        Assert.False(PreviewCacheFile.HasAlpha(new WpfDecodedImage(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Indexed1, opaque, new byte[4], 1))));
    }

    [Fact(DisplayName = "Reading an entry yields a render-native, opaque Bgr32 bitmap")]
    public async Task ReadYieldsRenderNativeOpaquePixelFormat()
    {
        // The source is Bgra32 (see FixtureGenerator.CreateGradientCheckerboard); JPEG can never
        // carry alpha, so the round-tripped bitmap must come back as opaque Bgr32, not whatever
        // format BitmapImage happens to decode a JPEG frame as.
        var source = FixtureGenerator.CreateGradientCheckerboard(48, 32);
        var path = CachePath();
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation: 1), path);

        var result = PreviewCacheFile.ReadAsDecodedImage(path);

        var resultBitmap = Assert.IsAssignableFrom<BitmapSource>(result.PlatformImage);
        Assert.Equal(PixelFormats.Bgr32, resultBitmap.Format);
    }

    [Theory(DisplayName = "Every EXIF orientation value (1-8) round-trips")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task OrientationRoundTrips(int orientation)
    {
        var source = FixtureGenerator.CreateGradientCheckerboard(32, 24);
        var path = Path.Combine(_tempDir, $"{nameof(OrientationRoundTrips)}-{orientation}.pv4");
        await PreviewCacheFile.WriteAtomicallyAsync(ToDecodedImage(source, DecoderBackend.Wpf, orientation), path);

        Assert.Equal(orientation, PreviewCacheFile.ReadAsDecodedImage(path).Orientation);
    }
}
