using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// The raw IFD ImageWidth/ImageLength of ARW and NEF include masked sensor margins, while CR2/DNG/RW2 report the
/// active area. ARW and NEF must prefer DefaultCropSize, then the Exif pixel size, before the raw IFD size (real
/// numbers: Sony A7M3 raw IFD 6048x4024 vs active 6000x4000; Nikon Z 7 raw IFD 8288x5520 vs JpgFromRaw 8256x5504).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffSensorSizeTests
{
    private const int RawWidth = 6048;
    private const int RawHeight = 4024;

    private static RawContainerInfo Read(IRawContainerReader reader, byte[] data) =>
        reader.Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    public enum Crop { None, Shorts, Longs, Rationals }

    /// <summary>ARW/NEF-shaped file: IFD0 -> SubIFD (raw IFD with optional crop) and an optional Exif IFD.</summary>
    private static byte[] BuildRawTiff(Crop crop, int cropWidth = 6000, int cropHeight = 4000, int? exifWidth = null, int exifHeight = 0)
    {
        var rawEntries = new List<Entry>
        {
            Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0103, 32767),
        };
        switch (crop)
        {
            case Crop.Shorts: rawEntries.Add(new Entry(0xC620, 3, 2, (uint)cropWidth | ((uint)cropHeight << 16))); break; // two SHORTs fit inline
            case Crop.Longs: rawEntries.Add(At(0xC620, 4, 2, 400)); break;
            case Crop.Rationals: rawEntries.Add(At(0xC620, 5, 2, 400)); break;
        }

        var ifd0 = new List<Entry> { Long(0x014A, 100) };
        if (exifWidth is not null) ifd0.Add(Long(0x8769, 600));

        var file = new TiffBytes(true, 1000).Header(8)
            .Ifd(8, 0, [.. ifd0])
            .Ifd(100, 0, [.. rawEntries]);
        switch (crop)
        {
            case Crop.Longs: file.U32(400, (uint)cropWidth).U32(404, (uint)cropHeight); break;
            case Crop.Rationals: file.U32(400, (uint)cropWidth).U32(404, 1).U32(408, (uint)cropHeight).U32(412, 1); break;
        }

        if (exifWidth is { } w)
            file.Ifd(600, 0, Long(0xA002, (uint)w), Long(0xA003, (uint)exifHeight));
        return file.ToArray();
    }

    [Theory]
    [InlineData(Crop.Shorts)]
    [InlineData(Crop.Longs)]
    [InlineData(Crop.Rationals)]
    public void ArwReader_DefaultCropSize_BeatsRawIfdSizeAndExifSize(Crop crop)
    {
        var data = BuildRawTiff(crop, 6000, 4000, exifWidth: 5999, exifHeight: 3999);

        var info = Read(new ArwContainerReader(), data);

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ArwReader_NoCropSize_UsesExifPixelDimensions()
    {
        var data = BuildRawTiff(Crop.None, exifWidth: 6000, exifHeight: 4000);

        var info = Read(new ArwContainerReader(), data);

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ArwReader_NoCropSizeNoExif_FallsBackToRawIfdSize()
    {
        var info = Read(new ArwContainerReader(), BuildRawTiff(Crop.None));

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ArwReader_CropAndExifLargerThanRawIfd_AreIgnored()
    {
        var data = BuildRawTiff(Crop.Longs, 7000, 5000, exifWidth: 7000, exifHeight: 5000);

        var info = Read(new ArwContainerReader(), data);

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(Crop.Shorts)]
    [InlineData(Crop.Longs)]
    [InlineData(Crop.Rationals)]
    public void NefReader_DefaultCropSize_BeatsRawIfdSize(Crop crop)
    {
        var info = Read(new NefContainerReader(), BuildRawTiff(crop, 6000, 4000));

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void NefReader_NoCropSize_UsesExifPixelDimensions()
    {
        var data = BuildRawTiff(Crop.None, exifWidth: 6000, exifHeight: 4000);

        var info = Read(new NefContainerReader(), data);

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    /// <summary>NEF with a raw SubIFD of 6048x4024 and a JpgFromRaw SubIFD (0x0201/0x0202 only, no size tags) of the given frame.</summary>
    private static byte[] BuildNefWithJpegFromRaw(int jpegWidth, int jpegHeight)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(jpegWidth, jpegHeight);
        return new TiffBytes(true, 700 + jpeg.Length).Header(8)
            .Ifd(8, 0, At(0x014A, 4, 2, 300))
            .U32(300, 100).U32(304, 200)
            .Ifd(100, 0, Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0103, 34713))
            .Ifd(200, 0, Long(0x00FE, 1), Long(0x0201, 700), Long(0x0202, (uint)jpeg.Length))
            .Put(700, jpeg)
            .ToArray();
    }

    [Fact]
    public void NefReader_NoCropNoExif_UsesFullSizeJpegFromRawFrameWithinMaskedMargin()
    {
        var info = Read(new NefContainerReader(), BuildNefWithJpegFromRaw(6016, 4016));

        Assert.Equal((6016, 4016), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData(1616, 1080)] // reduced preview: far smaller than the raw image
    [InlineData(6300, 4200)] // larger than the raw IFD: cannot be its active area
    [InlineData(5700, 4000)] // more than 256 px of margin on one axis
    public void NefReader_JpegFromRawUnrelatedToRawSize_KeepsRawIfdSize(int jpegWidth, int jpegHeight)
    {
        var info = Read(new NefContainerReader(), BuildNefWithJpegFromRaw(jpegWidth, jpegHeight));

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void ChooseActiveSensorSize_PrefersCropThenExifThenRaw()
    {
        Assert.Equal((10, 20), TiffHeaderNavigator.ChooseActiveSensorSize(100, 200, 10, 20, 30, 40));
        Assert.Equal((30, 40), TiffHeaderNavigator.ChooseActiveSensorSize(100, 200, 0, 0, 30, 40));
        Assert.Equal((100, 200), TiffHeaderNavigator.ChooseActiveSensorSize(100, 200, 0, 0, 0, 0));
        Assert.Equal((100, 200), TiffHeaderNavigator.ChooseActiveSensorSize(100, 200, 10, 0, 300, 40));
    }
}
