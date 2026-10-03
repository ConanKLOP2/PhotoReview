using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// The size the RAW reader reports is what the viewer lays the image out at; the zoom decode (LibRaw) then swaps in a bitmap of ITS
/// size. For ORF the two must agree, otherwise the swap changes the image size under the user. IFD0 ImageWidth/ImageLength is the raw
/// frame, and LibRaw decodes it unchanged except for one hard-coded rule its identify.cpp applies to a 4080-wide frame (E-PM1, E-PL3,
/// E-P3: 24 columns are not image; the file has no tag for it). MakerNote ImageProcessing 0x0614/0x0615 (the camera image proper:
/// E-M1 4608x3456, OM-1 5184x3888, E-P3 4032x3024) is smaller than both, as the embedded 3200x2400 preview shows: it is an exact
/// isotropic downscale of that image.
/// </summary>
// Shares the collection of the other full-decoding LibRaw tests: the full-decode gate is process-wide.
[Collection(LibRawNativeDecodeGate.Name)]
public sealed class OrfDisplayedSizeTests
{
    private static byte[] BuildOrf(uint width, uint height, bool littleEndian = true) =>
        new TiffBytes(littleEndian, 200).Header(8, littleEndian ? "IIRO"u8 : "MMOR"u8)
            .Ifd(8, 0, Long(0x0100, width), Long(0x0101, height))
            .ToArray();

    private static RawContainerInfo ReadContainer(byte[] data) =>
        new OrfContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    [Theory]
    [Trait("Category", "HotPath")]
    [InlineData(4080u, 3040u, 4056, 3040)] // E-P3 / E-PL3 / E-PM1 raw frame: LibRaw drops the 24 masked columns
    [InlineData(4640u, 3472u, 4640, 3472)] // E-M1: no rule, IFD0 == LibRaw
    [InlineData(5220u, 3912u, 5220, 3912)] // OM-1: no rule, IFD0 == LibRaw
    [InlineData(4079u, 3040u, 4079, 3040)] // the rule is exact, not a range
    [InlineData(4081u, 3040u, 4081, 3040)]
    public void Read_Ifd0RawFrame_ReportsTheSizeLibRawDecodes(uint ifd0Width, uint ifd0Height, int width, int height)
    {
        foreach (var littleEndian in new[] { true, false })
        {
            var info = ReadContainer(BuildOrf(ifd0Width, ifd0Height, littleEndian));

            Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
        }
    }

    [Fact]
    [Trait("Category", "HotPath")]
    public void Read_HeightIsNeverTouchedByTheWidthRule() =>
        Assert.Equal(4080, ReadContainer(BuildOrf(4056, 4080)).SensorHeight);

    [Theory]
    [Trait("Category", "Native")]
    [InlineData("Olympus - E-P3 - 16bit (4_3).ORF", 4056, 3040)]
    [InlineData("Olympus - E-M1 - 16bit (4_3).orf", 4640, 3472)]
    [InlineData("OM System - OM-1 - 16bit (4_3).ORF", 5220, 3912)]
    public void CorpusOrf_ReaderSizeEqualsLibRawSize(string fileName, int width, int height)
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        var path = RawCorpus.TryGetFile(fileName);
        if (path is null) return;

        var reader = new RawDecoder(new LibRawDecoder()).ReadInfo(path);
        var librawInfo = new LibRawDecoder().ReadInfo(path);

        Assert.Equal((width, height), (reader.Width, reader.Height));
        Assert.Equal((width, height), (librawInfo.Width, librawInfo.Height));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void CorpusEp3_RealLibRawDecodeHasTheReaderSize()
    {
        if (!RawCorpus.RequireNative(LibRawAvailability.Probe(out var reason), reason)) return;
        var path = RawCorpus.TryGetFile("Olympus - E-P3 - 16bit (4_3).ORF");
        if (path is null) return;

        var reader = new RawDecoder(new LibRawDecoder()).ReadInfo(path);
        // A bounded box still reports the full decoded size; E-P3 is a 12 MP sensor, the lightest ORF of the corpus.
        var decoded = new LibRawDecoder().Decode(new PhotoReview.Imaging.Decoding.DecodeRequest(path, new DecodeBox(64, 64)));

        Assert.Equal((4056, 3040), (decoded.OriginalWidth, decoded.OriginalHeight));
        Assert.Equal((decoded.OriginalWidth, decoded.OriginalHeight), (reader.Width, reader.Height));
    }
}
