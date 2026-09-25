using System;
using System.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Tests.Fixtures;
using PhotoReview.Imaging.TurboJpeg;
using Xunit;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// Review round 7: TurboJpeg reads the whole JPEG before it gives up on an embedded ICC profile; the fallback decoder
/// must decode that copy instead of opening and reading the file again.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FallbackSourceBytesTests : IDisposable
{
    private readonly TempRoot _root = new("fallback-bytes");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "TurboJpeg -> fallback on an ICC JPEG hands the already-read bytes to the fallback decoder")]
    public void IccJpeg_FallbackDecodesFromBytesTurboJpegAlreadyRead()
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(Path.Combine(_root.Path, "icc.jpg"), 64, 48);
        var fileBytes = File.ReadAllBytes(path);
        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(fileBytes)); // precondition: TurboJpeg will refuse it
        var fallback = new RecordingDecoder(new WpfBitmapImageDecoder());
        var decoder = new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, fallback, DecoderBackend.Wpf);

        var decoded = decoder.Decode(new DecodeRequest(path, TargetWidth: 32));

        var request = Assert.Single(fallback.Requests);
        Assert.True(request.Bytes.HasValue, "fallback re-read the file instead of using TurboJpeg's bytes");
        Assert.Equal(fileBytes, request.Bytes!.Value.ToArray());
        Assert.Equal(DecoderBackend.Wpf, decoded.ActualBackend);
        Assert.Equal(32, decoded.PixelWidth);
    }

    [Fact(DisplayName = "Bytes the caller supplied reach the fallback unchanged")]
    public void CallerBytes_ReachFallbackUnchanged()
    {
        var path = FixtureGenerator.GenerateJpegWithIcc(Path.Combine(_root.Path, "icc-mem.jpg"), 64, 48);
        ReadOnlyMemory<byte> callerBytes = File.ReadAllBytes(path);
        var fallback = new RecordingDecoder(new WpfBitmapImageDecoder());
        var decoder = new FallbackImageDecoder(new TurboJpegDecoder(), DecoderBackend.TurboJpeg, fallback, DecoderBackend.Wpf);

        decoder.Decode(new DecodeRequest(path, TargetWidth: 0, Bytes: callerBytes));

        var request = Assert.Single(fallback.Requests);
        Assert.True(request.Bytes!.Value.Span.SequenceEqual(callerBytes.Span));
    }

    private sealed class RecordingDecoder(IImageDecoder inner) : IImageDecoder
    {
        public List<DecodeRequest> Requests { get; } = [];

        public IDecodedImage Decode(DecodeRequest request)
        {
            Requests.Add(request);
            return inner.Decode(request);
        }

        public ImageInfo ReadInfo(string path) => inner.ReadInfo(path);
    }
}
