using System.IO;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Resampler edge shapes (one pixel wide or high sources, one and three channels, box-average and bilinear paths) with exact
/// expected pixels, so a source index past the row/column shows up as a wrong value or an exception.
/// </summary>
public sealed class RgbBgraResamplerEdgeShapeTests
{
    private static byte[] Resize(byte[] source, int sw, int sh, int tw, int th, int channels)
    {
        var bgra = new byte[tw * th * 4];
        RgbBgraResampler.Resize(source, sw, sh, bgra, tw, th, CancellationToken.None, channels);
        return bgra;
    }

    private static byte[] Gray(params byte[] values) => values.SelectMany(v => new byte[] { v, v, v, 255 }).ToArray();

    [Fact]
    public void Resize_MonochromeOnePixelWideColumn_BoxAveragesEachGroupOfRows()
    {
        // 1x8 -> 1x2: ratio 4 > 2 selects the box average; rows 0-3 average 25, rows 4-7 average 65.
        var actual = Resize([10, 20, 30, 40, 50, 60, 70, 80], 1, 8, 1, 2, channels: 1);

        Assert.Equal(Gray(25, 65), actual);
    }

    [Fact]
    public void Resize_MonochromeOnePixelHighRow_BoxAveragesEachGroupOfColumns()
    {
        var actual = Resize([10, 20, 30, 40, 50, 60, 70, 80], 8, 1, 2, 1, channels: 1);

        Assert.Equal(Gray(25, 65), actual);
    }

    [Fact]
    public void Resize_MonochromeOnePixelWideColumn_BilinearInterpolatesAlongTheColumnOnly()
    {
        // 1x4 -> 1x3 (ratio 1.33 < 2): source rows 0.1667, 1.5 and 2.8333 of [0,30,60,90] -> 5, 45, 85.
        var actual = Resize([0, 30, 60, 90], 1, 4, 1, 3, channels: 1);

        Assert.Equal(Gray(5, 45, 85), actual);
    }

    [Fact]
    public void Resize_MonochromeOnePixelHighRow_BilinearInterpolatesAlongTheRowOnly()
    {
        var actual = Resize([0, 30, 60, 90], 4, 1, 3, 1, channels: 1);

        Assert.Equal(Gray(5, 45, 85), actual);
    }

    [Fact]
    public void Resize_RgbOnePixelWideColumn_BoxAveragesWithoutReadingTheNextColumn()
    {
        // 1x4 RGB -> 1x1: one target pixel covers all four rows: mean of R, G, B = (10+20+30+40)/4 etc.
        byte[] rgb = [10, 100, 200, 20, 110, 190, 30, 120, 180, 40, 130, 170];

        var actual = Resize(rgb, 1, 4, 1, 1, channels: 3);

        Assert.Equal(new byte[] { 185, 115, 25, 255 }, actual); // BGRA = (B, G, R, A)
    }

    [Fact]
    public void Resize_RgbOnePixelHighRowUpscaledBilinear_KeepsEachPixelsOwnColour()
    {
        // 2x1 -> 4x1 (ratio 0.5): the outer target pixels sample the source ends exactly.
        byte[] rgb = [0, 0, 0, 200, 100, 50];

        var actual = Resize(rgb, 2, 1, 4, 1, channels: 3);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, actual[..4]);
        Assert.Equal(new byte[] { 50, 100, 200, 255 }, actual[12..]);
    }

    [Theory]
    [InlineData(1, 1, 3, 2, 1)]
    [InlineData(1, 1, 3, 2, 3)]
    [InlineData(1, 5, 4, 5, 1)] // width-1 source stretched sideways
    [InlineData(5, 1, 5, 4, 3)]
    [InlineData(1, 9, 1, 3, 3)]
    [InlineData(9, 1, 3, 1, 1)]
    public void Resize_UniformSourceOfDegenerateShape_YieldsAUniformImageForEveryChannelCount(int sw, int sh, int tw, int th, int channels)
    {
        var source = Enumerable.Repeat((byte)77, sw * sh * channels).ToArray();

        var actual = Resize(source, sw, sh, tw, th, channels);

        for (var i = 0; i < actual.Length; i += 4)
            Assert.Equal(new byte[] { 77, 77, 77, 255 }, actual[i..(i + 4)]);
    }
}

/// <summary>
/// Native LibRaw: a file LibRaw rejects must not stay locked after the call (no leaked file handle), so the caller can delete it.
/// </summary>
[Collection(LibRawNativeDecodeGate.Name)]
[Trait("Category", "Native")]
public sealed class LibRawCorruptFileHandleTests
{
    private static byte[] Junk()
    {
        var bytes = new byte[8192];
        new Random(1234).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void ReadInfo_CorruptFile_ThrowsInvalidDataAndReleasesTheFile()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        using var temp = new TempRoot("libraw-corrupt-info");
        var path = temp.File("junk.cr2", Junk());

        Assert.Throws<InvalidDataException>(() => new LibRawDecoder().ReadInfo(path));

        File.Delete(path); // throws IOException when LibRaw still holds the handle
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ReadJpegThumbnail_CorruptFile_ThrowsInvalidDataAndReleasesTheFile()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        using var temp = new TempRoot("libraw-corrupt-thumb");
        var path = temp.File("junk.nef", Junk());

        Assert.Throws<InvalidDataException>(() => LibRawDecoder.ReadJpegThumbnail(path));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
