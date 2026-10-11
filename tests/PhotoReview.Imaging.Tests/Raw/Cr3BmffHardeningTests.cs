using System.Buffers.Binary;
using System.IO;
using System.Text;
using PhotoReview.Imaging.Raw.Bmff;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// CR3 / ISO-BMFF hardening: the real Canon preview layout (uuid prefix, PRVW header) and hostile box structures
/// (overflowing sizes, undersized uuid boxes, out-of-range chunk offsets, bad CMT1 IFD offsets / sensor sizes).
/// </summary>
public sealed class Cr3BmffHardeningTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static readonly byte[] CanonMoovUuid =
    [
        0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48
    ];

    private static readonly byte[] PreviewUuid =
    [
        0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16
    ];

    // ---------------------------------------------------------------- real corpus

    [Fact]
    [Trait("Category", "Native")]
    public void Read_RealCanonCr3Corpus_ReturnsPrvwPreviewFirstThenThumbnail()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var files = Directory.GetFiles(CorpusDir, "*.cr3");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var fs = File.OpenRead(file);
            using var source = new SourceRawHeaderSource(fs);
            var info = new Cr3ContainerReader().Read(source, CancellationToken.None);
            string name = Path.GetFileName(file);

            // PRVW (1620x1080) is listed first, THMB (160x120) last.
            var prvw = info.Previews[0];
            Assert.True(prvw is { Width: 1620, Height: 1080 }, $"{name}: first preview was {prvw.Width}x{prvw.Height}");
            var thumb = info.Previews[^1];
            Assert.True(thumb is { Width: 160, Height: 120 }, $"{name}: last preview was {thumb.Width}x{thumb.Height}");

            for (int i = 0; i < info.Previews.Count; i++)
                Assert.Equal(i, info.Previews[i].Index);

            foreach (var preview in new[] { prvw, thumb })
            {
                var bytes = new byte[preview.Length];
                fs.Position = preview.Offset;
                fs.ReadExactly(bytes);

                Assert.True(bytes[0] == 0xFF && bytes[1] == 0xD8, $"{name}: preview at {preview.Offset} lacks JPEG SOI");
                Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(bytes), 0, bytes.Length, out int w, out int h, out _));
                Assert.Equal((preview.Width, preview.Height), (w, h));

                // Real decode through libjpeg-turbo agrees with the advertised dimensions.
                string tmp = Path.Combine(Path.GetTempPath(), $"cr3-preview-{Guid.NewGuid():N}.jpg");
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    var decoded = new TurboJpegDecoder(WpfBitmapSourceCodec.Instance).ReadInfo(tmp);
                    Assert.Equal((preview.Width, preview.Height), (decoded.PixelWidth, decoded.PixelHeight));
                }
                finally
                {
                    File.Delete(tmp);
                }
            }
        }
    }

    // ---------------------------------------------------------------- synthetic real-layout previews

    [Fact]
    public void Read_PreviewUuidWithEightBytePrefix_FindsPrvwAtSixteenByteHeaderOffset()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var file = Cat(Ftyp(), PreviewUuidBox(jpeg, width: 1620, height: 1080));

        var info = Read(file);

        var preview = Assert.Single(info.Previews);
        Assert.Equal((1620, 1080), (preview.Width, preview.Height));
        Assert.Equal(jpeg.Length, preview.Length);
        Assert.True(file.AsSpan((int)preview.Offset).StartsWith(jpeg));
    }

    [Fact]
    public void Read_PrvwWithoutSoiAtHeaderOffset_IsSkipped()
    {
        var notJpeg = new byte[64];
        var file = Cat(Ftyp(), PreviewUuidBox(notJpeg, width: 1620, height: 1080));

        var info = Read(file);

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Read_PrvwDeclaredJpegSizeLargerThanBox_IsClampedToBoxPayload()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(64, 48);
        var file = Cat(Ftyp(), PreviewUuidBox(jpeg, width: 64, height: 48, declaredJpegSize: uint.MaxValue));

        var info = Read(file);

        var preview = Assert.Single(info.Previews);
        Assert.Equal(jpeg.Length, preview.Length);
    }

    [Fact]
    public void Read_PrvwAndThmb_ListsPrvwBeforeThumbnailEvenWhenMoovComesFirst()
    {
        var thumbJpeg = SyntheticRawBuilder.CreateMinimalJpeg(160, 120);
        var prvwJpeg = SyntheticRawBuilder.CreateMinimalJpeg(1620, 1080);
        var file = Cat(
            Ftyp(),
            Box("moov", UuidBox(CanonMoovUuid, ThmbBox(thumbJpeg, 160, 120))),
            PreviewUuidBox(prvwJpeg, 1620, 1080));

        var info = Read(file);

        Assert.Equal(2, info.Previews.Count);
        Assert.Equal((1620, 1080), (info.Previews[0].Width, info.Previews[0].Height));
        Assert.Equal((160, 120), (info.Previews[1].Width, info.Previews[1].Height));
        Assert.Equal([0, 1], info.Previews.Select(p => p.Index));
    }

    [Fact]
    public void Read_ThmbDimensionsComeFromHeaderNotHardCodedConstants()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var file = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, ThmbBox(jpeg, 320, 240))));

        var info = Read(file);

        var thumb = Assert.Single(info.Previews);
        Assert.Equal((320, 240), (thumb.Width, thumb.Height));
        Assert.Equal(jpeg.Length, thumb.Length);
    }

    [Fact]
    public void Read_ThmbTruncatedAtEndOfFile_DoesNotThrowAndYieldsNoPreview()
    {
        // THMB payload of 13 bytes (> 12) at EOF: the old fixed 32-byte read ran past the file end.
        var file = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("THMB", new byte[13]))));

        var info = Read(file);

        Assert.Empty(info.Previews);
    }

    // ---------------------------------------------------------------- CMT1 hardening

    [Fact]
    public void Read_Cmt1SensorSizeAboveIntMax_IsIgnoredInsteadOfGoingNegative()
    {
        var cmt1 = Cmt1(entries: [(0x0100, 4, 0xFFFFFFFFu), (0x0101, 4, 4000u), (0x0112, 3, 6u)]);
        var file = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", cmt1))));

        var info = Read(file);

        Assert.Equal(0, info.SensorWidth);
        Assert.Equal(4000, info.SensorHeight);
        Assert.Equal(6, info.Orientation);
    }

    [Fact]
    public void Read_Cmt1Ifd0OffsetOutsidePayload_IsNotFollowedIntoUnrelatedFileBytes()
    {
        // CMT1 payload is only the 8-byte TIFF header (+padding) with IFD0 pointing 200 bytes past its start,
        // where the rest of the file happens to hold a plausible IFD with orientation 6.
        var cmt1Payload = new byte[16];
        cmt1Payload[0] = (byte)'I';
        cmt1Payload[1] = (byte)'I';
        cmt1Payload[2] = 0x2A;
        BinaryPrimitives.WriteUInt32LittleEndian(cmt1Payload.AsSpan(4), 200);

        var head = Cat(Ftyp(), Box("moov", UuidBox(CanonMoovUuid, Box("CMT1", cmt1Payload))));
        int cmt1PayloadOffset = IndexOf(head, "CMT1") + 4;
        var file = new byte[cmt1PayloadOffset + 200 + 64];
        head.CopyTo(file, 0);
        int ifd = cmt1PayloadOffset + 200;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(ifd), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(ifd + 2), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(ifd + 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ifd + 6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ifd + 10), 6);

        var info = Read(file);

        Assert.Equal(1, info.Orientation);
    }

    // ---------------------------------------------------------------- track chunk offsets

    [Theory]
    [InlineData(long.MaxValue - 1)]
    [InlineData(long.MaxValue - 50)]
    public void Read_Co64ChunkOffsetNearLongMax_DoesNotOverflowAndYieldsNoPreview(long chunkOffset)
    {
        var file = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: 100, chunkOffset: chunkOffset)));

        var info = Read(file);

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Read_Co64ChunkOffsetAboveLongMax_YieldsNoPreview()
    {
        var file = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: 100, chunkOffsetRaw: ulong.MaxValue - 3)));

        var info = Read(file);

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Read_Co64PointingAtValidJpegInsideFile_YieldsTrackPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var moovLess = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: (uint)jpeg.Length, chunkOffset: 0)));
        // Chunk data goes right after the moov; recompute the offset now that the size is known.
        long chunkOffset = moovLess.Length;
        var file = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: (uint)jpeg.Length, chunkOffset: chunkOffset)), jpeg);

        var info = Read(file);

        var preview = Assert.Single(info.Previews);
        Assert.Equal(chunkOffset, preview.Offset);
        Assert.Equal(jpeg.Length, preview.Length);
    }

    [Fact]
    public void Read_TrakSampleIsLosslessJpegSof3_IsNotAPreviewCandidate()
    {
        // CR3 raw track: lossless JPEG (FFD8 FFC3 ...) shares the SOI with real previews but cannot be decoded as one.
        var raw = LosslessJpeg(4000, 3000);
        var prvwBox = PreviewUuidBox(SyntheticRawBuilder.CreateMinimalJpeg(1620, 1080), 1620, 1080);
        var prefix = Cat(Ftyp(), prvwBox, Box("moov", TrakWithCo64(sampleSize: (uint)raw.Length, chunkOffset: 0)));
        long chunkOffset = prefix.Length;
        var file = Cat(Ftyp(), prvwBox, Box("moov", TrakWithCo64(sampleSize: (uint)raw.Length, chunkOffset: chunkOffset)), raw);

        var info = Read(file);

        var preview = Assert.Single(info.Previews);
        Assert.Equal((1620, 1080), (preview.Width, preview.Height));
        Assert.DoesNotContain(info.Previews, p => p.Offset == chunkOffset);
    }

    [Fact]
    public void Read_TrakSampleIsLosslessJpegSof3AndPrvwCorrupt_YieldsNoPreviewRangeCoveringRawTrack()
    {
        var raw = LosslessJpeg(4000, 3000);
        var badPrvw = PreviewUuidBox(new byte[64], 1620, 1080);
        var prefix = Cat(Ftyp(), badPrvw, Box("moov", TrakWithCo64(sampleSize: (uint)raw.Length, chunkOffset: 0)));
        long chunkOffset = prefix.Length;
        var file = Cat(Ftyp(), badPrvw, Box("moov", TrakWithCo64(sampleSize: (uint)raw.Length, chunkOffset: chunkOffset)), raw);

        var info = Read(file);

        Assert.Empty(info.Previews);
    }

    [Fact]
    public void Read_TrakSampleIsLossyJpeg_UsesProbedDimensions()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var prefix = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: (uint)jpeg.Length, chunkOffset: 0)));
        var file = Cat(Ftyp(), Box("moov", TrakWithCo64(sampleSize: (uint)jpeg.Length, chunkOffset: prefix.Length)), jpeg);

        var info = Read(file);

        var preview = Assert.Single(info.Previews);
        Assert.Equal((640, 480), (preview.Width, preview.Height));
    }

    private static byte[] LosslessJpeg(int width, int height) =>
        // SOI, SOF3 (len 11: precision 14, h, w, 1 component), then filler standing in for entropy-coded sensor data.
        Cat([0xFF, 0xD8, 0xFF, 0xC3, 0x00, 0x0B, 14], U16(height), U16(width), [1, 1, 0x11, 0], new byte[256]);

    // ---------------------------------------------------------------- BmffBoxNavigator

    [Fact]
    public void TryReadBox_LargesizeThatOverflowsWhenAddedToOffset_ReturnsFalse()
    {
        // Box at offset 20 declares largesize long.MaxValue - 8: offset + size wraps to a negative number.
        var file = Cat(Ftyp(), Cat(U32(1), Ascii("free"), U64(long.MaxValue - 8)), new byte[16]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.False(BmffBoxNavigator.TryReadBox(source, 20, out _));
    }

    [Fact]
    public void TryReadBox_LargesizeAboveLongMax_ReturnsFalse()
    {
        var file = Cat(Ftyp(), Cat(U32(1), Ascii("free"), U64Raw(ulong.MaxValue)), new byte[16]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.False(BmffBoxNavigator.TryReadBox(source, 20, out _));
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(20u)]
    [InlineData(23u)]
    public void TryReadBox_UuidBoxTooSmallForExtendedType_ReturnsFalse(uint size32)
    {
        var file = Cat(U32(size32), Ascii("uuid"), new byte[32]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.False(BmffBoxNavigator.TryReadBox(source, 0, out _));
    }

    [Fact]
    public void TryReadBox_UuidBoxExactlyHeaderPlusExtendedType_HasEmptyPayload()
    {
        var file = Cat(U32(24), Ascii("uuid"), new byte[16]);
        var source = new InMemoryRawHeaderSource(file);

        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var box));
        Assert.Equal(0, box.PayloadSize);
        Assert.Equal(24, box.PayloadOffset);
    }

    [Fact]
    public void ReadChildBoxes_ParentPayloadSizeOverflowingLimit_IsClampedToSource()
    {
        // Parent payload starts at offset 8, so PayloadOffset + long.MaxValue wraps negative if not clamped.
        var file = Cat(Box("skip", []), Box("free", new byte[8]), Box("skip", new byte[8]));
        var source = new InMemoryRawHeaderSource(file);
        var hostileParent = new BmffBoxNavigator.BmffBox("moov", 0, long.MaxValue, 8, long.MaxValue);

        var children = BmffBoxNavigator.ReadChildBoxes(source, hostileParent);

        Assert.Equal(["free", "skip"], children.Select(c => c.Type));
    }

    [Fact]
    public void ReadChildBoxes_NegativePayloadSize_ReturnsEmptyWithoutThrowing()
    {
        var source = new InMemoryRawHeaderSource(Box("free", new byte[8]));
        var hostileParent = new BmffBoxNavigator.BmffBox("moov", 0, 0, 0, -4);

        Assert.Empty(BmffBoxNavigator.ReadChildBoxes(source, hostileParent));
    }

    [Fact]
    public void ReadChildBoxes_ChildLargerThanParentPayload_StopsWithoutThrowing()
    {
        // Child claims 0x7FFFFFF0 bytes but the file (and parent) are tiny.
        var inner = Cat(U32(0x7FFFFFF0), Ascii("free"), new byte[8]);
        var file = Box("moov", inner);
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));

        Assert.Empty(BmffBoxNavigator.ReadChildBoxes(source, moov));
    }

    [Fact]
    public void ReadChildBoxes_MoreThanMaxChildBoxes_ReturnsExactlyTheCapWithoutThrowing()
    {
        var kids = Enumerable.Range(0, BmffBoxNavigator.MaxChildBoxes + 44).Select(_ => Box("free", [])).ToArray();
        var file = Box("moov", Cat(kids));
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));

        var children = BmffBoxNavigator.ReadChildBoxes(source, moov);

        Assert.Equal(BmffBoxNavigator.MaxChildBoxes, children.Count);
    }

    [Fact]
    public void ReadChildBoxes_PayloadSkipBeyondPayload_ReturnsEmpty()
    {
        var file = Box("moov", Box("free", []));
        var source = new InMemoryRawHeaderSource(file);
        Assert.True(BmffBoxNavigator.TryReadBox(source, 0, out var moov));

        Assert.Empty(BmffBoxNavigator.ReadChildBoxes(source, moov, payloadSkip: int.MaxValue));
    }

    // ---------------------------------------------------------------- builders

    private static RawContainerInfo Read(byte[] file) =>
        new Cr3ContainerReader().Read(new InMemoryRawHeaderSource(file), CancellationToken.None);

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] U16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(long v) => U64Raw((ulong)v);

    private static byte[] U64Raw(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    private static byte[] Ftyp() => Box("ftyp", Ascii("crx "), U32(1), Ascii("crx "));

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = Cat(parts);
        return Cat(U32((uint)(8 + payload.Length)), Ascii(type), payload);
    }

    private static byte[] UuidBox(byte[] uuid, params byte[][] parts) => Box("uuid", [uuid, .. parts]);

    private static byte[] PreviewUuidBox(byte[] jpeg, int width, int height, uint? declaredJpegSize = null)
    {
        // Canon layout: uuid payload = u32 0, u32 1, then PRVW box; PRVW payload = u32 0, u16 1, w, h, u16 1, u32 size, JPEG.
        var prvw = Box("PRVW", U32(0), U16(1), U16(width), U16(height), U16(1), U32(declaredJpegSize ?? (uint)jpeg.Length), jpeg);
        return UuidBox(PreviewUuid, U32(0), U32(1), prvw);
    }

    private static byte[] ThmbBox(byte[] jpeg, int width, int height) =>
        // THMB payload = u32 0, u16 w, u16 h, u32 size, u16 1, u16 0, JPEG.
        Box("THMB", U32(0), U16(width), U16(height), U32((uint)jpeg.Length), U16(1), U16(0), jpeg);

    private static byte[] Cmt1(params (ushort Tag, ushort Type, uint Value)[] entries)
    {
        var ms = new MemoryStream();
        ms.Write([(byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0]);
        Span<byte> tmp = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, (ushort)entries.Length);
        ms.Write(tmp[..2]);
        foreach (var (tag, type, value) in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tmp[2..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp[4..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(tmp[8..], value);
            ms.Write(tmp);
        }

        ms.Write(new byte[4]); // next IFD offset
        return ms.ToArray();
    }

    private static byte[] TrakWithCo64(uint sampleSize, long chunkOffset) =>
        TrakWithCo64(sampleSize, (ulong)chunkOffset);

    private static byte[] TrakWithCo64(uint sampleSize, ulong chunkOffsetRaw)
    {
        var stsz = Box("stsz", U32(0), U32(sampleSize), U32(1));
        var co64 = Box("co64", U32(0), U32(1), U64Raw(chunkOffsetRaw));
        return Box("trak", Box("mdia", Box("minf", Box("stbl", stsz, co64))));
    }

    private static int IndexOf(byte[] data, string ascii) => data.AsSpan().IndexOf(Ascii(ascii));
}