using System.IO;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Gap tests for <see cref="PreviewSelector.TryExtractJpegDimensions"/> on hostile frame headers and for
/// <see cref="SourceRawHeaderSource"/> multi-block reads and a stream shorter than its declared length.
/// </summary>
public sealed class RawJpegFrameAndHeaderSourceGapTests
{
    private static byte[] Sof(byte marker, int declaredSegmentLength, params byte[] payload) =>
        [0xFF, marker, (byte)(declaredSegmentLength >> 8), (byte)declaredSegmentLength, .. payload];

    private static readonly byte[] Frame640x480 = [8, 0x01, 0xE0, 0x02, 0x80, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1];

    private static bool TryDims(byte[] jpeg, out int width, out int height) =>
        PreviewSelector.TryExtractJpegDimensions(jpeg, out width, out height, out _);

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    public void TryExtractJpegDimensions_LossySofFrame_ReportsTheDimensions_Control(byte marker)
    {
        byte[] jpeg = [0xFF, 0xD8, .. Sof(marker, 2 + Frame640x480.Length, Frame640x480), 0xFF, 0xD9];

        Assert.True(TryDims(jpeg, out var width, out var height));
        Assert.Equal((640, 480), (width, height));
    }

    [Theory]
    [InlineData((byte)0xC3)] // lossless
    [InlineData((byte)0xC5)] // differential sequential
    [InlineData((byte)0xC9)] // arithmetic sequential
    [InlineData((byte)0xCA)] // arithmetic progressive
    [InlineData((byte)0xCB)] // arithmetic lossless
    public void TryExtractJpegDimensions_SofFrameTypeThatIsNotBaselineOrHuffmanLossy_ReturnsFalse(byte marker)
    {
        byte[] jpeg = [0xFF, 0xD8, .. Sof(marker, 2 + Frame640x480.Length, Frame640x480), 0xFF, 0xD9];

        Assert.False(TryDims(jpeg, out var width, out var height));
        Assert.Equal((0, 0), (width, height));
    }

    [Fact]
    public void TryExtractJpegDimensions_SofSegmentRunsPastTheBuffer_ReturnsFalseWithoutThrowing()
    {
        // Declares a 19-byte segment but the buffer ends after the first 8 payload bytes.
        byte[] jpeg = [0xFF, 0xD8, .. Sof(0xC0, 2 + Frame640x480.Length, Frame640x480[..8])];

        Assert.False(TryDims(jpeg, out var width, out var height));
        Assert.Equal((0, 0), (width, height));
    }

    [Fact]
    public void TryExtractJpegDimensions_SofSegmentLengthFarBeyondTheBuffer_ReturnsFalse()
    {
        byte[] jpeg = [0xFF, 0xD8, .. Sof(0xC0, 0xFFFF, Frame640x480)];

        Assert.False(TryDims(jpeg, out _, out _));
    }

    [Fact]
    public void TryExtractJpegDimensions_SofWithTooSmallPayload_DoesNotReadTheDimensionsFromTheFollowingSegment()
    {
        // The SOF says it has a 2-byte payload; the 4 bytes after it belong to the next segment (0x01E0 / 0x0280 look like a size).
        byte[] jpeg = [0xFF, 0xD8, .. Sof(0xC0, 4, 8, 0x01), 0x01, 0xE0, 0x02, 0x80, 0xFF, 0xD9];

        Assert.False(TryDims(jpeg, out var width, out var height));
        Assert.Equal((0, 0), (width, height));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00 })]
    public void TryExtractJpegDimensions_BufferEndsInsideTheFirstSegmentHeader_ReturnsFalse(byte[] jpeg)
    {
        Assert.False(TryDims(jpeg, out _, out _));
    }

    // ---------------------------------------------------------------- SourceRawHeaderSource

    private static byte Pattern(long index) => (byte)((index * 7) + (index >> 16));

    private static byte[] PatternBytes(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = Pattern(i);
        return data;
    }

    private static byte[] Expected(long offset, int count)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++) data[i] = Pattern(offset + i);
        return data;
    }

    [Fact]
    public void Read_SuccessiveMultiBlockReadsOfDifferentSizes_EachReturnTheirOwnBytes()
    {
        const int Block = SourceRawHeaderSource.BlockSize;
        using var source = new SourceRawHeaderSource(new MemoryStream(PatternBytes(3 * Block)));

        // Large span over blocks 0-2, then smaller spans over 0-1 and 1-2 (the shared buffer is reused), then large again.
        Assert.Equal(Expected(Block - 100, Block + 200), source.Read(Block - 100, Block + 200).ToArray());
        var small = source.Read(Block - 10, 20);
        Assert.Equal(20, small.Length);
        Assert.Equal(Expected(Block - 10, 20), small.ToArray());
        Assert.Equal(Expected((2 * Block) - 5, 10), source.Read((2 * Block) - 5, 10).ToArray());
        Assert.Equal(Expected(10, (2 * Block) + 50), source.Read(10, (2 * Block) + 50).ToArray());
    }

    [Fact]
    public void Read_SecondMultiBlockRead_DoesNotChangeTheBytesAlreadyCopiedByTheCaller()
    {
        // The multi-block span is a view over one shared buffer, so a caller that needs both results has to copy the first.
        const int Block = SourceRawHeaderSource.BlockSize;
        using var source = new SourceRawHeaderSource(new MemoryStream(PatternBytes(3 * Block)));

        var first = source.Read(Block - 50, 100).ToArray();
        var second = source.Read((2 * Block) - 50, 100).ToArray();

        Assert.Equal(Expected(Block - 50, 100), first);
        Assert.Equal(Expected((2 * Block) - 50, 100), second);
    }

    private sealed class ShortStream(byte[] actual, long declaredLength) : MemoryStream(actual)
    {
        public override long Length => declaredLength;
    }

    [Fact]
    public void Read_StreamEndsBeforeItsDeclaredLength_ThrowsInvalidDataAndChargesNothingForTheShortBlock()
    {
        const int Block = SourceRawHeaderSource.BlockSize;
        using var source = new SourceRawHeaderSource(new ShortStream(PatternBytes(Block + 4000), declaredLength: 2L * Block));

        Assert.Equal(Expected(0, 16), source.Read(0, 16).ToArray()); // block 0 is whole
        var ex = Assert.Throws<InvalidDataException>(() => source.Read(Block + 10, 16)); // block 1 holds 4000 of 65536 bytes

        Assert.Null(ex.InnerException);
        Assert.Equal(Block, source.TotalBytesRead);
    }

    [Fact]
    public void Read_MultiBlockReadCrossingTheShortBlock_ThrowsInvalidData()
    {
        const int Block = SourceRawHeaderSource.BlockSize;
        using var source = new SourceRawHeaderSource(new ShortStream(PatternBytes(Block + 4000), declaredLength: 2L * Block));

        Assert.Throws<InvalidDataException>(() => source.Read(Block - 8, 16));
    }
}
