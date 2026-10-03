using System.IO;
using System.Text;
using PhotoReview.Imaging.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Some writers put IFD0 values and the Exif IFD after the pixel data. The TIFF readers used to declare a fixed
/// 128 KiB EXIF block, so such files produced no EXIF summary; the block is now sized from the IFD offsets found.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffExifBlockPlacementTests
{
    private const int MakeOffset = 300_000;
    private const int ExifIfdOffset = 200_100;

    public static TheoryData<string> Readers => new() { "arw", "cr2", "dng", "nef", "orf" };

    private static IRawContainerReader Create(string name) => name switch
    {
        "arw" => new ArwContainerReader(),
        "cr2" => new Cr2ContainerReader(),
        "dng" => new DngContainerReader(),
        "nef" => new NefContainerReader(),
        _ => new OrfContainerReader(),
    };

    /// <summary>IFD0 at 8 (dimensions, Make far away, Exif pointer); the Exif IFD (ISO) sits far away as well.</summary>
    private static byte[] Build(int size, int makeOffset, int exifOffset)
    {
        var tiff = new TiffBytes(littleEndian: true, size).Header(8);
        tiff.Ifd(8, 0,
            Short(0x0100, 4000), Short(0x0101, 3000),
            At(0x010F, 2, 6, (uint)makeOffset),
            Long(0x8769, (uint)exifOffset));
        tiff.Put(makeOffset, Encoding.ASCII.GetBytes("Acme\0\0"));
        tiff.Ifd(exifOffset, 0, Short(0x8827, 400));
        return tiff.ToArray();
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void TryReadExif_IfdsAfter128KiB_StillYieldsSummary(string reader)
    {
        var source = new InMemoryRawHeaderSource(Build(310_000, MakeOffset, ExifIfdOffset));

        var info = Create(reader).Read(source, CancellationToken.None);
        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("Acme", exif!.CameraMake);
        Assert.Equal(400, exif.Iso);
        Assert.All(info.ExifBlocks, b => Assert.Equal(0, b.Offset));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_IfdsInsideDefaultWindow_KeepsDefaultBlockLength(string reader)
    {
        var source = new InMemoryRawHeaderSource(Build(310_000, 2000, 3000));

        var info = Create(reader).Read(source, CancellationToken.None);

        Assert.All(info.ExifBlocks, b => Assert.Equal(RawContainerLimits.DefaultExifBlockBytes, b.Length));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_IfdsBeyondMaxBlock_CapsBlockAndDoesNotThrow(string reader)
    {
        int far = RawContainerLimits.MaxExifBlockBytes + 50_000;
        var source = new InMemoryRawHeaderSource(Build(far + 10_000, far, far + 100));

        var info = Create(reader).Read(source, CancellationToken.None);

        Assert.All(info.ExifBlocks, b => Assert.Equal(RawContainerLimits.DefaultExifBlockBytes, b.Length)); // an extent that cannot fit keeps the default block (was grown to the 4 MiB cap for nothing)
        Assert.Null(RawExif.TryReadExif(source, info)?.CameraMake);
    }

    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    public void CorpusTiffSamples_ExifSummaryEqualsFixed128KiBWindowParse()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var registry = new RawContainerReaderRegistry();
        int compared = 0;
        foreach (var file in Directory.GetFiles(CorpusDir))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".arw" or ".cr2" or ".dng" or ".nef" or ".orf")) continue;

            var bytes = File.ReadAllBytes(file);
            var reader = registry.FindReader(bytes.AsSpan(0, 64), ext);
            Assert.NotNull(reader);

            var info = reader.Read(new InMemoryRawHeaderSource(bytes), CancellationToken.None);
            var actual = RawExif.TryReadExif(new InMemoryRawHeaderSource(bytes), info);
            var legacy = PhotoReview.Imaging.Metadata.ExifParser.TryParseTiffBlock(
                bytes.AsSpan(0, Math.Min(bytes.Length, RawContainerLimits.DefaultExifBlockBytes)));

            Assert.Equal(legacy, actual);
            compared++;
        }

        Assert.True(compared >= 15, $"Expected the TIFF corpus files, compared {compared}");
    }
}
