using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Decoding.Wic;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Q-FMT-WEBP-HEIC codec probe, through its seams (no real WIC/Media Foundation enumeration).</summary>
[Trait("Category", "HotPath")]
public sealed class WicCodecAvailabilityTests
{
    private static readonly Guid Jpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    private static WicCodecAvailability.DecoderEntry Entry(Guid container) => new(container, "name", ".ext");

    [Fact]
    public void Probe_WebpDecoderRegistered_ReportsWebpOnly()
    {
        var result = WicCodecAvailability.Probe(() => [Entry(Jpeg), Entry(WicCodecAvailability.ContainerFormatWebp)], _ => true);

        Assert.True(result.WebP);
        Assert.False(result.HeifContainer);
        Assert.False(result.Heif); // an HEVC decoder alone is not a HEIF codec
        Assert.True(result.Supports(WicImageFormat.WebP));
        Assert.False(result.Supports(WicImageFormat.Heif));
    }

    [Fact]
    public void Probe_HeifDecoderWithoutHevc_IsNotEnoughForHeic()
    {
        Guid? askedFor = null;
        var result = WicCodecAvailability.Probe(() => [Entry(WicCodecAvailability.ContainerFormatHeif)], subtype => { askedFor = subtype; return false; });

        Assert.True(result.HeifContainer);
        Assert.False(result.HevcDecoder);
        Assert.False(result.Heif);
        Assert.False(result.WebP);
        Assert.Equal(WicCodecAvailability.MediaSubtypeHevc, askedFor);
        Assert.Contains("HEVC video decoder: not found", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_HeifDecoderAndHevc_SupportsHeic()
    {
        var result = WicCodecAvailability.Probe(() => [Entry(WicCodecAvailability.ContainerFormatHeif)], _ => true);

        Assert.True(result.Heif);
        Assert.True(result.Supports(WicImageFormat.Heif));
    }

    [Fact]
    public void Probe_EnumerationThrows_MeansNothingAvailable_NotACrash()
    {
        var result = WicCodecAvailability.Probe(() => throw new InvalidOperationException("no factory"), _ => true);

        Assert.False(result.WebP);
        Assert.False(result.HeifContainer);
        Assert.False(result.HevcDecoder);
        Assert.Contains("no factory", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_HevcProbeThrows_MeansNoHevc_ButKeepsWebp()
    {
        var result = WicCodecAvailability.Probe(
            () => [Entry(WicCodecAvailability.ContainerFormatWebp), Entry(WicCodecAvailability.ContainerFormatHeif)],
            _ => throw new DllNotFoundException("mfplat"));

        Assert.True(result.WebP);
        Assert.True(result.HeifContainer);
        Assert.False(result.Heif);
        Assert.Contains("mfplat", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Supports_NonOptionalFormat_IsAlwaysTrue()
    {
        Assert.True(WicCodecSupport.None("x").Supports(WicImageFormat.None));
    }

    [Fact]
    public void ContainerGuids_MatchWincodecH()
    {
        Assert.Equal(new Guid("e094b0e2-67f2-45b3-b0ea-115337ca7cf3"), WicCodecAvailability.ContainerFormatWebp);
        Assert.Equal(new Guid("e1e62521-6787-405b-a339-500715b5763f"), WicCodecAvailability.ContainerFormatHeif);
        Assert.Equal(new Guid("43564548-0000-0010-8000-00aa00389b71"), WicCodecAvailability.MediaSubtypeHevc);
    }
}
