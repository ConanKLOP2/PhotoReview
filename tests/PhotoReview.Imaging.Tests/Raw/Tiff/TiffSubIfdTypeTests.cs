using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// The SubIFDs tag (0x014A) may be written with field type IFD (13) as well as LONG (4). Type 13 had size 0 in
/// <see cref="TiffStructure.TypeSize"/>, so the pointer list came back empty and the raw IFD was never visited.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffSubIfdTypeTests
{
    private const int RawWidth = 6048;
    private const int RawHeight = 4024;

    private static RawContainerInfo Read(IRawContainerReader reader, byte[] data) =>
        reader.Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    /// <summary>IFD0 (no size) -> SubIFD raw IFD of 6048x4024 through a SubIFDs entry of <paramref name="type"/>, inline or as an array.</summary>
    private static byte[] Build(ushort type, bool array)
    {
        var file = new TiffBytes(true, 500).Header(8)
            .Ifd(8, 0, array ? At(0x014A, type, 2, 300) : At(0x014A, type, 1, 100))
            .Ifd(100, 0, Long(0x00FE, 0), Short(0x0100, RawWidth), Short(0x0101, RawHeight), Short(0x0103, 32767));
        if (array) file.U32(300, 100).U32(304, 400);
        file.Ifd(400, 0, Long(0x00FE, 1), Short(0x0100, 160), Short(0x0101, 120), Short(0x0103, 1));
        return file.ToArray();
    }

    public static TheoryData<string, ushort, bool> Cases()
    {
        var data = new TheoryData<string, ushort, bool>();
        foreach (var format in new[] { "dng", "arw", "nef" })
            foreach (bool array in new[] { false, true })
                foreach (ushort type in new ushort[] { 4, 13 })
                    data.Add(format, type, array);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SubIfdPointers_OfLongOrIfdType_ReachTheRawIfd(string format, ushort type, bool array)
    {
        IRawContainerReader reader = format switch
        {
            "dng" => new DngContainerReader(),
            "arw" => new ArwContainerReader(),
            _ => new NefContainerReader(),
        };

        var info = Read(reader, Build(type, array));

        Assert.Equal((RawWidth, RawHeight), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void TypeSize_IfdType_IsFourBytesAndReadsAsUnsigned()
    {
        Assert.Equal(4, TiffStructure.TypeSize(13));
        Assert.Equal(0x01020304L, TiffStructure.ReadUnsigned([4, 3, 2, 1], 13, littleEndian: true));
        Assert.Equal(0, TiffStructure.TypeSize(16)); // BigTIFF LONG8 stays unsupported
    }

    // ---------------------------------------------------------------- RV-R03: DNG signed sizes and wide compression

    private static RawContainerInfo ReadDng(byte[] data) => Read(new DngContainerReader(), data);

    [Theory]
    [InlineData((ushort)8)] // SSHORT
    [InlineData((ushort)9)] // SLONG
    public void Dng_NegativeImageSize_NeverYieldsNegativeSizes(ushort type)
    {
        // Raw-less IFD0 with a valid JPEG interchange preview but a signed -5 x -5 ImageWidth/ImageLength.
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = new TiffBytes(true, 1000 + jpeg.Length).Header(8)
            .Ifd(8, 0, At(0x0100, type, 1, unchecked((uint)-5)), At(0x0101, type, 1, unchecked((uint)-5)),
                Long(0x0201, 1000), Long(0x0202, (uint)jpeg.Length));
        file.Put(1000, jpeg);

        var info = ReadDng(file.ToArray());

        Assert.True(info.SensorWidth >= 0 && info.SensorHeight >= 0, $"sensor {info.SensorWidth}x{info.SensorHeight}");
        Assert.All(info.Previews, p => Assert.True(p.Width >= 0 && p.Height >= 0, $"preview {p.Width}x{p.Height}"));
    }

    [Fact]
    public void Dng_CompressionAboveUShort_IsNotTreatedAsJpegCompression()
    {
        // Compression LONG 65542 = 0x10006: truncated to (ushort) it became 6 and the lossy-JPEG strip was taken as a preview.
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = new TiffBytes(true, 1000 + jpeg.Length).Header(8)
            .Ifd(8, 0, Long(0x0103, 65542), Long(0x0111, 1000), Long(0x0117, (uint)jpeg.Length));
        file.Put(1000, jpeg);

        var info = ReadDng(file.ToArray());

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Dng_CompressionSix_StillYieldsTheStripPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = new TiffBytes(true, 1000 + jpeg.Length).Header(8)
            .Ifd(8, 0, Short(0x0103, 6), Long(0x0111, 1000), Long(0x0117, (uint)jpeg.Length));
        file.Put(1000, jpeg);

        var preview = Assert.Single(ReadDng(file.ToArray()).Previews);
        Assert.Equal((64, 48), (preview.Width, preview.Height));
    }
}
