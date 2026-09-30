using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>A busy (queue-full) refusal is transient: the fallback must not decode the same file outside the gate that refused it.</summary>
public sealed class DecoderBusyFallbackTests
{
    [Fact]
    public void Decode_PrimaryThrowsDecoderBusy_PropagatesWithoutUsingTheFallbackOrCountingAFallback()
    {
        var fallback = new CountingDecoder();
        var metrics = new ReviewMetrics();
        var log = new WarnCountingLog();
        var decoder = new FallbackImageDecoder(new BusyDecoder(), DecoderBackend.LibRaw, fallback, DecoderBackend.Wpf, log, metrics);

        Assert.Throws<DecoderBusyException>(() => decoder.Decode(new DecodeRequest(@"C:\raw\a.cr2", TargetWidth: 0)));

        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0, log.Warnings);
        Assert.Equal(0, metrics.Snapshot().DecoderFallbackCount);
    }

    [Fact]
    public void ReadInfo_PrimaryThrowsDecoderBusy_PropagatesWithoutUsingTheFallback()
    {
        var fallback = new CountingDecoder();
        var decoder = new FallbackImageDecoder(new BusyDecoder(), DecoderBackend.LibRaw, fallback, DecoderBackend.Wpf);

        Assert.Throws<DecoderBusyException>(() => decoder.ReadInfo(@"C:\raw\a.cr2"));

        Assert.Equal(0, fallback.Calls);
    }

    private sealed class BusyDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => throw new DecoderBusyException();
        public ImageInfo ReadInfo(string path) => throw new DecoderBusyException();
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        public int Calls { get; private set; }
        public IDecodedImage Decode(DecodeRequest request) { Calls++; throw new InvalidOperationException("fallback must not run"); }
        public ImageInfo ReadInfo(string path) { Calls++; throw new InvalidOperationException("fallback must not run"); }
    }

    private sealed class WarnCountingLog : ILog
    {
        public int Warnings { get; private set; }
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings++;
        public void Error(string message, Exception? ex = null) { }
    }
}
