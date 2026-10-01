using PhotoReview.Imaging.Metadata;
using static PhotoReview.Imaging.Tests.Metadata.ExifTestData;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>RV-T51: self-referencing Exif sub-IFD pointers and over-large entry counts on the Exif sub-IFD terminate with bounded work.</summary>
[Trait("Category", "HotPath")]
public sealed class ExifParserHostilePointerTests
{
    [Fact]
    public void TryParseJpeg_ExifPointerBackToIfd0_TerminatesAndKeepsIfd0Fields()
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Nikon")], [Short(ExifParser.TagIso, 100, true)]);
        // IFD0 = [Make, ExifPointer]; the pointer entry is the second one: its value field is at 8 + 2 + 12 + 8.
        BitConverter.GetBytes(8u).CopyTo(tiff, 8 + 2 + 12 + 8); // pointer -> IFD0 itself

        var summary = ExifParser.TryParseJpeg(JpegWithApp1(tiff));

        Assert.NotNull(summary);
        Assert.Equal("Nikon", summary!.CameraMake);
        Assert.Null(summary.Iso); // the ISO lived in the old sub-IFD, which is no longer referenced
    }

    [Fact]
    public void TryParseJpeg_ExifSubIfdThatContainsAPointerToItself_IsReadOnceWithoutLooping()
    {
        // The test builder writes the Exif-pointer value of ANY 0x8769 entry as the Exif IFD offset, so this entry
        // inside the Exif IFD points at the Exif IFD itself.
        var exif = new List<Entry> { new(ExifParser.TagExifIfd, 4, 1, new byte[4]), Short(ExifParser.TagIso, 640, true) };
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Sony")], exif);

        var summary = ExifParser.TryParseJpeg(JpegWithApp1(tiff));

        Assert.Equal("Sony", summary?.CameraMake);
        Assert.Equal(640, summary?.Iso);
    }

    [Fact]
    public void TryParseJpeg_ExifSubIfdWithCount65535OnASmallBlock_ReadsOnlyTheEntriesInsideTheBlock()
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Leica")], [Short(ExifParser.TagIso, 800, true)]);
        // Exif IFD starts right after IFD0 (8 + 2 + 2 * 12 + 4 ... IFD0 has Make + pointer = 2 entries).
        var exifIfdOffset = 8 + 2 + (2 * 12) + 4;
        tiff[exifIfdOffset] = 0xFF;
        tiff[exifIfdOffset + 1] = 0xFF;

        var summary = ExifParser.TryParseJpeg(JpegWithApp1(tiff));

        Assert.Equal("Leica", summary?.CameraMake);
        Assert.Equal(800, summary?.Iso);
    }

    [Fact]
    public void TryReadOrientationFromJpeg_Ifd0CountOf65535OnASmallBlock_StillFindsTheOrientationInsideTheBlock()
    {
        var tiff = Tiff(true, [Short(ExifParser.TagOrientation, 6, true)], []);
        tiff[8] = 0xFF;
        tiff[9] = 0xFF;

        Assert.Equal(6, ExifParser.TryReadOrientationFromJpeg(JpegWithApp1(tiff)));
    }

    [Fact]
    public void TryParseJpeg_EveryTruncationOfASelfPointingBlock_NeverThrows()
    {
        var tiff = Tiff(true, [Ascii(ExifParser.TagMake, "Nikon")], [Short(ExifParser.TagIso, 100, true)]);
        BitConverter.GetBytes(8u).CopyTo(tiff, 8 + 2 + 12 + 8);

        for (var length = 0; length <= tiff.Length; length++)
        {
            var truncated = tiff[..length];
            var exception = Record.Exception(() => ExifParser.TryParseTiffBlock(truncated));
            Assert.Null(exception);
        }
    }
}