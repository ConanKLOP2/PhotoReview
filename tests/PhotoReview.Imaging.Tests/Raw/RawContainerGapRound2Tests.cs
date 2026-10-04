using System.Buffers.Binary;
using System.Text;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Bmff;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Second round of mutation-gap tests for <see cref="BmffBoxNavigator"/>, <see cref="Cr3ContainerReader"/> and <see cref="PreviewSelector"/>.</summary>
public sealed class RawContainerGapRound2Tests
{
    private static readonly byte[] CanonMoovUuid =
    [
        0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48
    ];

    /// <summary>A source that, against the interface contract, returns one byte fewer than asked for a read at <c>shortAt</c>.</summary>
    private sealed class ShortReadSource(byte[] data, long shortAt) : IRawHeaderSource
    {
        public long Length => data.Length;

        public ReadOnlySpan<byte> Read(long offset, int count) =>
            data.AsSpan((int)offset, offset == shortAt ? count - 1 : count);
    }

    // ---------------------------------------------------------------- BmffBoxNavigator: defensive short reads

    [Fact]
    public void TryReadBox_ShortHeaderRead_ReturnsFalse()
    {
        var file = Cat(Box("free", new byte[8]), Box("free", new byte[8]));

        Assert.False(BmffBoxNavigator.TryReadBox(new ShortReadSource(file, shortAt: 0), 0, out _));
    }

    [Fact]
    public void TryReadBox_ShortLargeSizeRead_ReturnsFalse()
    {
        var file = Cat(U32(1), Ascii("free"), U64(24), new byte[8]);

        Assert.False(BmffBoxNavigator.TryReadBox(new ShortReadSource(file, shortAt: 8), 0, out _));
    }

    [Fact]
    public void TryReadBox_ShortUuidRead_ReturnsFalse()
    {
        var file = Box("uuid", CanonMoovUuid, new byte[8]);

        Assert.False(BmffBoxNavigator.TryReadBox(new ShortReadSource(file, shortAt: 8), 0, out _));
    }

    // ---------------------------------------------------------------- Cr3ContainerReader

    [Fact]
    public void Read_SixteenByteFile_IsAcceptedAsAContainerWithoutPreviews()
    {
        var file = Cat(Box("free"), Box("free"));
        Assert.Equal(16, file.Length);

        var info = Read(file);

        Assert.Empty(info.Previews);
        Assert.Empty(info.ExifBlocks);
    }

    [Fact]
    public void Read_FifteenByteFile_IsRejected() =>
        Assert.Throws<System.IO.InvalidDataException>(() => Read(new byte[15]));

    [Fact]
    public void Read_Cmt2Box_IsReportedAsAnExifIfdBlock()
    {
        var cmt2Payload = new TiffBytes(true, 20).Header(8).Ifd(8, 0).ToArray();
        var file = Cat(Ftyp(), Box("moov", Box("uuid", CanonMoovUuid, Box("CMT2", cmt2Payload))));

        var block = Assert.Single(Read(file).ExifBlocks);

        Assert.True(block.IsTiffHeader);
        Assert.True(block.IfdIsExif);
        Assert.Equal(cmt2Payload.Length, block.Length);
    }

    [Fact]
    public void Read_Cmt1OrientationEntryWithZeroCount_IsIgnored()
    {
        var tiff = new TiffBytes(true, 8 + 2 + 12 + 4).Header(8).Ifd(8, 0, new Entry(0x0112, 3, 0, 6)).ToArray();
        var file = Cat(Ftyp(), Box("moov", Box("uuid", CanonMoovUuid, Box("CMT1", tiff))));

        Assert.Equal(1, Read(file).Orientation);
    }

    [Fact]
    public void Read_Cmt1AtEndOfFileWithIfdPointerJustPastThePayload_DoesNotThrow()
    {
        var tiff = new TiffBytes(true, 16).Header(17).ToArray();
        var file = Cat(Ftyp(), Box("moov", Box("uuid", CanonMoovUuid, Box("CMT1", tiff))));

        var info = Read(file);

        Assert.Equal(1, info.Orientation);
        Assert.Single(info.ExifBlocks);
    }

    [Fact]
    public void Read_StszTooShortForItsHeader_YieldsNoPreviewEvenWhenFollowingBytesLookLikeOne()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Box("stsz", U32(0), U32((uint)jpeg.Length)), // payload 8 bytes: shorter than the 12-byte header
            Box("stco", U32(0), U32(1), U32((uint)off))));

        Assert.Empty(Read(file).Previews);
    }

    [Fact]
    public void Read_StcoTooShort_FallsBackToCo64()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);

        var file = TrackFile(jpeg, off => Trak(
            Box("stsz", U32(0), U32((uint)jpeg.Length), U32(1)),
            Box("stco", U32(0)),
            Box("co64", U32(0), U32(1), U64(off))));

        var preview = Assert.Single(Read(file).Previews);
        Assert.Equal(640, preview.Width);
    }

    // ---------------------------------------------------------------- PreviewSelector

    [Fact]
    public void SelectPreview_NoJpegPreviewKind_FallsBackToTheFirstPreview()
    {
        var previews = new[] { new EmbeddedPreview(0, 0, 100, (EmbeddedPreviewKind)7, 10, 10, PreviewColorSpace.Unknown) };

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(new byte[256]), previews, DecodeBox.Unbounded, 1);

        Assert.Same(previews[0], chosen);
    }

    [Fact]
    public void SelectPreview_FourByteUnknownSizePreview_IsMarkedResolvedWithoutAWalk()
    {
        var previews = new[] { new EmbeddedPreview(0, 0, 4, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown) };

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource([0xFF, 0xD8, 0xFF, 0xD9, 0, 0, 0, 0]), previews, DecodeBox.Unbounded, 1, out var resolved);

        Assert.True(chosen!.HeaderResolved);
        Assert.True(resolved[0].HeaderResolved);
    }

    // ---------------------------------------------------------------- builders

    private static RawContainerInfo Read(byte[] file) =>
        new Cr3ContainerReader().Read(new InMemoryRawHeaderSource(file), CancellationToken.None);

    private static byte[] TrackFile(byte[] jpeg, Func<long, byte[]> trak)
    {
        long offset = Cat(Ftyp(), Box("moov", trak(0))).Length;
        return Cat(Ftyp(), Box("moov", trak(offset)), jpeg);
    }

    private static byte[] Trak(params byte[][] stblChildren) => Box("trak", Box("mdia", Box("minf", Box("stbl", stblChildren))));

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v);
        return b;
    }

    private static byte[] Ftyp() => Box("ftyp", Ascii("crx "), U32(1), Ascii("crx "));

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = Cat(parts);
        return Cat(U32((uint)(8 + payload.Length)), Ascii(type), payload);
    }
}
