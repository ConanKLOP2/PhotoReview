using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>ORF and RW2 validate their header like CR2/NEF/ARW/DNG and never wrap a huge ImageWidth to a negative int.</summary>
[Trait("Category", "HotPath")]
public sealed class OrfRw2HeaderTests
{
    private static byte[] Junk() => Enumerable.Repeat((byte)0x5A, 64).ToArray();

    private static byte[] Tiff(bool littleEndian, ReadOnlySpan<byte> signature, params Entry[] ifd0) =>
        new TiffBytes(littleEndian, 200).Header(8, signature).Ifd(8, 0, ifd0).ToArray();

    [Fact]
    public void OrfRead_JunkHeader_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => new OrfContainerReader().Read(new InMemoryRawHeaderSource(Junk()), CancellationToken.None));

    [Fact]
    public void Rw2Read_JunkHeader_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => new Rw2ContainerReader().Read(new InMemoryRawHeaderSource(Junk()), CancellationToken.None));

    [Fact]
    public void OrfRead_TiffByteOrderWithUnknownMagic_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() =>
            new OrfContainerReader().Read(new InMemoryRawHeaderSource(Tiff(true, [(byte)'I', (byte)'I', 0x34, 0x12])), CancellationToken.None));

    [Theory]
    [InlineData((byte)'I', (byte)'I', (byte)0x52, (byte)0x4F)] // IIRO
    [InlineData((byte)'I', (byte)'I', (byte)0x52, (byte)0x53)] // IIRS
    [InlineData((byte)'I', (byte)'I', (byte)0x2A, (byte)0x00)] // plain TIFF
    public void OrfRead_AcceptedLittleEndianMagics_Parse(byte a, byte b, byte c, byte d)
    {
        var info = new OrfContainerReader().Read(
            new InMemoryRawHeaderSource(Tiff(true, [a, b, c, d], Short(0x0100, 4000), Short(0x0101, 3000))), CancellationToken.None);

        Assert.Equal(4000, info.SensorWidth);
        Assert.Equal(3000, info.SensorHeight);
    }

    [Fact]
    public void OrfRead_BigEndianMmor_Parses()
    {
        var info = new OrfContainerReader().Read(
            new InMemoryRawHeaderSource(Tiff(false, "MMOR"u8, Short(0x0100, 4000), Short(0x0101, 3000))), CancellationToken.None);

        Assert.Equal(4000, info.SensorWidth);
    }

    [Fact]
    public void OrfRead_ImageWidthFfffffff_ClampsInsteadOfWrappingNegative()
    {
        var info = new OrfContainerReader().Read(
            new InMemoryRawHeaderSource(Tiff(true, "IIRO"u8, Long(0x0100, 0xFFFFFFFF), Long(0x0101, 0xFFFFFFFF))), CancellationToken.None);

        Assert.Equal(int.MaxValue, info.SensorWidth);
        Assert.Equal(int.MaxValue, info.SensorHeight);
    }

    [Theory]
    [InlineData(false, (byte)0x49)] // wrong byte order (MM) even with the right magic bytes
    [InlineData(true, (byte)0x2A)]  // plain TIFF magic 42 is not RW2
    public void Rw2Read_WrongMagic_ThrowsInvalidData(bool littleEndian, byte magicLow)
    {
        var data = Tiff(littleEndian, littleEndian ? [(byte)'I', (byte)'I', magicLow, 0] : [(byte)'M', (byte)'M', 0x55, 0]);

        Assert.Throws<InvalidDataException>(() => new Rw2ContainerReader().Read(new InMemoryRawHeaderSource(data), CancellationToken.None));
    }

    [Fact]
    public void Rw2Read_ValidIiuHeader_Parses()
    {
        var info = new Rw2ContainerReader().Read(
            new InMemoryRawHeaderSource(Tiff(true, "IIU\0"u8, Short(0x0002, 5000), Short(0x0003, 3000))), CancellationToken.None);

        Assert.Equal(5000, info.SensorWidth);
        Assert.Equal(3000, info.SensorHeight);
    }
}
