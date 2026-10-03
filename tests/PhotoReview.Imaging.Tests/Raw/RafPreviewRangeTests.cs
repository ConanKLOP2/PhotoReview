using System.Buffers.Binary;
using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Raf;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RAF: an invalid or missing embedded-JPEG range only drops the preview (the CFA size stays readable so the no-preview
/// LibRaw fallback can run), and CFA record 0x0121 (RawImageSize, as LibRaw's parse_fuji reads it) is the size when the
/// cropped record 0x0111 is absent.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class RafPreviewRangeTests
{
    private static RawContainerInfo Read(byte[] data) =>
        new RafContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    /// <summary>Synthetic RAF with its JPEG pointer patched to (offset, length) and a CFA header of (tag, height, width) records.</summary>
    private static byte[] Build(uint jpegOffset, uint jpegLength, params (ushort Tag, ushort Height, ushort Width)[] records)
    {
        var bytes = SyntheticRawBuilder.BuildRaf().ToList();
        uint cfaOffset = (uint)bytes.Count;
        var cfa = new List<byte>();
        AddU32(cfa, (uint)records.Length);
        foreach (var (tag, height, width) in records)
        {
            AddU16(cfa, tag);
            AddU16(cfa, 4);
            AddU16(cfa, height);
            AddU16(cfa, width);
        }

        bytes.AddRange(cfa);
        var data = bytes.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(84), jpegOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(88), jpegLength);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(92), cfaOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(96), (uint)cfa.Count);
        return data;
    }

    private static void AddU16(List<byte> list, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        list.AddRange(b.ToArray());
    }

    private static void AddU32(List<byte> list, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        list.AddRange(b.ToArray());
    }

    private static readonly (ushort, ushort, ushort) Full = (0x0100, 4032, 6160);

    public static TheoryData<uint, uint> BadRanges => new()
    {
        { 160, 0 },                // zero length
        { 0, 100 },                // zero offset
        { 50, 100 },               // overlaps the header
        { 99, 100 },               // overlaps the CFA pointer table
        { 160, 0x7FFF_FFFF },      // extends far past the end of the file (truncated copy)
        { uint.MaxValue, 100 },    // offset past the end
    };

    [Theory]
    [MemberData(nameof(BadRanges))]
    public void Read_InvalidJpegRange_DropsThePreviewButKeepsTheContainerAndCfaSize(uint offset, uint length)
    {
        var info = Read(Build(offset, length, Full, (0x0111, 4000, 6000)));

        Assert.Empty(info.Previews);
        Assert.Empty(info.ExifBlocks);
        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_ValidJpegRange_StillYieldsThePreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(1024, 768);
        var info = Read(Build(160, (uint)jpeg.Length, Full));

        var preview = Assert.Single(info.Previews);
        Assert.Equal(160, preview.Offset);
    }

    [Fact]
    public void Read_JpegRangeWithoutSoi_DropsThePreviewButKeepsTheContainerAndCfaSize()
    {
        // Offset 160 is inside the file but the bytes there are not a JPEG (SyntheticRawBuilder leaves them zero-filled
        // in the region this test patches): a pointer table that points at zeros must not yield a preview.
        var data = Build(160, 32, Full, (0x0111, 4000, 6000));
        data.AsSpan(160, 32).Clear();

        var info = Read(data);

        Assert.Empty(info.Previews);
        Assert.Empty(info.ExifBlocks);
        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_NoCroppedRecord_UsesRawImageSizeRecord0x0121()
    {
        var info = Read(Build(160, 10, Full, (0x0121, 4000, 6032)));

        Assert.Equal((6032, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_CroppedRecordPresent_BeatsRawImageSize()
    {
        var info = Read(Build(160, 10, Full, (0x0111, 4000, 6000), (0x0121, 4032, 6032)));

        Assert.Equal((6000, 4000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_RawImageSizeLargerThanFullReadout_IsIgnored()
    {
        var info = Read(Build(160, 10, Full, (0x0121, 9000, 9000)));

        Assert.Equal((6160, 4032), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_RawImageSizeWidth4284_IsWidenedTo4287LikeLibRaw()
    {
        var info = Read(Build(160, 10, (0x0121, 2856, 4284)));

        Assert.Equal((4287, 2856), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Decode_RafWithTruncatedJpeg_FallsBackToTheFullDecoder()
    {
        using var temp = new TempRoot("raf-truncated");
        var jpegPath = Path.Combine(temp.Path, "full.jpg");
        FixtureGenerator.GenerateGradientJpeg(jpegPath, 48, 32);
        var path = temp.File("truncated.raf", Build(160, 0x7FFF_FFFF, Full));
        var full = new RecordingFullDecoder(File.ReadAllBytes(jpegPath));

        var decoded = new RawDecoder(new WpfBitmapImageDecoder(), noPreviewDecoder: full)
            .Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        Assert.Equal(1, full.CallCount);
        Assert.Equal((48, 32), (decoded.PixelWidth, decoded.PixelHeight));
    }

    private sealed class RecordingFullDecoder(byte[] jpeg) : IImageDecoder
    {
        public int CallCount { get; private set; }
        public ImageInfo ReadInfo(string path) => throw new NotSupportedException();

        public IDecodedImage Decode(DecodeRequest request)
        {
            CallCount++;
            return new WpfBitmapImageDecoder().Decode(request with { Bytes = jpeg });
        }
    }
}
