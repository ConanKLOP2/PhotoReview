using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Metadata;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RawExif block merging pinned per field: the first block that has a value wins, a later block fills exactly the fields
/// that are missing, reading stops once every field is known, and out-of-range blocks are skipped or clamped.
/// </summary>
public sealed class RawExifMergeGapTests
{
    private static RawContainerInfo Info(params ExifBlock[] blocks) =>
        new(RawFormat.Cr3, 100, 100, 1, [], blocks);

    private static byte[] FullCameraTiff(Func<ushort, bool> keep)
    {
        var (ifd0, exif) = ExifTestData.FullCamera(little: true);
        return ExifTestData.Tiff(true, [.. ifd0.Where(e => keep(e.Tag))], [.. exif.Where(e => keep(e.Tag))]);
    }

    private static (InMemoryRawHeaderSource Source, RawContainerInfo Info) Blocks(byte[] first, byte[] second, bool secondIsExifIfd = false)
    {
        var source = new InMemoryRawHeaderSource([.. first, .. second]);
        return (source, Info(new ExifBlock(0, first.Length, true), new ExifBlock(first.Length, second.Length, true, secondIsExifIfd)));
    }

    public static TheoryData<string> Fields() =>
        ["date", "make", "model", "lens", "iso", "focal", "fnumber", "exposure"];

    private static ushort[] TagsOf(string field) => field switch
    {
        "date" => [ExifParser.TagDateTime, ExifParser.TagDateTimeOriginal],
        "make" => [ExifParser.TagMake],
        "model" => [ExifParser.TagModel],
        "lens" => [ExifParser.TagLensModel],
        "iso" => [ExifParser.TagIso],
        "focal" => [ExifParser.TagFocalLength],
        "fnumber" => [ExifParser.TagFNumber],
        "exposure" => [ExifParser.TagExposureTime],
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void TryReadExif_FieldMissingFromTheFirstBlock_IsTakenFromTheSecond(string field)
    {
        var tags = TagsOf(field);
        var (source, info) = Blocks(FullCameraTiff(t => !tags.Contains(t)), FullCameraTiff(tags.Contains));

        ExifTestData.AssertFullCamera(RawExif.TryReadExif(source, info, out var complete));

        Assert.True(complete);
    }

    [Fact]
    public void TryReadExif_BothBlocksHaveEveryFieldButOneMissing_FirstBlockWinsEveryFieldItHas()
    {
        // The first block lacks only the focal length (so the second is read); the second disagrees on everything.
        var other = ExifTestData.Tiff(true,
            [ExifTestData.Ascii(ExifParser.TagMake, "Nikon"), ExifTestData.Ascii(ExifParser.TagModel, "D850"), ExifTestData.Ascii(ExifParser.TagDateTime, "2000:01:01 00:00:00")],
            [
                ExifTestData.Rational(ExifParser.TagExposureTime, 1, 1000, true),
                ExifTestData.Rational(ExifParser.TagFNumber, 56, 10, true),
                ExifTestData.Short(ExifParser.TagIso, 100, true),
                ExifTestData.Ascii(ExifParser.TagDateTimeOriginal, "2000:01:02 00:00:00"),
                ExifTestData.Rational(ExifParser.TagFocalLength, 85, 1, true),
                ExifTestData.Ascii(ExifParser.TagLensModel, "other lens"),
            ]);
        var (source, info) = Blocks(FullCameraTiff(t => t != ExifParser.TagFocalLength), other);

        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("Canon", exif.CameraMake);
        Assert.Equal("Canon EOS R5", exif.CameraModel);
        Assert.Equal(new DateTime(2024, 5, 1, 14, 3, 22), exif.DateTaken);
        Assert.Equal("RF24-70mm F2.8 L IS USM", exif.LensModel);
        Assert.Equal(400, exif.Iso);
        Assert.Equal(new ExifRational(28, 10), exif.FNumber);
        Assert.Equal(new ExifRational(1, 250), exif.ExposureTime);
        Assert.Equal(new ExifRational(85, 1), exif.FocalLength);
    }

    [Fact]
    public void TryReadExif_FirstBlockAlreadyHasEveryField_LaterBlocksAreNotRead()
    {
        // A later Exif-IFD block would normally replace the date: it must not even be looked at once the summary is complete.
        var later = ExifTestData.Tiff(true, [ExifTestData.Ascii(ExifParser.TagDateTimeOriginal, "2000:01:01 00:00:00")], []);
        var (source, info) = Blocks(FullCameraTiff(_ => true), later, secondIsExifIfd: true);

        ExifTestData.AssertFullCamera(RawExif.TryReadExif(source, info));
    }

    [Fact]
    public void TryReadExif_BlockStartingPastTheEndOfTheFile_IsSkipped()
    {
        var tiff = FullCameraTiff(_ => true);
        var source = new InMemoryRawHeaderSource(tiff);

        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(tiff.Length + 10, 64, true), new ExifBlock(0, tiff.Length, true)), out var complete);

        ExifTestData.AssertFullCamera(exif);
        Assert.True(complete);
    }

    [Fact]
    public void TryReadExif_BlockWithNegativeOffset_IsSkipped()
    {
        var tiff = FullCameraTiff(_ => true);
        var source = new InMemoryRawHeaderSource(tiff);

        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(-5, 64, true), new ExifBlock(0, tiff.Length, true)), out var complete);

        ExifTestData.AssertFullCamera(exif);
        Assert.True(complete);
    }

    [Fact]
    public void TryReadExif_BlockClaimingToExtendPastTheEndOfTheFile_IsReadUpToTheEnd()
    {
        var tiff = FullCameraTiff(_ => true);
        var source = new InMemoryRawHeaderSource(tiff);

        var exif = RawExif.TryReadExif(source, Info(new ExifBlock(0, tiff.Length + 1000, true)), out var complete);

        ExifTestData.AssertFullCamera(exif);
        Assert.True(complete);
    }

    [Fact]
    public void TryReadExif_ZeroLengthBlock_IsSkippedAndNothingIsReported()
    {
        var tiff = FullCameraTiff(_ => true);
        var source = new InMemoryRawHeaderSource(tiff);

        Assert.Null(RawExif.TryReadExif(source, Info(new ExifBlock(0, 0, true)), out var complete));
        Assert.True(complete);
    }

    [Fact]
    public void TryReadExif_BothBlocksHaveTheFocalLength_FirstBlockWins()
    {
        var other = ExifTestData.Tiff(true, [], [ExifTestData.Rational(ExifParser.TagFocalLength, 85, 1, true)]);
        var (source, info) = Blocks(FullCameraTiff(t => t != ExifParser.TagLensModel), other);

        Assert.Equal(new ExifRational(50, 1), RawExif.TryReadExif(source, info)!.FocalLength);
    }

    [Fact]
    public void TryReadExif_SecondBlockClaimingToExtendPastTheEndOfTheFile_IsReadUpToTheEnd()
    {
        var first = FullCameraTiff(t => t != ExifParser.TagLensModel);
        var second = ExifTestData.Tiff(true, [], [ExifTestData.Ascii(ExifParser.TagLensModel, "Lens X")]);
        var source = new InMemoryRawHeaderSource([.. first, .. second]);
        var info = Info(new ExifBlock(0, first.Length, true), new ExifBlock(first.Length, second.Length + 1000, true));

        var exif = RawExif.TryReadExif(source, info, out var complete);

        Assert.Equal("Lens X", exif!.LensModel);
        Assert.True(complete);
    }
}