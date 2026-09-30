using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// The DNG sensor size comes from the raw IFD only: reduced, mask and enhanced/super-resolution IFDs and IFDs without a
/// raw PhotometricInterpretation (CFA 32803 / LinearRaw 34892) must not win just because they are larger.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class DngPrimaryIfdTests
{
    private const int RawWidth = 4000;
    private const int RawHeight = 3000;

    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static RawContainerInfo Read(byte[] data) =>
        new DngContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    /// <summary>IFD0 = the CFA raw image (4000x3000); SubIFD = <paramref name="other"/> entries with a bigger size.</summary>
    private static byte[] Build(uint otherSubFileType, ushort otherPhotometric, bool otherHasSubFileType = true)
    {
        var otherEntries = new List<Entry>();
        if (otherHasSubFileType) otherEntries.Add(Long(0x00FE, otherSubFileType));
        otherEntries.AddRange([Short(0x0100, 6000), Short(0x0101, 4500), Short(0x0103, 1), Short(0x0106, otherPhotometric)]);

        return new TiffBytes(true, 500).Header(8)
            .Ifd(8, 0,
                Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0103, 7), Short(0x0106, 32803),
                Long(0x014A, 200))
            .Ifd(200, 0, [.. otherEntries])
            .ToArray();
    }

    [Theory]
    [InlineData(4u, (ushort)32803)]      // transparency mask
    [InlineData(8u, (ushort)32803)]
    [InlineData(0x10u, (ushort)34892)]   // DNG 1.6 enhanced / super-resolution
    [InlineData(1u, (ushort)32803)]      // reduced resolution (already handled)
    public void SecondaryImageIfd_LargerThanRawIfd_DoesNotWin(uint subFileType, ushort photometric)
    {
        var info = Read(Build(subFileType, photometric));

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void NonRawIfdWithoutSubFileType_LargerThanRawIfd_DoesNotWin()
    {
        var info = Read(Build(0, otherPhotometric: 2, otherHasSubFileType: false));

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void RawIfdOutranksLargerNonRawIfd_RegardlessOfOrder()
    {
        // The non-raw IFD comes first in the chain, the CFA IFD second.
        var data = new TiffBytes(true, 500).Header(8)
            .Ifd(8, 200, Long(0x00FE, 0), Short(0x0100, 6000), Short(0x0101, 4500), Short(0x0103, 1), Short(0x0106, 2))
            .Ifd(200, 0, Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0103, 7), Short(0x0106, 32803))
            .ToArray();

        var info = Read(data);

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void LargerRawIfd_StillWinsOverSmallerRawIfd()
    {
        var data = new TiffBytes(true, 500).Header(8)
            .Ifd(8, 200, Long(0x00FE, 0), Short(0x0100, 100), Short(0x0101, 80), Short(0x0106, 32803))
            .Ifd(200, 0, Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0106, 34892))
            .ToArray();

        var info = Read(data);

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData("Leica", 3916, 2634)]
    [InlineData("K-7", 4672, 3104)]
    [InlineData("iPhone", 4032, 3024)]
    public void Corpus_DngSizesAreUnchanged(string camera, int width, int height)
    {
        var file = Directory.Exists(CorpusDir)
            ? Directory.GetFiles(CorpusDir, "*.dng").FirstOrDefault(f => Path.GetFileName(f).Contains(camera, StringComparison.Ordinal))
            : null;
        if (file is null) return;

        using var fs = File.OpenRead(file);
        using var source = new SourceRawHeaderSource(fs);
        var info = new DngContainerReader().Read(source, CancellationToken.None);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
    }
}
