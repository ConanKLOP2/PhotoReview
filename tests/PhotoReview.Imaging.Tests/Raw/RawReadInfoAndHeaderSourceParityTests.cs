using System.Buffers.Binary;
using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Decoding;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RawDecoder.ReadInfo never reports a 0x0 "size" (callers show it as dimensions), and InMemoryRawHeaderSource charges
/// the header budget block by block exactly like SourceRawHeaderSource.
/// </summary>
public sealed class RawReadInfoAndHeaderSourceParityTests
{
    private const int Block = SourceRawHeaderSource.BlockSize;

    [Fact]
    public void ReadInfo_RafWithNoPreviewAndNoSensorSize_ThrowsInvalidData()
    {
        using var temp = new TempRoot("raw-readinfo-nosize");
        // BuildRaf patched to an invalid JPEG pointer and no CFA header: nothing in the file declares any size.
        var data = SyntheticRawBuilder.BuildRaf();
        data[84] = data[85] = data[86] = data[87] = 0;
        data[92] = data[93] = data[94] = data[95] = 0;
        var path = temp.File("nosize.raf", data);

        var ex = Assert.Throws<InvalidDataException>(() => new RawDecoder(new WpfBitmapImageDecoder()).ReadInfo(path));

        // Pin the REASON (the 0x0 rule in RawDecoder.ReadInfo), not just the exception type any malformed RAF would also throw.
        Assert.Contains("declares no image size", ex.Message, StringComparison.Ordinal);
        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.Equal(Tr.ImageErrorRawCorrupt, UserFacingError.Describe(ex));
    }

    /// <summary>The synthetic RAF with its JPEG pointers zeroed (no preview) and a real CFA header declaring <paramref name="width"/> x <paramref name="height"/>.</summary>
    private static byte[] RafWithoutPreviewAndCfaSize(ushort width, ushort height)
    {
        var raf = SyntheticRawBuilder.BuildRaf().ToList();
        uint cfaOffset = (uint)raf.Count;
        var cfa = new List<byte>();
        cfa.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(1u))); // one record
        foreach (var value in new ushort[] { 0x0111, 4, height, width })
            cfa.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(value)));
        raf.AddRange(cfa);
        var bytes = raf.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(84), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(88), 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(92), cfaOffset);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(96), (uint)cfa.Count);
        return bytes;
    }

    [Fact]
    public void ReadInfo_RealRafBytesWithCfaSizeButNoPreview_ReturnsTheCfaSize()
    {
        using var temp = new TempRoot("raw-readinfo-real-raf");
        var path = temp.File("cfa-only.raf", RafWithoutPreviewAndCfaSize(6000, 4000));

        var info = new RawDecoder(new WpfBitmapImageDecoder()).ReadInfo(path);

        Assert.Equal((6000, 4000), (info.Width, info.Height));
    }

    [Fact]
    public void ReadInfo_RafWithSensorSizeButNoPreview_ReturnsTheSensorSize()
    {
        using var temp = new TempRoot("raw-readinfo-sensor");
        var data = SyntheticRawBuilder.BuildRaf();
        data[84] = data[85] = data[86] = data[87] = 0;
        var info = new RawContainerInfo(RawFormat.Raf, 6000, 4000, 1, [], []);
        var path = temp.File("sensor.raf", data);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder(),
            registry: new RawContainerReaderRegistry([new FixedRafReader(info)]));

        Assert.Equal((6000, 4000), (decoder.ReadInfo(path).Width, decoder.ReadInfo(path).Height));
    }

    private sealed class FixedRafReader(RawContainerInfo info) : IRawContainerReader
    {
        public RawFormat Format => info.Format;
        public bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension) => extension.Equals(".raf", StringComparison.OrdinalIgnoreCase);
        public RawContainerInfo Read(IRawHeaderSource source, CancellationToken cancellationToken) => info;
    }

    [Fact]
    public void Read_MultiBlockReadThatExhaustsTheBudget_KeepsTheEarlierBlocksChargedLikeProduction()
    {
        // 9 MiB of blocks: a read of the last 8 MiB + 1 byte range cannot fit, but the reads before it charge normally.
        var bytes = new byte[RawContainerLimits.MaxHeaderBytes + (2 * Block)];
        var memory = new InMemoryRawHeaderSource(bytes);
        using var stream = new MemoryStream(bytes);
        using var production = new SourceRawHeaderSource(stream);

        // Charge 126 blocks (MaxHeaderBytes / Block = 128, minus 2; two under the budget) then attempt a read spanning 3 untouched blocks.
        int blocksBefore = (RawContainerLimits.MaxHeaderBytes / Block) - 2;
        _ = memory.Read(0, blocksBefore * Block);
        _ = production.Read(0, blocksBefore * Block);
        long offset = (long)blocksBefore * Block;
        int count = 3 * Block;
        Assert.Throws<InvalidDataException>(() => memory.Read(offset, count));
        Assert.Throws<InvalidDataException>(() => production.Read(offset, count));

        Assert.Equal(production.TotalBytesRead, memory.TotalBytesRead);
        // The first blocks of the failed read were kept: re-reading just them is free and succeeds in both.
        _ = memory.Read(offset, 2 * Block);
        _ = production.Read(offset, 2 * Block);
        Assert.Equal(production.TotalBytesRead, memory.TotalBytesRead);
    }
}
