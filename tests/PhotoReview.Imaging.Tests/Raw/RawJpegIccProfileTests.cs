using System.IO;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// <see cref="RawJpegIccProfile.EnsureAdobeRgbProfile"/> on synthetic JPEG headers: the injected APP2 segment is byte exact,
/// an already tagged stream is returned as is, a non-JPEG is rejected, and malformed segment lengths in the existing-ICC scan
/// neither throw nor count as "already tagged".
/// </summary>
public sealed class RawJpegIccProfileTests
{
    private static readonly byte[] IccId = "ICC_PROFILE\0"u8.ToArray();

    private static byte[] WithSegments(params byte[][] segments) => [0xFF, 0xD8, .. segments.SelectMany(s => s), 0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22, 0xFF, 0xD9];

    private static byte[] Segment(byte marker, params byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static byte[] ExistingIccSegment() => Segment(0xE2, [.. IccId, 1, 1, 0xAA, 0xBB]);

    private static void AssertInjected(byte[] original, byte[] result)
    {
        var profile = BundledAdobeProfile.Load();
        var segmentLength = 16 + profile.Length;
        Assert.Equal(original.Length + 2 + segmentLength, result.Length);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE2, (byte)(segmentLength >> 8), (byte)segmentLength }, result[..6]);
        Assert.Equal(IccId, result[6..18]);
        Assert.Equal(new byte[] { 1, 1 }, result[18..20]); // sequence 1 of 1
        Assert.Equal(profile, result[20..(20 + profile.Length)]);
        Assert.Equal(original[2..], result[(20 + profile.Length)..]); // the rest of the JPEG follows untouched
    }

    [Fact]
    public void EnsureAdobeRgbProfile_UntaggedJpeg_InsertsOneExactApp2SegmentRightAfterSoi()
    {
        var jpeg = WithSegments(Segment(0xE0, "JFIF\0"u8.ToArray()));

        var result = RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg);

        AssertInjected(jpeg, result);
        Assert.Same(result, RawJpegIccProfile.EnsureAdobeRgbProfile(result)); // now tagged: a second call is a no-op
    }

    [Fact]
    public void EnsureAdobeRgbProfile_UntaggedJpeg_DoesNotMutateTheInput()
    {
        var jpeg = WithSegments(Segment(0xE0, "JFIF\0"u8.ToArray()));
        var snapshot = (byte[])jpeg.Clone();

        _ = RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg);

        Assert.Equal(snapshot, jpeg);
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccAfterOtherSegments_ReturnsTheSameInstance()
    {
        var jpeg = WithSegments(Segment(0xE0, "JFIF\0"u8.ToArray()), Segment(0xE1, "Exif\0\0"u8.ToArray()), Segment(0xDB, new byte[65]), ExistingIccSegment());

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_App2ThatIsNotIcc_StillGetsTheProfile()
    {
        var jpeg = WithSegments(Segment(0xE2, [.. "MPF\0"u8.ToArray(), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));

        AssertInjected(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccIdentifierInAnApp1Segment_IsNotCountedAsTagged()
    {
        var jpeg = WithSegments(Segment(0xE1, [.. IccId, 1, 1, 0xAA]));

        AssertInjected(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_IccAfterTheScanHeader_IsNotCountedAsTagged()
    {
        // The scan (SOS) ends the header area: an ICC identifier in later entropy-coded bytes is not an APP2 segment.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xE2, 0x00, 0x10, .. IccId, 1, 1, 0xFF, 0xD9];

        AssertInjected(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xD8 })] // SOI only: valid prefix, nothing to scan
    public void EnsureAdobeRgbProfile_VeryShortInput_NeverThrowsAnythingButInvalidData(byte[] jpeg)
    {
        if (jpeg.Length >= 2) AssertInjected(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
        else Assert.Throws<InvalidDataException>(() => RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })] // PNG
    [InlineData(new byte[] { 0xD8, 0xFF, 0xE0, 0x00 })] // swapped SOI
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 })]
    public void EnsureAdobeRgbProfile_NotAJpeg_ThrowsInvalidData(byte[] data)
    {
        Assert.Throws<InvalidDataException>(() => RawJpegIccProfile.EnsureAdobeRgbProfile(data));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => RawJpegIccProfile.EnsureAdobeRgbProfile(null!));
    }

    [Theory]
    [InlineData(0xE2, 0x4000)] // ICC-bearing APP2 whose length reaches far past the end of the buffer
    [InlineData(0xE2, 0x0001)] // length below the 2-byte minimum
    [InlineData(0xE2, 0x0000)]
    [InlineData(0xE1, 0xFFFF)]
    public void EnsureAdobeRgbProfile_TruncatedOrImpossibleSegmentLength_DoesNotThrowAndIsNotTagged(byte marker, int declaredLength)
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, marker, (byte)(declaredLength >> 8), (byte)declaredLength, .. IccId, 1, 1, 0xAA, 0xBB, 0xCC];

        var result = RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg);

        AssertInjected(jpeg, result);
    }

    [Fact]
    public void EnsureAdobeRgbProfile_SegmentHeaderCutAfterTheMarker_DoesNotThrowAndIsNotTagged()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE2, 0x00]; // the length field itself is cut in half

        AssertInjected(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }

    [Fact]
    public void EnsureAdobeRgbProfile_StandaloneMarkersAndFillBytesBeforeTheIccSegment_AreSkipped()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xFF, 0x01, 0xFF, 0xD0, .. ExistingIccSegment(), 0xFF, 0xD9];

        Assert.Same(jpeg, RawJpegIccProfile.EnsureAdobeRgbProfile(jpeg));
    }
}
