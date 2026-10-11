using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Error-handling review (imaging): decode-path fixes with focused regression tests.</summary>
[Trait("Category", "HotPath")]
public sealed class ErrorHandlingReviewDecodingTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly TempRoot _root = new("errh-decode");

    public void Dispose() => _root.Dispose();

    /// <summary>Throws an IOException on the first open (a downscale-fallbackable failure), then serves the real file.</summary>
    private sealed class FailFirstOpenReader : ISourceReader
    {
        public int Opens { get; private set; }

        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024)
        {
            Opens++;
            if (Opens == 1) throw new IOException("simulated transient failure of the downscaled attempt");
            return PhysicalSourceReader.Instance.OpenSource(path, priority, bufferSize);
        }
    }

    [Fact(DisplayName = "Wpf downscale retry keeps the caller's SourceOrientation (the retry must not rebuild the request)")]
    public void DownscaleRetry_KeepsSourceOrientation()
    {
        // 64x48 stored, no EXIF orientation: the override (6 = rotate 90) is the only thing that can make the result portrait.
        var path = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_root.Path, "land.jpg"), 64, 48);
        var reader = new FailFirstOpenReader();

        var decoded = WpfBitmapImageDecoder.DecodeWithFallback(
            new DecodeRequest(path, TargetWidth: 32, ApplyOrientation: true, SourceOrientation: 6), reader);

        Assert.Equal(2, reader.Opens); // first attempt failed, the retry ran
        Assert.False(decoded.Downscaled);
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal(48, decoded.PixelWidth);
        Assert.Equal(64, decoded.PixelHeight);
    }

    [Fact(DisplayName = "TurboJpeg attaches the file bytes to a FALLBACKABLE failure only")]
    public void SourceBytes_AttachedOnlyForFallbackableFailure()
    {
        var notJpeg = _root.File("not.jpg", [1, 2, 3, 4, 5, 6, 7, 8]); // NotSupportedException: fallbackable
        var ex = Assert.ThrowsAny<Exception>(() => new TurboJpegDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(notJpeg, TargetWidth: 0)));
        Assert.True(FallbackImageDecoder.IsFallbackable(ex));
        Assert.True(DecodeFailureSourceBytes.TryGet(ex, out var bytes));
        Assert.Equal(8, bytes.Length);
    }

    [Fact(DisplayName = "TurboJpeg does not retain the file bytes on a NON-fallbackable failure")]
    public void SourceBytes_NotAttachedForNonFallbackableFailure()
    {
        // A header claiming 20000x20000 reaches the memory guard; a failing memory probe throws InvalidOperationException
        // (non-fallbackable: a programming error), which must not carry the file bytes.
        var jpeg = File.ReadAllBytes(FixtureGenerator.GenerateGradientJpeg(Path.Combine(_root.Path, "bomb.jpg"), 64, 48));
        for (var i = 2; i + 9 < jpeg.Length; i++)
        {
            if (jpeg[i] != 0xFF || (jpeg[i + 1] != 0xC0 && jpeg[i + 1] != 0xC2)) continue;
            jpeg[i + 5] = 0x4E; jpeg[i + 6] = 0x20; // 20000 = 0x4E20
            jpeg[i + 7] = 0x4E; jpeg[i + 8] = 0x20;
            break;
        }
        var path = _root.File("bomb2.jpg", jpeg);
        var decoder = new TurboJpegDecoder(WpfBitmapSourceCodec.Instance) { MemoryInfo = () => throw new InvalidOperationException("probe failed") };

        var ex = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.False(FallbackImageDecoder.IsFallbackable(ex));
        Assert.False(DecodeFailureSourceBytes.TryGet(ex, out _));
    }

    [Fact(DisplayName = "TurboJpeg refuses to read a huge source file into memory on a machine without the RAM (clean error before the allocation)")]
    public void HugeSourceFile_IsRefusedBeforeReading()
    {
        var path = Path.Combine(_root.Path, "huge.jpg");
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            fs.SetLength(MemoryHeadroom.GuardThresholdBytes + 1024);
        // Available memory SMALLER than the file: only the source-size guard can produce this rejection (the all-zero file would
        // otherwise fail later in the decode with a different NotSupportedException).
        var probed = 0;
        var decoder = new TurboJpegDecoder(WpfBitmapSourceCodec.Instance) { MemoryInfo = () => { probed++; return (MemoryHeadroom.GuardThresholdBytes / 2, 0); } };

        var ex = Assert.Throws<NotSupportedException>(() => decoder.Decode(new DecodeRequest(path, TargetWidth: 0)));

        Assert.Equal(1, probed); // the memory guard was consulted (and refused) before the file was read
        Assert.Contains("too large for the available memory", ex.ToString(), StringComparison.Ordinal);
        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.True(FallbackImageDecoder.IsFallbackable(ex)); // WIC streams it
        Assert.False(DecodeFailureSourceBytes.TryGet(ex, out _)); // nothing was read, so no source bytes ride on the exception
    }

    [Fact(DisplayName = "WicDirect output guard: a huge buffer on a small machine is a clean DecoderMemoryAdmissionException; a small buffer never consults memory")]
    public void WicOutputGuard_RefusesOnlyWhenItDoesNotFit()
    {
        var ex = Assert.Throws<DecoderMemoryAdmissionException>(() =>
            WicDirectDecoder.EnsureOutputFits(20000, 20000, 20000L * 20000 * 4, () => (1 * Gb, 0)));
        Assert.True(UserFacingError.IsLocalized(ex));

        WicDirectDecoder.EnsureOutputFits(20000, 20000, 20000L * 20000 * 4, () => (64 * Gb, 0)); // fits
        WicDirectDecoder.EnsureOutputFits(64, 48, 64 * 48 * 4, () => throw new InvalidOperationException("must not be consulted"));
    }

    [Fact(DisplayName = "WicDirect maps WIC's ArgumentException (E_INVALIDARG) to a fallbackable InvalidDataException")]
    public void WicInvalidArg_BecomesFallbackable()
    {
        var mapped = WicDirectDecoder.AsInvalidData(new ArgumentException("E_INVALIDARG"));

        Assert.True(FallbackImageDecoder.IsFallbackable(mapped));
        Assert.True(UserFacingError.IsLocalized(mapped));
        Assert.IsType<ArgumentException>(mapped.InnerException);
    }

    [Fact(DisplayName = "WicDirect still decodes a normal JPEG (guards and exception mapping add no failure on the happy path)")]
    public void WicDirect_HappyPath_Unchanged()
    {
        var path = FixtureGenerator.GenerateGradientJpeg(Path.Combine(_root.Path, "ok.jpg"), 64, 48);

        var decoded = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).Decode(new DecodeRequest(path, TargetWidth: 0));
        var info = new WicDirectDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(path);

        Assert.Equal(64, decoded.PixelWidth);
        Assert.Equal(64, info.Width);
    }

    [Fact(DisplayName = "A downscaled image with no real original size does not claim its small size as the original")]
    public void UnknownOriginal_IsNotReportedAsKnown()
    {
        var bitmap = FixtureGenerator.CreateGradientCheckerboard(32, 24);

        var unknown = new WpfDecodedImage(bitmap, downscaled: true); // original defaults to 32x24
        var known = new WpfDecodedImage(bitmap, downscaled: true, originalWidth: 640, originalHeight: 480);
        var full = new WpfDecodedImage(bitmap, downscaled: false);

        Assert.False(DecodedImageSize.HasKnownOriginal(unknown));
        Assert.True(DecodedImageSize.HasKnownOriginal(known));
        Assert.True(DecodedImageSize.HasKnownOriginal(full));
    }

    [Fact(DisplayName = "TurboJpegAvailability.Probe never throws and reports a reason when unavailable")]
    public void Availability_ProbeIsTotal()
    {
        var available = TurboJpegAvailability.Probe(out var reason);

        Assert.True(available ? reason is null : !string.IsNullOrEmpty(reason));
    }
}
