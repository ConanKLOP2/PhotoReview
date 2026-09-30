using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Raw;
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
}
