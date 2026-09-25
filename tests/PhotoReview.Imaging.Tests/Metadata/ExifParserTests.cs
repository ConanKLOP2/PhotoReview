using PhotoReview.Imaging.Metadata;
using static PhotoReview.Imaging.Tests.Metadata.ExifTestData;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>The managed EXIF reader TurboJpeg uses, on hand-built byte arrays: both byte orders, gaps, and hostile input.</summary>
[Trait("Category", "HotPath")]
public sealed class ExifParserTests
{
    [Theory(DisplayName = "Reads every photo-information field in both byte orders")]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsAllFields(bool little)
    {
        var (ifd0, exif) = FullCamera(little);
        var jpeg = JpegWithApp1(Tiff(little, ifd0, exif), app0: "JFIF\0\u0001\u0001"u8.ToArray());

        AssertFullCamera(ExifParser.TryParseJpeg(jpeg));
    }

    [Fact(DisplayName = "Missing tags are skipped; the rest is still read")]
    public void MissingTagsAreSkipped()
    {
        var jpeg = JpegWithApp1(Tiff(true, [Ascii(ExifParser.TagModel, "ILCE-7M3")], [Short(ExifParser.TagIso, 100, true)]));

        var summary = ExifParser.TryParseJpeg(jpeg);

        Assert.NotNull(summary);
        Assert.Equal("ILCE-7M3", summary.CameraModel);
        Assert.Equal(100, summary.Iso);
        Assert.Null(summary.CameraMake);
        Assert.Null(summary.LensModel);
        Assert.Null(summary.DateTaken);
        Assert.Null(summary.FNumber);
        Assert.Null(summary.ExposureTime);
        Assert.Null(summary.FocalLength);
    }

    [Fact(DisplayName = "Without DateTimeOriginal the IFD0 DateTime is used")]
    public void DateFallsBackToIfd0DateTime()
    {
        var jpeg = JpegWithApp1(Tiff(true, [Ascii(ExifParser.TagDateTime, "2023:12:24 18:30:00")], []));

        Assert.Equal(new DateTime(2023, 12, 24, 18, 30, 0), ExifParser.TryParseJpeg(jpeg)?.DateTaken);
    }

    [Fact(DisplayName = "No APP1 Exif segment, no JPEG, or an EXIF block with nothing usable gives null")]
    public void NothingUsableGivesNull()
    {
        Assert.Null(ExifParser.TryParseJpeg([]));
        Assert.Null(ExifParser.TryParseJpeg([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Null(ExifParser.TryParseJpeg([0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9]));
        Assert.Null(ExifParser.TryParseJpeg(JpegWithApp1(Tiff(true, [], []))));
        // Placeholder values cameras write when unset: empty strings, zero rationals, "0000:00:00" dates.
        Assert.Null(ExifParser.TryParseJpeg(JpegWithApp1(Tiff(true,
            [Ascii(ExifParser.TagMake, "   "), Ascii(ExifParser.TagDateTime, "0000:00:00 00:00:00")],
            [Rational(ExifParser.TagFNumber, 0, 0, true), Rational(ExifParser.TagExposureTime, 1, 0, true)]))));
    }

    [Fact(DisplayName = "An EXIF block after the image data (SOS) is not searched")]
    public void StopsAtStartOfScan()
    {
        var (ifd0, exif) = FullCamera(true);
        var output = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02 };
        AddSegment(output, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. Tiff(true, ifd0, exif)]);

        Assert.Null(ExifParser.TryParseJpeg([.. output]));
    }

    [Fact(DisplayName = "A huge IFD entry count is capped to the entries inside the block")]
    public void HugeEntryCountIsBounded()
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Nikon")], []);
        tiff[8] = 0xFF; tiff[9] = 0xFF; // IFD0 count = 65535, block holds one entry

        Assert.Equal("Nikon", ExifParser.TryParseJpeg(JpegWithApp1(tiff))?.CameraMake);
    }

    [Fact(DisplayName = "An entry whose value offset or count runs past the block is skipped, not read out of bounds")]
    public void OutOfRangeValueIsSkipped()
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagModel, "EOS R6 Mark II"), Ascii(ExifParser.TagMake, "Canon")], []);
        // First entry (Model): count = 0xFFFFFFFF, offset = 0x7FFFFFF0.
        Array.Copy(U32(0xFFFFFFFF, true), 0, tiff, 8 + 2 + 4, 4);
        Array.Copy(U32(0x7FFFFFF0, true), 0, tiff, 8 + 2 + 8, 4);

        var summary = ExifParser.TryParseJpeg(JpegWithApp1(tiff));

        Assert.NotNull(summary);
        Assert.Null(summary.CameraModel);
        Assert.Equal("Canon", summary.CameraMake);
    }

    [Theory(DisplayName = "An Exif-IFD pointer to IFD0, the header or past the end is not followed")]
    [InlineData(8u)]
    [InlineData(0u)]
    [InlineData(0xFFFFFFF0u)]
    public void BadExifPointerIsIgnored(uint exifIfdOffset)
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Canon")], [Short(ExifParser.TagIso, 800, true)]);
        // IFD0 entry #2 is the Exif pointer (LONG, inline value at +8).
        Array.Copy(U32(exifIfdOffset, true), 0, tiff, 8 + 2 + 12 + 8, 4);

        var summary = ExifParser.TryParseJpeg(JpegWithApp1(tiff));

        Assert.Equal("Canon", summary?.CameraMake);
        Assert.Null(summary?.Iso);
    }

    [Fact(DisplayName = "Every truncation of a valid JPEG parses without throwing")]
    public void TruncatedInputNeverThrows()
    {
        var (ifd0, exif) = FullCamera(false);
        var jpeg = JpegWithApp1(Tiff(false, ifd0, exif));

        for (var length = 0; length < jpeg.Length; length++)
            _ = ExifParser.TryParseJpeg(jpeg.AsSpan(0, length));
        AssertFullCamera(ExifParser.TryParseJpeg(jpeg));
    }

    [Fact(DisplayName = "Corrupted EXIF bytes (deterministic fuzz) never throw out of the parser")]
    public void CorruptedInputNeverThrows()
    {
        var (ifd0, exif) = FullCamera(true);
        var original = JpegWithApp1(Tiff(true, ifd0, exif));
        var random = new Random(20240501);

        for (var round = 0; round < 3000; round++)
        {
            var mutated = (byte[])original.Clone();
            var flips = 1 + random.Next(8);
            for (var i = 0; i < flips; i++)
                mutated[4 + random.Next(mutated.Length - 4)] = (byte)random.Next(256);
            var summary = ExifParser.TryParseJpeg(mutated);
            if (summary is not null)
            {
                Assert.False(summary.IsEmpty);
                Assert.True((summary.CameraModel?.Length ?? 0) <= ExifSummary.MaxTextLength);
            }
        }
    }

    [Fact(DisplayName = "Long, NUL-padded and control-character text is cleaned and capped")]
    public void TextIsSanitised()
    {
        var jpeg = JpegWithApp1(Tiff(true,
            [Ascii(ExifParser.TagMake, "  Canon\t\u0001  "), Ascii(ExifParser.TagModel, new string('X', 300))], []));

        var summary = ExifParser.TryParseJpeg(jpeg);

        Assert.Equal("Canon", summary?.CameraMake);
        Assert.Equal(new string('X', ExifSummary.MaxTextLength), summary?.CameraModel);
    }

    [Fact(DisplayName = "A signed rational (SRATIONAL) is accepted when positive")]
    public void SignedRationalIsAccepted()
    {
        var entry = Rational(ExifParser.TagFocalLength, 35, 1, little: true) with { Type = 10 };
        var summary = ExifParser.TryParseJpeg(JpegWithApp1(Tiff(true, [], [entry])));

        Assert.Equal(new ExifRational(35, 1), summary?.FocalLength);
    }
}
