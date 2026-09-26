using System;
using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Failures a native/COM primary decoder raises that the WPF fallback can still decode around (a failed native call,
/// a failed COM cast, an overflowing size computation) must fall back, and never-fallback types must keep propagating.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FallbackExceptionCoverageTests : IDisposable
{
    private readonly TempRoot _root = new("fallback-exceptions");

    public void Dispose() => _root.Dispose();

    [Theory(DisplayName = "A primary decoder failing with a fallbackable exception type is retried on the fallback")]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(NotSupportedException))]
    public void FallbackableTypes_UseTheFallback(Type exceptionType)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(Path.Combine(_root.Path, "a.jpg"), 32, 24);
        var decoder = new FallbackImageDecoder(new ThrowingDecoder(exceptionType), DecoderBackend.TurboJpeg,
            new WpfBitmapImageDecoder(), DecoderBackend.Wpf);

        var decoded = decoder.Decode(new DecodeRequest(path, TargetWidth: 0));
        var info = decoder.ReadInfo(path);

        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(32, decoded.PixelWidth);
        Assert.Equal(32, info.Width);
    }

    [Theory(DisplayName = "Missing files, cancellation and out-of-memory never fall back")]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(OutOfMemoryException))]
    public void NonFallbackableTypes_Propagate(Type exceptionType)
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(Path.Combine(_root.Path, "b.jpg"), 32, 24);
        var decoder = new FallbackImageDecoder(new ThrowingDecoder(exceptionType), DecoderBackend.TurboJpeg,
            new WpfBitmapImageDecoder(), DecoderBackend.Wpf);

        Assert.Throws(exceptionType, () => decoder.Decode(new DecodeRequest(path, TargetWidth: 0)));
        Assert.Throws(exceptionType, () => decoder.ReadInfo(path));
    }

    private sealed class ThrowingDecoder(Type exceptionType) : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw (Exception)Activator.CreateInstance(exceptionType)!;
        public ImageInfo ReadInfo(string path) => throw (Exception)Activator.CreateInstance(exceptionType)!;
    }
}
