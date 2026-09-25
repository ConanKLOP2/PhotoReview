using PhotoReview.Imaging.Metadata;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>The binary EXIF block stored in preview disk-cache entries (v7).</summary>
[Trait("Category", "HotPath")]
public sealed class ExifSummaryCodecTests
{
    private static readonly ExifSummary Full = new()
    {
        DateTaken = new DateTime(2024, 5, 1, 14, 3, 22),
        CameraMake = "Canon",
        CameraModel = "Canon EOS R5",
        LensModel = "RF24-70mm F2.8 L IS USM",
        Iso = 400,
        FocalLength = new ExifRational(50, 1),
        FNumber = new ExifRational(28, 10),
        ExposureTime = new ExifRational(1, 250),
    };

    [Fact(DisplayName = "A full summary round-trips exactly")]
    public void FullRoundTrips() => Assert.Equal(Full, ExifSummaryCodec.Decode(ExifSummaryCodec.Encode(Full)));

    [Fact(DisplayName = "Partial summaries round-trip without inventing the missing fields")]
    public void PartialRoundTrips()
    {
        ExifSummary[] partials =
        [
            new() { DateTaken = new DateTime(2020, 1, 2, 3, 4, 5) },
            new() { LensModel = "Ống kính 50mm ƒ/1.8 😀" },
            new() { Iso = 12800, ExposureTime = new ExifRational(30, 1) },
            new() { CameraModel = "X100V", FNumber = new ExifRational(2, 1) },
        ];

        foreach (var partial in partials)
            Assert.Equal(partial, ExifSummaryCodec.Decode(ExifSummaryCodec.Encode(partial)));
    }

    [Fact(DisplayName = "No EXIF encodes to nothing and nothing decodes to null")]
    public void EmptyIsNothing()
    {
        Assert.Empty(ExifSummaryCodec.Encode(null));
        Assert.Null(ExifSummaryCodec.Decode([]));
    }

    [Fact(DisplayName = "Every truncated or over-long block, and an unknown version, decodes to null")]
    public void MalformedDecodesToNull()
    {
        var bytes = ExifSummaryCodec.Encode(Full);
        for (var length = 0; length < bytes.Length; length++)
            Assert.Null(ExifSummaryCodec.Decode(bytes.AsSpan(0, length)));

        Assert.Null(ExifSummaryCodec.Decode([.. bytes, 0]));
        var otherVersion = (byte[])bytes.Clone();
        otherVersion[0] = 99;
        Assert.Null(ExifSummaryCodec.Decode(otherVersion));
    }
}
