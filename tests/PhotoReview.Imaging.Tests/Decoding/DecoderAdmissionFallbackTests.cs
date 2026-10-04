using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.TestSupport;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// R06: a memory-admission refusal (the output would not fit the RAM left) must reach the caller as that refusal. It must not be
/// classified as a corrupt image and handed to the WPF fallback, whose decoder has no equivalent admission (it would allocate the
/// very surface the primary refused). A genuinely corrupt image still falls back.
/// </summary>
[Collection("GlobalState")] // the guards build localized text through the ambient localizer
public sealed class DecoderAdmissionFallbackTests
{
    private const long TwoGb = 2L * 1024 * 1024 * 1024;

    [Fact]
    public void Decode_WicOutputAdmissionRefusal_PropagatesWithoutUsingTheFallback()
    {
        var fallback = new CountingDecoder();
        var metrics = new ReviewMetrics();
        var primary = new ThrowingDecoder(() =>
            WicDirectDecoder.EnsureOutputFits(16384, 16384, 1024L * 1024 * 1024, () => (TwoGb / 4, 0)));
        var decoder = new FallbackImageDecoder(primary, DecoderBackend.WicDirect, fallback, metrics: metrics);

        var ex = Assert.Throws<DecoderMemoryAdmissionException>(() => decoder.Decode(new DecodeRequest("big.jpg", TargetWidth: 0)));

        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0, metrics.Snapshot().DecoderFallbackCount);
        Assert.Contains("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_TurboJpegOutputAdmissionRefusal_PropagatesWithoutUsingTheFallback()
    {
        var bomb = WithClaimedSize(SmallJpeg(), 8192, 4096); // 128 MiB output, over a 64 MiB machine
        var fallback = new CountingDecoder();
        var primary = new TurboJpegDecoder { MemoryInfo = () => (64L * 1024 * 1024, 0) };
        var decoder = new FallbackImageDecoder(primary, DecoderBackend.TurboJpeg, fallback);

        var ex = Assert.Throws<DecoderMemoryAdmissionException>(
            () => decoder.Decode(new DecodeRequest("bomb.jpg", DecodeBox.Unbounded, bytes: bomb)));

        Assert.Equal(0, fallback.Calls);
        Assert.Contains("too large for the available memory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsFallbackable_AdmissionRefusal_IsFalse()
    {
        Assert.False(FallbackImageDecoder.IsFallbackable(new DecoderMemoryAdmissionException("no room")));
        Assert.True(FallbackImageDecoder.IsFallbackable(new InvalidDataException("bad scan data")));
    }

    [Fact]
    public void Decode_CorruptImageInvalidData_StillFallsBack()
    {
        var fallback = new CountingDecoder();
        var decoder = new FallbackImageDecoder(
            new ThrowingDecoder(() => throw new InvalidDataException("bad scan data")), DecoderBackend.TurboJpeg, fallback);

        Assert.Throws<FallbackReachedException>(() => decoder.Decode(new DecodeRequest("bad.jpg", TargetWidth: 0)));

        Assert.Equal(1, fallback.Calls);
    }

    private static byte[] SmallJpeg()
    {
        using var stream = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(FixtureGenerator.CreateGradientCheckerboard(64, 48)));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] WithClaimedSize(byte[] jpeg, int width, int height)
    {
        var patched = (byte[])jpeg.Clone();
        for (var i = 2; i + 9 < patched.Length; i++)
        {
            if (patched[i] != 0xFF || (patched[i + 1] != 0xC0 && patched[i + 1] != 0xC2)) continue;
            patched[i + 5] = (byte)(height >> 8); patched[i + 6] = (byte)height;
            patched[i + 7] = (byte)(width >> 8); patched[i + 8] = (byte)width;
            return patched;
        }

        throw new InvalidOperationException("no SOF");
    }

    private sealed class FallbackReachedException : Exception { }

    private sealed class ThrowingDecoder(Action thrower) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) { thrower(); throw new InvalidOperationException("thrower returned"); }
        public ImageInfo ReadInfo(string path) { thrower(); throw new InvalidOperationException("thrower returned"); }
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        public int Calls { get; private set; }
        public IDecodedImage Decode(DecodeRequest request) { Calls++; throw new FallbackReachedException(); }
        public ImageInfo ReadInfo(string path) { Calls++; throw new FallbackReachedException(); }
    }
}
