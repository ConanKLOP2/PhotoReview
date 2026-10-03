using PhotoReview.Imaging.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// DNG DefaultCropSize is honoured only when it fits the raw IFD (same rule as ARW/NEF) and DefaultScale is 1:1
/// (a non-square scale falls back to the raw IFD size).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DngCropSizeTests
{
    private const int RawWidth = 4032;
    private const int RawHeight = 3024;

    private static (int Width, int Height) SensorSize(uint cropWidth, uint cropHeight, (uint HNum, uint HDen, uint VNum, uint VDen)? scale = null)
    {
        var entries = new List<Entry>
        {
            Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0106, 32803),
            new Entry(0xC620, 4, 2, 200), // two LONGs at 200
        };
        if (scale is not null) entries.Add(At(0xC61E, 5, 2, 300));

        var file = new TiffBytes(true, 500).Header(8).Ifd(8, 0, [.. entries]).U32(200, cropWidth).U32(204, cropHeight);
        if (scale is { } s) file.U32(300, s.HNum).U32(304, s.HDen).U32(308, s.VNum).U32(312, s.VDen);

        var info = new DngContainerReader().Read(new InMemoryRawHeaderSource(file.ToArray()), CancellationToken.None);
        return (info.SensorWidth, info.SensorHeight);
    }

    [Fact]
    public void DefaultCropSize_SmallerThanRawIfd_IsUsed() =>
        Assert.Equal((4000, 3000), SensorSize(4000, 3000));

    [Theory]
    [InlineData(5000u, 3000u)]
    [InlineData(4000u, 3100u)]
    [InlineData(9000u, 9000u)]
    public void DefaultCropSize_LargerThanRawIfd_IsIgnored(uint cropWidth, uint cropHeight) =>
        Assert.Equal((RawWidth, RawHeight), SensorSize(cropWidth, cropHeight));

    [Fact]
    public void DefaultScale_OneToOne_KeepsTheCrop() =>
        Assert.Equal((4000, 3000), SensorSize(4000, 3000, (1, 1, 2, 2)));

    [Theory]
    [InlineData(2u, 1u, 1u, 1u)] // wide pixels
    [InlineData(1u, 1u, 3u, 2u)] // tall pixels
    [InlineData(1u, 0u, 1u, 1u)] // zero denominator
    public void DefaultScale_NotSquare_FallsBackToRawIfdSize(uint hNum, uint hDen, uint vNum, uint vDen) =>
        Assert.Equal((RawWidth, RawHeight), SensorSize(4000, 3000, (hNum, hDen, vNum, vDen)));
}
