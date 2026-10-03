using System.IO;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Exact-value tests for <see cref="DngContainerReader"/>: header sniffing, IFD chain / SubIFD caps, orientation range,
/// primary IFD choice (raw vs non-raw, area ties, zero dimensions) and the preview-size fallback.
/// </summary>
public sealed class DngMutationGapTests
{
    private const ushort Cfa = 32803;
    private const ushort Rgb = 2;

    private static RawContainerInfo Read(byte[] data) =>
        new DngContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    private static Entry[] Image(int width, int height, ushort photometric) =>
        [Short(0x0100, (ushort)width), Short(0x0101, (ushort)height), Short(0x0106, photometric)];

    // ---------------------------------------------------------------- CanRead

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanRead_FourByteTiffHeader_IsAccepted(bool littleEndian)
    {
        byte[] head = littleEndian ? [0x49, 0x49, 0x2A, 0x00] : [0x4D, 0x4D, 0x00, 0x2A];

        Assert.True(new DngContainerReader().CanRead(head, ".dng"));
        Assert.False(new DngContainerReader().CanRead(head.AsSpan(0, 3), ".dng"));
        Assert.False(new DngContainerReader().CanRead(head, ".tif"));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    public void CanRead_AnySingleSignatureByteWrong_IsRejected(bool littleEndian, int position)
    {
        byte[] head = littleEndian ? [0x49, 0x49, 0x2A, 0x00] : [0x4D, 0x4D, 0x00, 0x2A];
        head[position] ^= 0x40;

        Assert.False(new DngContainerReader().CanRead(head, ".dng"));
    }

    // ---------------------------------------------------------------- header

    [Fact]
    public void Read_SixteenByteFileWithEmptyIfd_ReturnsEmptyInfo()
    {
        var info = Read(new TiffBytes(true, 16).Header(8).Ifd(8, 0).ToArray());

        Assert.Equal((0, 0), (info.SensorWidth, info.SensorHeight));
        Assert.Equal(1, info.Orientation);
        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Read_ValidByteOrderButMagicFortyThree_Throws()
    {
        var data = new TiffBytes(true, 64).Header(8, "II+\0"u8).Ifd(8, 0, Image(100, 100, Cfa)).ToArray();

        Assert.Throws<InvalidDataException>(() => Read(data));
    }

    // ---------------------------------------------------------------- chain and SubIFD caps

    [Fact]
    public void Read_ChainOfSixtySixIfds_ConsidersOnlyTheFirstSixtyFive()
    {
        // IFD i is 100+i wide, so the widest IFD read wins: the chain walk stops after MaxIfdCount (64) IFDs, whose last
        // next-pointer registers IFD 64 (the 65th); IFD 65 is never reached.
        const int ifdSize = 42;
        var file = new TiffBytes(true, 8 + (66 * ifdSize)).Header(8);
        for (int i = 0; i < 66; i++)
        {
            int at = 8 + (i * ifdSize);
            file.Ifd(at, i < 65 ? (uint)(at + ifdSize) : 0, Image(100 + i, 100, Cfa));
        }

        var info = Read(file.ToArray());

        Assert.Equal((164, 100), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_SixtyFiveSubIfdsAcrossTwoEntries_IgnoresTheSixtyFifth()
    {
        const int ifdSize = 42;
        const int arrayAt = 80;
        const int chainIfdAt = 340;
        const int subsAt = 400;
        var file = new TiffBytes(true, subsAt + (65 * ifdSize) + 8).Header(8)
            .Ifd(8, chainIfdAt, [.. Image(10, 10, Rgb), At(0x014A, 4, 64, arrayAt)])
            .Ifd(chainIfdAt, 0, Long(0x014A, (uint)(subsAt + (64 * ifdSize))));
        for (int i = 0; i < 64; i++)
            file.U32(arrayAt + (i * 4), (uint)(subsAt + (i * ifdSize)));
        for (int i = 0; i < 64; i++)
            file.Ifd(subsAt + (i * ifdSize), 0, Image(50, 50, Cfa));
        file.Ifd(subsAt + (64 * ifdSize), 0, Image(5000, 4000, Cfa));

        var info = Read(file.ToArray());

        Assert.Equal((50, 50), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_NextIfdPointerEqualToARegisteredSubIfd_ParsesThatIfdOnce()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var data = new TiffBytes(true, 400 + jpeg.Length).Header(8)
            .Ifd(8, 200, [.. Image(4000, 3000, Cfa), Long(0x014A, 200)])
            .Ifd(200, 0, Short(0x0100, 640), Short(0x0101, 480), Long(0x0201, 400), Long(0x0202, (uint)jpeg.Length))
            .Put(400, jpeg)
            .ToArray();

        var info = Read(data);

        var preview = Assert.Single(info.Previews);
        Assert.Equal((640, 480), (preview.Width, preview.Height));
        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    // ---------------------------------------------------------------- orientation

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(0, 6)]
    [InlineData(9, 6)]
    public void Read_SecondOrientationEntryInIfd0_OverridesOnlyWithinOneToEight(int second, int expected)
    {
        var data = new TiffBytes(true, 128).Header(8)
            .Ifd(8, 0, [.. Image(100, 100, Cfa), Short(0x0112, 6), Short(0x0112, (ushort)second)])
            .ToArray();

        Assert.Equal(expected, Read(data).Orientation);
    }

    // ---------------------------------------------------------------- primary IFD choice

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Read_RawIfdWithAZeroDimension_NeverBecomesThePrimaryImage(int width, int height)
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 120, Image(4000, 3000, Rgb))
            .Ifd(120, 0, Image(width, height, Cfa))
            .ToArray();

        var info = Read(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_TwoRawIfdsOfEqualArea_KeepsTheFirst()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 120, Image(4000, 3000, Cfa))
            .Ifd(120, 0, Image(3000, 4000, Cfa))
            .ToArray();

        var info = Read(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_SmallerNonRawIfdAfterLargerNonRawIfd_KeepsTheLarger()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 120, Image(4000, 3000, Rgb))
            .Ifd(120, 0, Image(100, 100, Rgb))
            .ToArray();

        var info = Read(data);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void Read_RawIfdFollowedBySmallerNonRawIfd_KeepsTheRaw()
    {
        var data = new TiffBytes(true, 256).Header(8)
            .Ifd(8, 120, Image(100, 100, Cfa))
            .Ifd(120, 0, Image(4000, 3000, Rgb))
            .ToArray();

        var info = Read(data);

        Assert.Equal((100, 100), (info.SensorWidth, info.SensorHeight));
    }

    // ---------------------------------------------------------------- preview-size fallback

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_OnlyPreviewIfds_SensorSizeIsTheLargestPreview(bool largestFirst)
    {
        var small = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var large = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var (first, second) = largestFirst ? (large, small) : (small, large);
        var firstSize = largestFirst ? (640, 480) : (320, 240);
        var secondSize = largestFirst ? (320, 240) : (640, 480);
        var data = new TiffBytes(true, 600 + first.Length + second.Length).Header(8)
            .Ifd(8, 120, Short(0x0100, (ushort)firstSize.Item1), Short(0x0101, (ushort)firstSize.Item2), Long(0x0201, 600), Long(0x0202, (uint)first.Length))
            .Ifd(120, 0, Short(0x0100, (ushort)secondSize.Item1), Short(0x0101, (ushort)secondSize.Item2), Long(0x0201, (uint)(600 + first.Length)), Long(0x0202, (uint)second.Length))
            .Put(600, first)
            .Put(600 + first.Length, second)
            .ToArray();

        var info = Read(data);

        Assert.Equal(2, info.Previews.Count);
        Assert.Equal((640, 480), (info.SensorWidth, info.SensorHeight));
    }
}