using PhotoReview.Imaging.Raw;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class WicRawFullDecoderTests
{
    [Fact]
    public void IsCodecAvailable_CachesProbeResultPerFormat()
    {
        var calls = new Dictionary<RawFormat, int>();
        var decoder = new WicRawFullDecoder(format =>
        {
            calls[format] = calls.GetValueOrDefault(format) + 1;
            return format == RawFormat.Dng;
        });

        Assert.True(decoder.IsCodecAvailable(RawFormat.Dng));
        Assert.True(decoder.IsCodecAvailable(RawFormat.Dng));
        Assert.False(decoder.IsCodecAvailable(RawFormat.Cr3));
        Assert.False(decoder.IsCodecAvailable(RawFormat.Cr3));

        Assert.Equal(1, calls[RawFormat.Dng]);
        Assert.Equal(1, calls[RawFormat.Cr3]);
    }
}
