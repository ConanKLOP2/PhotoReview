namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Mutation-testing gap tests for the JPEG marker walk of <c>RawJpegIccProfile.EnsureAdobeRgbProfile</c> (Stryker round 2):
/// an existing APP2 ICC profile must be found wherever a well-formed segment layout puts it (long segments, empty segments,
/// restart markers, a segment ending exactly at the end of the stream), because a miss would embed a second profile; and a stream
/// that ends inside the fill bytes or a marker must be handled without an out-of-range read.
/// </summary>
public sealed class RawJpegIccProfileMutationGapTests
{
    private static readonly byte[] IccIdentifier = "ICC_PROFILE\0"u8.ToArray();

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] IccSegment() => Segment(0xE2, [.. IccIdentifier, 1, 1, 0x11, 0x22]);

    private static byte[] Jpeg(params byte[][] parts) => [0xFF, 0xD8, .. parts.SelectMany(p => p)];

    private static readonly byte[] Eoi = [0xFF, 0xD9];

    [Fact]
    public void EnsureAdobeRgbProfile_IccAfterASegmentLongerThan255Bytes_IsRecognizedAndLeftAlone()
    {
        var jpeg = Jpeg(Segment(0xE1, new byte[298]), IccSegment(), Eoi); // APP1 length field 0x012C: high byte matters

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccAfterARestart7Marker_IsRecognizedAndLeftAlone()
    {
        var jpeg = Jpeg([0xFF, 0xD7], IccSegment(), Eoi);

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccAfterAnEmptySegment_IsRecognizedAndLeftAlone()
    {
        var jpeg = Jpeg(Segment(0xE1, []), IccSegment(), Eoi); // length field 2: a segment with no payload

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccSegmentEndingExactlyAtTheEndOfTheStream_IsRecognizedAndLeftAlone()
    {
        var jpeg = Jpeg(IccSegment());

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccSegmentWithOnlyTheIdentifier_CountsAsPresent()
    {
        var jpeg = Jpeg(Segment(0xE2, IccIdentifier), Eoi); // payload is exactly the 12-byte identifier

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xFF })] // fill bytes run to the very end
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xE1 })] // the stream ends right after a marker byte
    public void EnsureAdobeRgbProfile_StreamEndingInsideFillBytesOrAMarker_AddsTheProfileWithoutReadingPastTheEnd(byte[] jpeg)
    {
        var result = RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg);

        var profile = RawJpegIccProfile.GetBundledAdobeRgbProfile();
        Assert.Equal(jpeg.Length + 4 + 12 + 2 + profile.Length, result.Length);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE2], result[..4]);
        Assert.Equal(jpeg[2..], result[(20 + profile.Length)..]); // the original stream after its SOI follows the new APP2
    }
}
