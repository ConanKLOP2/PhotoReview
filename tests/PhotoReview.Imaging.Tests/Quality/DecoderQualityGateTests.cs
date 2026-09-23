using System;
using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;
using Xunit;

namespace PhotoReview.Imaging.Tests.Quality;

/// <summary>
/// Quality gate tests verifying all decoders against the quality criteria in REFACTOR-PLAN Section 4.4:
/// 1. Pixel fidelity (PSNR, MeanDeltaE) vs WPF baseline.
/// 2. Color accuracy and safe handling of embedded ICC profiles.
/// 3. Accurate geometric transforms across all 8 EXIF orientations.
/// 4. Robust handling of truncated/corrupt files without process crash.
/// 5. Immediate release of file handles (INV-8).
/// 6. Reliable fallback to WPF when backend encounters unsupported formats (INV-12).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DecoderQualityGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WpfBitmapImageDecoder _wpfDecoder = new();
    private readonly WicDirectDecoder _wicDecoder = new();
    private readonly TurboJpegDecoder _turboDecoder = new();
    private readonly ImageDecoderFactory _factory = new();

    public DecoderQualityGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PhotoReview-QualityGates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact(DisplayName = "QG-1.1: Full-res JPEG fidelity meets threshold for WicDirect and TurboJpeg")]
    public void FullResJpegFidelityMeetsThreshold()
    {
        var jpegPath = Path.Combine(_tempDir, "fidelity_full.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 400, 300, quality: 90);

        var decodedWpf = _wpfDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));
        var decodedWic = _wicDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));
        var decodedTurbo = _turboDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: 0));

        var bmpWpf = (BitmapSource)decodedWpf.PlatformImage;
        var bmpWic = (BitmapSource)decodedWic.PlatformImage;
        var bmpTurbo = (BitmapSource)decodedTurbo.PlatformImage;

        // WicDirect vs Wpf: Underlying WIC engine is shared, high fidelity
        var cmpWic = ImageCompare.Compare(bmpWpf, bmpWic);
        Assert.True(cmpWic.Psnr >= 40.0, $"WicDirect full-res PSNR {cmpWic.Psnr} < 40 dB");
        Assert.True(cmpWic.MeanDeltaE <= 1.0, $"WicDirect full-res MeanDeltaE {cmpWic.MeanDeltaE} > 1.0");

        // TurboJpeg vs Wpf: SIMD IDCT and optimized color space transform
        var cmpTurbo = ImageCompare.Compare(bmpWpf, bmpTurbo);
        Assert.True(cmpTurbo.Psnr >= 35.0, $"TurboJpeg full-res PSNR {cmpTurbo.Psnr} < 35 dB");
        Assert.True(cmpTurbo.MeanDeltaE <= 2.5, $"TurboJpeg full-res MeanDeltaE {cmpTurbo.MeanDeltaE} > 2.5");
    }

    [Fact(DisplayName = "QG-1.2: Downscaled JPEG fidelity meets threshold for WicDirect and TurboJpeg")]
    public void DownscaledJpegFidelityMeetsThreshold()
    {
        var jpegPath = Path.Combine(_tempDir, "fidelity_downscale.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 800, 600, quality: 90);

        const int targetWidth = 200;
        var decodedWpf = _wpfDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: targetWidth));
        var decodedWic = _wicDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: targetWidth));
        var decodedTurbo = _turboDecoder.Decode(new DecodeRequest(jpegPath, TargetWidth: targetWidth));

        var bmpWpf = (BitmapSource)decodedWpf.PlatformImage;
        var bmpWic = (BitmapSource)decodedWic.PlatformImage;
        var bmpTurbo = (BitmapSource)decodedTurbo.PlatformImage;

        Assert.Equal(targetWidth, decodedWpf.PixelWidth);
        Assert.Equal(targetWidth, decodedWic.PixelWidth);
        Assert.Equal(targetWidth, decodedTurbo.PixelWidth);

        // WicDirect downscaling vs Wpf
        var cmpWic = ImageCompare.Compare(bmpWpf, bmpWic);
        Assert.True(cmpWic.Psnr >= 30.0, $"WicDirect downscaled PSNR {cmpWic.Psnr} < 30 dB");
        Assert.True(cmpWic.MeanDeltaE <= 2.0, $"WicDirect downscaled MeanDeltaE {cmpWic.MeanDeltaE} > 2.0");

        // TurboJpeg DCT-downscaling vs Wpf
        var cmpTurbo = ImageCompare.Compare(bmpWpf, bmpTurbo);
        Assert.True(cmpTurbo.Psnr >= 28.0, $"TurboJpeg downscaled PSNR {cmpTurbo.Psnr} < 28 dB");
        Assert.True(cmpTurbo.MeanDeltaE <= 3.0, $"TurboJpeg downscaled MeanDeltaE {cmpTurbo.MeanDeltaE} > 3.0");
    }

    [Fact(DisplayName = "QG-2: Embedded ICC profiles are handled safely with accurate color or fallback")]
    public void EmbeddedIccProfilesHandledSafely()
    {
        var iccJpegPath = Path.Combine(_tempDir, "icc_profile.jpg");
        var generated = FixtureGenerator.GenerateJpegWithIcc(iccJpegPath, 128, 96);

        var bytes = File.ReadAllBytes(generated);
        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(bytes),
            "The deterministic fixture must contain a JPEG APP2 ICC profile.");

        // 1. WPF is the color-managed reference; direct WIC applies an IWICColorTransform to sRGB
        //    and must match it.
        var decodedWpf = _wpfDecoder.Decode(new DecodeRequest(generated, TargetWidth: 0));
        Assert.NotNull(decodedWpf);
        var decodedWic = _wicDecoder.Decode(new DecodeRequest(generated, TargetWidth: 0));
        Assert.Equal(DecoderBackend.WicDirect, decodedWic.ActualBackend);
        var cmpWic = ImageCompare.Compare(decodedWpf, decodedWic);
        Assert.True(cmpWic.MeanDeltaE <= 1.0, $"WicDirect ICC MeanDeltaE {cmpWic.MeanDeltaE} > 1.0");
        Assert.Equal(128, _wicDecoder.ReadInfo(generated).PixelWidth);

        // 2. TurboJpeg throws NotSupportedException to reject ICC rather than render incorrect colors
        Assert.Throws<NotSupportedException>(() => _turboDecoder.Decode(new DecodeRequest(generated, TargetWidth: 0)));

        // The unsafe direct path transparently resolves to WPF (INV-12); WicDirect needs no fallback.
        foreach (var (backend, expectedActual) in new[]
                 {
                     (DecoderBackend.WicDirect, DecoderBackend.WicDirect),
                     (DecoderBackend.TurboJpeg, DecoderBackend.Wpf)
                 })
        {
            var fallbackDecoder = _factory.Create(backend);
            var decodedFallback = fallbackDecoder.Decode(new DecodeRequest(generated, TargetWidth: 0));
            Assert.Equal(128, decodedFallback.PixelWidth);
            Assert.Equal(96, decodedFallback.PixelHeight);
            Assert.Equal(expectedActual, decodedFallback.ActualBackend);
            Assert.Equal(128, fallbackDecoder.ReadInfo(generated).PixelWidth);
        }
    }

    [Theory(DisplayName = "QG-3: All 8 EXIF orientations map correctly across all backends")]
    [InlineData((ushort)1, 64, 48, "Yellow", "Cyan", "Magenta", "White")]
    [InlineData((ushort)2, 64, 48, "Cyan", "Yellow", "White", "Magenta")]
    [InlineData((ushort)3, 64, 48, "White", "Magenta", "Cyan", "Yellow")]
    [InlineData((ushort)4, 64, 48, "Magenta", "White", "Yellow", "Cyan")]
    [InlineData((ushort)5, 48, 64, "Yellow", "Magenta", "Cyan", "White")]
    [InlineData((ushort)6, 48, 64, "Magenta", "Yellow", "White", "Cyan")]
    [InlineData((ushort)7, 48, 64, "White", "Cyan", "Magenta", "Yellow")]
    [InlineData((ushort)8, 48, 64, "Cyan", "White", "Yellow", "Magenta")]
    public void ExifOrientationsMapCorrectly(
        ushort orientation,
        int expectedW,
        int expectedH,
        string tl,
        string tr,
        string bl,
        string br)
    {
        var path = Path.Combine(_tempDir, $"orient_{orientation}.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation);

        var request = new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true);

        // Test WPF
        var resWpf = _wpfDecoder.Decode(request);
        Assert.Equal(expectedW, resWpf.PixelWidth);
        Assert.Equal(expectedH, resWpf.PixelHeight);
        AssertCorners(ImageCompare.ToBgra32((BitmapSource)resWpf.PlatformImage), expectedW, expectedH, tl, tr, bl, br);

        // Test WicDirect
        var resWic = _wicDecoder.Decode(request);
        Assert.Equal(expectedW, resWic.PixelWidth);
        Assert.Equal(expectedH, resWic.PixelHeight);
        AssertCorners(ImageCompare.ToBgra32((BitmapSource)resWic.PlatformImage), expectedW, expectedH, tl, tr, bl, br);

        // Test TurboJpeg
        var resTurbo = _turboDecoder.Decode(request);
        Assert.Equal(expectedW, resTurbo.PixelWidth);
        Assert.Equal(expectedH, resTurbo.PixelHeight);
        AssertCorners(ImageCompare.ToBgra32((BitmapSource)resTurbo.PlatformImage), expectedW, expectedH, tl, tr, bl, br);
    }

    [Fact(DisplayName = "QG-4: Truncated, empty, and invalid files fail safely without crashing")]
    public void CorruptFilesFailSafelyWithoutCrash()
    {
        var zeroPath = Path.Combine(_tempDir, "zero.jpg");
        FixtureGenerator.GenerateZeroByteFile(zeroPath);

        var textPath = Path.Combine(_tempDir, "text.jpg");
        FixtureGenerator.GenerateTextFile(textPath);

        var validJpeg = Path.Combine(_tempDir, "valid_for_trunc.jpg");
        FixtureGenerator.GenerateGradientJpeg(validJpeg, 64, 48);

        var truncatedHeaderPath = Path.Combine(_tempDir, "truncated_header.jpg");
        FixtureGenerator.GenerateTruncatedJpeg(validJpeg, truncatedHeaderPath, ratio: 0.01);

        var truncatedScanPath = Path.Combine(_tempDir, "truncated_scan.jpg");
        FixtureGenerator.GenerateTruncatedJpeg(validJpeg, truncatedScanPath, ratio: 0.5);

        var pngPath = Path.Combine(_tempDir, "not_jpeg.png");
        var bmp = FixtureGenerator.CreateGradientCheckerboard(32, 32);
        FixtureGenerator.SavePng(bmp, pngPath, is32Bit: true);

        IImageDecoder[] decoders = [_wpfDecoder, _wicDecoder, _turboDecoder];

        foreach (var decoder in decoders)
        {
            // Empty file
            Assert.ThrowsAny<Exception>(() => decoder.Decode(new DecodeRequest(zeroPath, TargetWidth: 0)));

            // Text file disguised as JPEG
            Assert.ThrowsAny<Exception>(() => decoder.Decode(new DecodeRequest(textPath, TargetWidth: 0)));

            // Truncated file header (must throw typed exception)
            Assert.ThrowsAny<Exception>(() => decoder.Decode(new DecodeRequest(truncatedHeaderPath, TargetWidth: 0)));

            // Half-truncated scan: must either throw typed exception or return partial frame safely, NEVER AccessViolationException or crash
            try
            {
                var result = decoder.Decode(new DecodeRequest(truncatedScanPath, TargetWidth: 0));
                Assert.NotNull(result);
            }
            catch (Exception ex)
            {
                Assert.IsNotType<AccessViolationException>(ex);
            }
        }

        // TurboJpeg throws NotSupportedException on PNG, but FallbackImageDecoder safely falls back
        Assert.Throws<NotSupportedException>(() => _turboDecoder.Decode(new DecodeRequest(pngPath, TargetWidth: 0)));
        var fallbackDecoder = _factory.Create(DecoderBackend.TurboJpeg);
        var resFallback = fallbackDecoder.Decode(new DecodeRequest(pngPath, TargetWidth: 0));
        Assert.NotNull(resFallback);
        Assert.Equal(DecoderBackend.Wpf, resFallback.ActualBackend);
    }

    [Fact(DisplayName = "QG-5: File handles are released immediately after decode across all backends (INV-8)")]
    public void FileHandlesReleasedImmediatelyAfterDecode()
    {
        IImageDecoder[] decoders = [_wpfDecoder, _wicDecoder, _turboDecoder];

        for (int i = 0; i < decoders.Length; i++)
        {
            var path = Path.Combine(_tempDir, $"inv8_{i}.jpg");
            FixtureGenerator.GenerateGradientJpeg(path, 64, 48);

            var decoded = decoders[i].Decode(new DecodeRequest(path, TargetWidth: 0));
            Assert.NotNull(decoded);

            // INV-8 proof: File can be immediately deleted or overwritten without sharing violation
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    [Fact(DisplayName = "QG-6: Missing files throw FileNotFoundException (INV-12)")]
    public void MissingFilesThrowFileNotFoundException()
    {
        var missingPath = Path.Combine(_tempDir, "non_existent.jpg");
        IImageDecoder[] decoders = [_wpfDecoder, _wicDecoder, _turboDecoder];

        foreach (var decoder in decoders)
        {
            Assert.Throws<FileNotFoundException>(() => decoder.Decode(new DecodeRequest(missingPath, TargetWidth: 0)));
            Assert.Throws<FileNotFoundException>(() => decoder.ReadInfo(missingPath));
        }
    }

    private static void AssertCorners(
        byte[] bgra,
        int w,
        int h,
        string tl,
        string tr,
        string bl,
        string br)
    {
        AssertColor(bgra, w, h, 1, 1, tl);
        AssertColor(bgra, w, h, w - 2, 1, tr);
        AssertColor(bgra, w, h, 1, h - 2, bl);
        AssertColor(bgra, w, h, w - 2, h - 2, br);
    }

    private static void AssertColor(byte[] bgra, int width, int height, int x, int y, string expectedColor)
    {
        var offset = (y * width + x) * 4;
        byte b = bgra[offset];
        byte g = bgra[offset + 1];
        byte r = bgra[offset + 2];

        switch (expectedColor)
        {
            case "Yellow":
                Assert.True(r > 150 && g > 150 && b < 100, $"Expected Yellow at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "Cyan":
                Assert.True(r < 100 && g > 150 && b > 150, $"Expected Cyan at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "Magenta":
                Assert.True(r > 150 && g < 100 && b > 150, $"Expected Magenta at ({x},{y}), got R={r},G={g},B={b}");
                break;
            case "White":
                Assert.True(r > 180 && g > 180 && b > 180, $"Expected White at ({x},{y}), got R={r},G={g},B={b}");
                break;
            default:
                throw new ArgumentException($"Unknown color: {expectedColor}", nameof(expectedColor));
        }
    }
}
