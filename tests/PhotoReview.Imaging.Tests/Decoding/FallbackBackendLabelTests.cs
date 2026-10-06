using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// <see cref="FallbackImageDecoder"/> stamps the backend that was ASKED to decode (or the fallback backend) on the result: a wrapped image
/// must keep every other property of the real image, and constructor arguments are validated.
/// </summary>
public sealed class FallbackBackendLabelTests
{
    private static readonly ExifSummary SomeExif = new() { CameraModel = "TestCam", Iso = 400 };

    private sealed class StubImage(DecoderBackend backend) : IDecodedImage
    {
        public int PixelWidth => 640;
        public int PixelHeight => 480;
        public bool Downscaled => true;
        public int Orientation => 6;
        public long EstimatedBytes => 1_234_567;
        public object PlatformImage { get; } = new object();
        public DecoderBackend ActualBackend => backend;
        public int OriginalWidth => 6000;
        public int OriginalHeight => 4000;
        public bool IsDegradedFallback => true;
        public ExifSummary? Exif => SomeExif;
    }

    private sealed class StubDecoder(Func<IDecodedImage> decode, Func<ImageInfo>? readInfo = null) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => decode();
        public ImageInfo ReadInfo(string path) => readInfo?.Invoke() ?? throw new NotSupportedException();
    }

    private static void AssertSameImageApartFromBackend(StubImage expected, IDecodedImage actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(expected.Downscaled, actual.Downscaled);
        Assert.Equal(expected.Orientation, actual.Orientation);
        Assert.Equal(expected.EstimatedBytes, actual.EstimatedBytes);
        Assert.Same(expected.PlatformImage, actual.PlatformImage);
        Assert.Equal(expected.OriginalWidth, actual.OriginalWidth);
        Assert.Equal(expected.OriginalHeight, actual.OriginalHeight);
        Assert.Equal(expected.IsDegradedFallback, actual.IsDegradedFallback);
        Assert.Same(SomeExif, actual.Exif);
    }

    [Fact(DisplayName = "A primary result labelled with another backend is relabelled as the requested backend, all other data preserved")]
    public void Decode_PrimaryReportsAnotherBackend_IsRelabelledAndKeepsEveryProperty()
    {
        var inner = new StubImage(DecoderBackend.Wpf);
        var decoder = new FallbackImageDecoder(new StubDecoder(() => inner), DecoderBackend.TurboJpeg, new StubDecoder(() => throw new InvalidOperationException()));

        var decoded = decoder.Decode(new DecodeRequest(@"C:\a.jpg", TargetWidth: 0));

        Assert.Equal(DecoderBackend.TurboJpeg, decoded.ActualBackend);
        Assert.NotSame(inner, decoded);
        AssertSameImageApartFromBackend(inner, decoded);
    }

    [Fact(DisplayName = "A primary result already labelled with the requested backend is returned as is")]
    public void Decode_PrimaryReportsTheRequestedBackend_ReturnsTheSameInstance()
    {
        var inner = new StubImage(DecoderBackend.TurboJpeg);
        var decoder = new FallbackImageDecoder(new StubDecoder(() => inner), DecoderBackend.TurboJpeg, new StubDecoder(() => throw new InvalidOperationException()));

        Assert.Same(inner, decoder.Decode(new DecodeRequest(@"C:\a.jpg", TargetWidth: 0)));
    }

    [Fact(DisplayName = "A fallback result labelled with another backend is relabelled as the fallback backend, all other data preserved")]
    public void Decode_FallbackReportsAnotherBackend_IsRelabelledAsTheFallbackBackend()
    {
        var inner = new StubImage(DecoderBackend.LibRaw);
        var primary = new StubDecoder(() => throw new NotSupportedException("primary cannot"));
        var decoder = new FallbackImageDecoder(primary, DecoderBackend.TurboJpeg, new StubDecoder(() => inner));

        var decoded = decoder.Decode(new DecodeRequest(@"C:\a.jpg", TargetWidth: 0));

        Assert.Equal(FallbackImageDecoder.FallbackBackend, decoded.ActualBackend);
        Assert.NotSame(inner, decoded);
        AssertSameImageApartFromBackend(inner, decoded);
    }

    [Fact(DisplayName = "A fallback result already labelled with the fallback backend is returned as is")]
    public void Decode_FallbackReportsTheFallbackBackend_ReturnsTheSameInstance()
    {
        var inner = new StubImage(FallbackImageDecoder.FallbackBackend);
        var decoder = new FallbackImageDecoder(new StubDecoder(() => throw new NotSupportedException()), DecoderBackend.TurboJpeg, new StubDecoder(() => inner));

        Assert.Same(inner, decoder.Decode(new DecodeRequest(@"C:\a.jpg", TargetWidth: 0)));
    }

    [Fact(DisplayName = "ReadInfo falls back on a fallbackable failure and returns the fallback's answer")]
    public void ReadInfo_PrimaryFailsFallbackable_ReturnsTheFallbackInfo()
    {
        var info = new ImageInfo(321, 123, 3);
        var decoder = new FallbackImageDecoder(
            new StubDecoder(() => throw new InvalidOperationException(), () => throw new NotSupportedException()),
            DecoderBackend.TurboJpeg,
            new StubDecoder(() => throw new InvalidOperationException(), () => info));

        Assert.Equal(info, decoder.ReadInfo(@"C:\a.jpg"));
    }

    [Fact(DisplayName = "A null primary or fallback decoder is rejected up front")]
    public void Constructor_NullDecoders_Throw()
    {
        var ok = new StubDecoder(() => throw new InvalidOperationException());

        var primary = Assert.Throws<ArgumentNullException>(() => new FallbackImageDecoder(null!, DecoderBackend.TurboJpeg, ok));
        var fallback = Assert.Throws<ArgumentNullException>(() => new FallbackImageDecoder(ok, DecoderBackend.TurboJpeg, null!));

        Assert.Equal("primary", primary.ParamName);
        Assert.Equal("fallback", fallback.ParamName);
    }
}
