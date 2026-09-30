using System.IO;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Olympus ORF stores 'RO'/'SR' (0x4F52 / 0x5352, "IIRO"/"IIRS"/"MMOR") where TIFF has 42, so the EXIF parser used to
/// reject every ORF and the RAW pipeline returned no EXIF for Olympus/OM System files. Other formats stay strict.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class OrfExifMagicTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static readonly byte[] Iiro = [(byte)'I', (byte)'I', 0x52, 0x4F];
    private static readonly byte[] Iirs = [(byte)'I', (byte)'I', 0x52, 0x53];
    private static readonly byte[] Mmor = [(byte)'M', (byte)'M', 0x4F, 0x52];

    /// <summary>Whole-file TIFF-shaped block with IFD0 { Make = "OLYMPUS", Model = "E-M1" } and the given signature.</summary>
    private static byte[] BuildOrf(bool littleEndian, byte[] signature)
    {
        var file = new TiffBytes(littleEndian, 200).Header(8, signature)
            .Ifd(8, 0, At(0x010F, 2, 8, 100), At(0x0110, 2, 5, 120))
            .Put(100, "OLYMPUS\0"u8)
            .Put(120, "E-M1\0"u8);
        return file.ToArray();
    }

    private static ExifSummary? ReadThroughRawExif(byte[] orf)
    {
        var info = new RawContainerInfo(RawFormat.Orf, 0, 0, 1, [], [new ExifBlock(0, orf.Length, IsTiffHeader: true)]);
        return RawExif.TryReadExif(new InMemoryRawHeaderSource(orf), info);
    }

    [Fact]
    public void TryReadExif_IiroBlock_YieldsMakeAndModel()
    {
        var exif = ReadThroughRawExif(BuildOrf(true, Iiro));

        Assert.NotNull(exif);
        Assert.Equal("OLYMPUS", exif.CameraMake);
        Assert.Equal("E-M1", exif.CameraModel);
    }

    [Fact]
    public void TryReadExif_IirsBlock_YieldsMakeAndModel()
    {
        var exif = ReadThroughRawExif(BuildOrf(true, Iirs));

        Assert.NotNull(exif);
        Assert.Equal("OLYMPUS", exif.CameraMake);
    }

    [Fact]
    public void TryReadExif_BigEndianMmorBlock_YieldsMakeAndModel()
    {
        var exif = ReadThroughRawExif(BuildOrf(false, Mmor));

        Assert.NotNull(exif);
        Assert.Equal("E-M1", exif.CameraModel);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x002B)] // BigTIFF
    [InlineData(0x4F53)]
    [InlineData(0xFFFF)]
    public void TryReadExif_UnknownMagic_IsRejected(ushort magic)
    {
        var block = BuildOrf(true, [(byte)'I', (byte)'I', (byte)(magic & 0xFF), (byte)(magic >> 8)]);

        Assert.Null(ReadThroughRawExif(block));
    }

    [Fact]
    public void TryParseJpeg_ExifBlockWithOrfMagic_StaysStrict()
    {
        var block = BuildOrf(true, Iiro);
        byte[] payload = [.. "Exif\0\0"u8, .. block];
        int length = payload.Length + 2;
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, 0xFF, 0xD9];

        Assert.Null(ExifParser.TryParseJpeg(jpeg));
    }

    [Fact]
    public void TryReadExif_HostileOrfBlock_DoesNotThrow()
    {
        var block = BuildOrf(true, Iiro);
        // IFD0 offset far outside the block and an entry count larger than the data.
        block[4] = 0xFF; block[5] = 0xFF; block[6] = 0xFF; block[7] = 0x7F;
        Assert.Null(ReadThroughRawExif(block));

        var counted = BuildOrf(true, Iiro);
        counted[8] = 0xFF; counted[9] = 0xFF;
        _ = ReadThroughRawExif(counted);
    }

    [Fact]
    [Trait("Category", "Native")]
    public void CorpusOrfFiles_ExposeExif()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var files = Directory.GetFiles(CorpusDir, "*.orf");
        foreach (var file in files)
        {
            using var fs = File.OpenRead(file);
            using var source = new SourceRawHeaderSource(fs);
            var info = new OrfContainerReader().Read(source, CancellationToken.None);

            var exif = RawExif.TryReadExif(source, info);

            Assert.True(exif is not null, $"{Path.GetFileName(file)}: no EXIF read from the ORF.");
            Assert.False(string.IsNullOrEmpty(exif.CameraMake), $"{Path.GetFileName(file)}: no camera make.");
        }
    }
}
