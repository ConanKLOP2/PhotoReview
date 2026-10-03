using System.Buffers.Binary;
using System.IO;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Exact-value and exact-boundary tests for <see cref="JpegMarkerProbe"/>: range checks, every marker class, the segment and
/// fill-byte caps, frame header size limits and failure behaviour of the underlying source.
/// </summary>
public sealed class JpegMarkerProbeMutationGapTests
{
    private static byte[] Soi => [0xFF, 0xD8];

    private static byte[] Sof(byte marker = 0xC0, int width = 640, int height = 480, byte precision = 8) =>
        [0xFF, marker, 0x00, 0x0B, precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00];

    private static byte[] Segment(byte marker, int payloadLength)
    {
        var bytes = new byte[4 + payloadLength];
        bytes[0] = 0xFF;
        bytes[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)(payloadLength + 2));
        return bytes;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static bool Probe(byte[] bytes, out int width, out int height, long offset = 0, long? length = null) =>
        JpegMarkerProbe.TryReadLossyFrame(new InMemoryRawHeaderSource(bytes), offset, length ?? bytes.Length - offset, out width, out height);

    [Fact]
    public void TryReadLossyFrame_RangeEndingExactlyAtTheEndOfTheSource_IsProbed()
    {
        Assert.True(Probe(Cat(Soi, Sof()), out var w, out var h));
        Assert.Equal((640, 480), (w, h));
    }

    [Fact]
    public void TryReadLossyFrame_RangeStartingInsideTheSource_IsProbedFromThatOffset()
    {
        var bytes = Cat([1, 2, 3], Soi, Sof());

        Assert.True(Probe(bytes, out var w, out var h, offset: 3));
        Assert.Equal((640, 480), (w, h));
    }

    [Fact]
    public void TryReadLossyFrame_DeclaredRangeLongerThanTheSource_IsRejectedEvenWhenTheFrameIsInside()
    {
        var bytes = Cat(Soi, Sof());

        Assert.False(Probe(bytes, out _, out _, length: bytes.Length + 10));
    }

    [Fact]
    public void TryReadLossyFrame_NegativeOffset_ReturnsFalseWithoutThrowing()
    {
        Assert.False(Probe(Cat(Soi, Sof()), out _, out _, offset: -1, length: 10));
    }

    [Fact]
    public void TryReadLossyFrame_FrameHeaderBeyondTheEndOfTheRange_IsNotRead()
    {
        // The range is SOI + an APP0 segment; a valid frame header follows it in the source but outside the range.
        var range = Cat(Soi, Segment(0xE0, 4));
        var bytes = Cat(range, Sof());

        Assert.False(Probe(bytes, out _, out _, length: range.Length));
    }

    [Fact]
    public void TryReadLossyFrame_FrameHeaderCutOffByTheEndOfTheRange_IsNotRead()
    {
        var bytes = Cat(Soi, Sof());

        Assert.False(Probe(bytes, out _, out _, length: 10)); // the frame data would end at 11
    }

    [Fact]
    public void TryReadLossyFrame_MinimalFrameHeaderOfEightBytes_IsRead()
    {
        // Segment length 8 (6 payload bytes), the shortest accepted; the range ends with the width.
        byte[] sof = [0xFF, 0xC0, 0x00, 0x08, 0x08, 0x01, 0xE0, 0x02, 0x80];

        Assert.True(Probe(Cat(Soi, sof), out var w, out var h));
        Assert.Equal((640, 480), (w, h));
    }

    [Fact]
    public void TryReadLossyFrame_FrameHeaderSegmentOfSevenBytes_IsRejected()
    {
        byte[] sof = [0xFF, 0xC0, 0x00, 0x07, 0x08, 0x01, 0xE0, 0x02, 0x80];

        Assert.False(Probe(Cat(Soi, sof, new byte[4]), out _, out _));
    }

    [Theory]
    [InlineData((byte)0xC0, true)]
    [InlineData((byte)0xC1, true)]
    [InlineData((byte)0xC2, true)]
    [InlineData((byte)0xC3, false)]
    [InlineData((byte)0xC5, false)]
    [InlineData((byte)0xCF, false)]
    public void TryReadLossyFrame_FirstFrameMarker_OnlyLossySequentialAndProgressiveAreAccepted(byte marker, bool expected)
    {
        var bytes = Cat(Soi, Sof(marker, 1000, 700), Sof(0xC0, 111, 222));

        Assert.Equal(expected, Probe(bytes, out var w, out var h));
        Assert.Equal(expected ? (1000, 700) : (0, 0), (w, h));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, 0)]
    public void TryReadLossyFrame_ZeroWidthOrHeight_IsRejected(int width, int height)
    {
        Assert.False(Probe(Cat(Soi, Sof(0xC0, width, height)), out _, out _));
    }

    [Fact]
    public void TryReadLossyFrame_TwelveBitPrecision_IsRejected()
    {
        Assert.False(Probe(Cat(Soi, Sof(0xC1, 640, 480, precision: 12)), out _, out _));
    }

    [Theory]
    [InlineData((byte)0xC4)]
    [InlineData((byte)0xC8)]
    [InlineData((byte)0xCC)]
    public void TryReadLossyFrame_TableOrReservedSegmentInsideTheSofRange_IsSkipped(byte marker)
    {
        Assert.True(Probe(Cat(Soi, Segment(marker, 20), Sof()), out var w, out _));
        Assert.Equal(640, w);
    }

    [Fact]
    public void TryReadLossyFrame_SegmentLongerThanTwoFiftySixBytes_IsSkippedWithItsFullLength()
    {
        Assert.True(Probe(Cat(Soi, Segment(0xE0, 300), Sof()), out var w, out _));
        Assert.Equal(640, w);
    }

    [Fact]
    public void TryReadLossyFrame_EmptySegmentBeforeTheFrame_IsSkipped()
    {
        Assert.True(Probe(Cat(Soi, [0xFF, 0xE0, 0x00, 0x02], Sof()), out var w, out _));
        Assert.Equal(640, w);
    }

    [Fact]
    public void TryReadLossyFrame_SegmentLengthBelowTwo_EndsTheWalk()
    {
        Assert.False(Probe(Cat(Soi, [0xFF, 0xE0, 0x00, 0x01], Sof()), out _, out _));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0xDA)]
    [InlineData((byte)0xD9)]
    public void TryReadLossyFrame_EntropyDataOrEndMarkerBeforeTheFrame_EndsTheWalk(byte marker)
    {
        Assert.False(Probe(Cat(Soi, [0xFF, marker, 0x00, 0x02], Sof()), out _, out _));
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0xD0)]
    [InlineData((byte)0xD4)]
    [InlineData((byte)0xD8)]
    public void TryReadLossyFrame_StandaloneMarkerBeforeTheFrame_IsSkippedWithoutALength(byte marker)
    {
        Assert.True(Probe(Cat(Soi, [0xFF, marker], Sof()), out var w, out _));
        Assert.Equal(640, w);
    }

    [Theory]
    [InlineData(511, true)]
    [InlineData(512, false)]
    public void TryReadLossyFrame_FrameHeaderAfterManySegments_IsFoundOnlyWithinTheSegmentCap(int segments, bool expected)
    {
        var parts = new List<byte[]> { Soi };
        for (int i = 0; i < segments; i++) parts.Add([0xFF, 0xE0, 0x00, 0x02]);
        parts.Add(Sof());

        Assert.Equal(expected, Probe(Cat([.. parts]), out _, out _));
    }

    [Theory]
    [InlineData(65_536, true)]
    [InlineData(65_537, false)]
    public void TryReadLossyFrame_RunOfFillBytesBeforeTheFrame_IsToleratedUpToTheCap(int fillBytes, bool expected)
    {
        var fill = new byte[fillBytes];
        Array.Fill(fill, (byte)0xFF);

        Assert.Equal(expected, Probe(Cat(Soi, fill, Sof()), out _, out _));
    }

    [Fact]
    public void TryReadLossyFrame_NoSoi_IsRejected()
    {
        Assert.False(Probe(Cat([0x00, 0x00], Sof()), out _, out _));
    }

    [Fact]
    public void TryReadLossyFrame_SourceThatFailsWithInvalidData_ReturnsFalseAndZeroSize()
    {
        var source = new ThrowingSource(new InvalidDataException("budget exhausted"));

        Assert.False(JpegMarkerProbe.TryReadLossyFrame(source, 0, 64, out var w, out var h));
        Assert.Equal((0, 0), (w, h));
    }

    [Fact]
    public void TryReadLossyFrame_SourceThatFailsWithAnIoError_Propagates()
    {
        var source = new ThrowingSource(new InvalidDataException("read failed", new IOException("disk")));

        Assert.Throws<InvalidDataException>(() => JpegMarkerProbe.TryReadLossyFrame(source, 0, 64, out _, out _));
    }

    private sealed class ThrowingSource(Exception exception) : IRawHeaderSource
    {
        public long Length => 64;

        public ReadOnlySpan<byte> Read(long offset, int count) => throw exception;
    }
}