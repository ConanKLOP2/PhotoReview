using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Tests.Fixtures;

namespace PhotoReview.Imaging.Tests.Decoding;

public sealed class DecoderContractRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "PhotoReview-DecoderContract-" + Guid.NewGuid().ToString("N"));

    public DecoderContractRegressionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void TurboSuccessReportsActualBackendAndOrientation()
    {
        string path = Path.Combine(_tempDir, "oriented.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation: 6);

        var decoded = new TurboJpegDecoder().Decode(
            new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));

        Assert.Equal(DecoderBackend.TurboJpeg, decoded.ActualBackend);
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal(48, decoded.PixelWidth);
        Assert.Equal(64, decoded.PixelHeight);
    }

    [Fact]
    public void WpfSuccessReportsOrientationMetadata()
    {
        string path = Path.Combine(_tempDir, "wpf-oriented.jpg");
        FixtureGenerator.GenerateJpegWithOrientation(path, 64, 48, orientation: 8);

        var decoded = new WpfBitmapImageDecoder().Decode(
            new DecodeRequest(path, TargetWidth: 0, ApplyOrientation: true));

        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(8, decoded.Orientation);
        Assert.Equal(48, decoded.PixelWidth);
        Assert.Equal(64, decoded.PixelHeight);
    }

    [Fact]
    public void TurboHeaderFailureAttemptsWpfFallbackExactlyOnceAndPreservesFinalException()
    {
        string path = Path.Combine(_tempDir, "bad-header.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD9]);
        var fallback = new CountingDecoder(new WpfBitmapImageDecoder());
        var decoder = new FallbackImageDecoder(
            new TurboJpegDecoder(), DecoderBackend.TurboJpeg,
            fallback, DecoderBackend.Wpf);

        Assert.ThrowsAny<Exception>(() => decoder.Decode(new DecodeRequest(path, 0)));
        Assert.Equal(1, fallback.DecodeCalls);
    }

    [Fact]
    public void TurboScanFailureAttemptsWpfFallbackExactlyOnce()
    {
        string path = Path.Combine(_tempDir, "bad-scan.jpg");
        FixtureGenerator.GenerateGradientJpeg(path, 256, 192);
        byte[] completeJpeg = File.ReadAllBytes(path);
        File.WriteAllBytes(path, completeJpeg[..(completeJpeg.Length / 2)]);
        var fallback = new CountingDecoder(new WpfBitmapImageDecoder());
        var decoder = new FallbackImageDecoder(
            new TurboJpegDecoder(), DecoderBackend.TurboJpeg,
            fallback, DecoderBackend.Wpf);

        IDecodedImage decoded = decoder.Decode(new DecodeRequest(path, 0));
        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(1, fallback.DecodeCalls);
    }

    [Theory]
    [MemberData(nameof(NonFallbackableExceptions))]
    public void NonFallbackableFailuresAreNotRetried(Exception failure)
    {
        var fallback = new CountingDecoder(new ConstantDecoder());
        var decoder = new FallbackImageDecoder(
            new ThrowingDecoder(failure), DecoderBackend.TurboJpeg,
            fallback, DecoderBackend.Wpf);

        Exception actual = Assert.Throws(failure.GetType(), () =>
            decoder.Decode(new DecodeRequest("unused.jpg", 0)));

        Assert.Same(failure, actual);
        Assert.Equal(0, fallback.DecodeCalls);
    }

#pragma warning disable CA2201 // Runtime exceptions are intentional contract probes.
    public static TheoryData<Exception> NonFallbackableExceptions() => new()
    {
        new FileNotFoundException("missing"),
        new OperationCanceledException("cancelled"),
        new OutOfMemoryException("oom"),
        new InvalidOperationException("programming failure")
    };
#pragma warning restore CA2201

    private sealed class CountingDecoder(IImageDecoder inner) : IImageDecoder
    {
        public int DecodeCalls { get; private set; }
        public IDecodedImage Decode(DecodeRequest request)
        {
            DecodeCalls++;
            return inner.Decode(request);
        }
        public ImageInfo ReadInfo(string path) => inner.ReadInfo(path);
    }

    private sealed class ThrowingDecoder(Exception failure) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw failure;
        public ImageInfo ReadInfo(string path) => throw failure;
    }

    private sealed class ConstantDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            return new WpfDecodedImage(bitmap);
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
    }
}
