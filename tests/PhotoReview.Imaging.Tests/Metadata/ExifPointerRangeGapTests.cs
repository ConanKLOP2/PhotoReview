using PhotoReview.Imaging.Metadata;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>Pins which Exif-IFD pointer value <see cref="ExifParser"/> accepts: the first in-range one wins, an out-of-range one is skipped.</summary>
public sealed class ExifPointerRangeGapTests
{
    private const uint ExifIfdAt = 38; // 8 + (2 + 2 * 12 + 4)

    private static byte[] Block(uint firstPointer, uint secondPointer)
    {
        var tiff = new Raw.Tiff.TiffBytes(true, 80).Header(8);
        tiff.Ifd(8, 0, Long(0x8769, firstPointer), Long(0x8769, secondPointer));
        tiff.Ifd((int)ExifIfdAt, 0, Short(0x8827, 400));
        return tiff.ToArray();
    }

    [Fact]
    public void TooSmallFirstPointerIsSkippedAndTheNextValidPointerIsUsed()
    {
        var summary = ExifParser.TryParseTiffBlock(Block(4, ExifIfdAt));

        Assert.Equal(400, summary?.Iso);
    }

    [Fact]
    public void FirstPointerWithinRangeWinsEvenWhenItPointsNowhere()
    {
        // 0xFFFFFFFF is the largest accepted pointer value; it is taken as the pointer (and then fails to resolve),
        // so the later valid pointer is not consulted.
        Assert.Null(ExifParser.TryParseTiffBlock(Block(uint.MaxValue, ExifIfdAt)));
    }

    [Fact]
    public void FirstValidPointerWinsOverALaterOne()
    {
        Assert.Equal(400, ExifParser.TryParseTiffBlock(Block(ExifIfdAt, 4))?.Iso);
    }
}
