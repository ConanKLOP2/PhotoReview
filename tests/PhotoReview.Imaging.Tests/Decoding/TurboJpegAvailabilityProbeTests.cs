using System.Runtime.InteropServices;
using PhotoReview.Imaging.TurboJpeg.Native;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>RV-T52: availability probe caching and SafeTurboJpegHandle release behaviour (needs native\x64\turbojpeg.dll).</summary>
[Trait("Category", "Native")]
public sealed class TurboJpegAvailabilityProbeTests
{
    [Fact]
    public void Probe_WithTheNativeDllNextToTheTests_SucceedsWithNoReasonAndIsCached()
    {
        var first = TurboJpegAvailability.Probe(out var firstReason);
        var second = TurboJpegAvailability.Probe(out var secondReason);

        Assert.True(first, firstReason);
        Assert.Null(firstReason);
        Assert.Equal(first, second);
        Assert.Equal(firstReason, secondReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Dispose_InvalidHandleValue_DoesNotCrashAndClosesTheHandle(long value)
    {
        var handle = new SafeTurboJpegHandle(new IntPtr(value), ownsHandle: true);

        Assert.True(handle.IsInvalid);
        var exception = Record.Exception(handle.Dispose);

        Assert.Null(exception);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Dispose_RealDecompressorHandle_IsValidUntilDisposedAndDisposeTwiceIsHarmless()
    {
        var handle = TurboJpegNative.CreateDecompressor();

        Assert.False(handle.IsInvalid);
        Assert.False(handle.IsClosed);
        handle.Dispose();
        handle.Dispose();

        Assert.True(handle.IsClosed);
    }
}