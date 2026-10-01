using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// Single-strip JPEG previews (StripOffsets/StripByteCounts, Compression 6/7): the JPEG's own frame size wins over a
/// declared IFD size, and a 64-bit strip length above int.MaxValue must neither wrap negative nor escape as a non-corrupt exception.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffStripPreviewTests
{
    private const int StripAt = 1000;

    public static TheoryData<string> Readers => new() { "cr2", "nef", "dng" };

    private static IRawContainerReader NewReader(string kind) => kind switch
    {
        "cr2" => new Cr2ContainerReader(),
        "nef" => new NefContainerReader(),
        _ => new DngContainerReader(),
    };

    /// <summary>IFD0 declaring 9000x6000, Compression 7 and one strip at <see cref="StripAt"/> holding <paramref name="jpeg"/>.</summary>
    private static byte[] Build(byte[] jpeg, uint declaredStripLength)
    {
        var file = new TiffBytes(true, 3000).Header(8)
            .Ifd(8, 0, Short(0x0100, 9000), Short(0x0101, 6000), Short(0x0103, 7), Long(0x0111, StripAt), Long(0x0117, declaredStripLength));
        file.Put(StripAt, jpeg);
        return file.ToArray();
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_StripPreviewWhoseDeclaredSizeContradictsTheJpegFrame_UsesTheFrameSize(string kind)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);

        var info = NewReader(kind).Read(new InMemoryRawHeaderSource(Build(jpeg, (uint)jpeg.Length)), CancellationToken.None);

        var preview = Assert.Single(info.Previews, p => p.Offset == StripAt);
        Assert.Equal(320, preview.Width);
        Assert.Equal(240, preview.Height);
    }

    [Fact]
    public void Read_Cr2StripLengthAboveIntMaxValue_DoesNotWrapNegativeOrThrowANonCorruptException()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var data = Build(jpeg, 3_000_000_000u);
        var source = new HugeFileSource(new InMemoryRawHeaderSource(data), length: 3_500_000_000L);

        // Before the fix (int)stripLength wrapped to a negative count and Read threw ArgumentOutOfRangeException.
        var info = new Cr2ContainerReader().Read(source, CancellationToken.None);

        var preview = Assert.Single(info.Previews, p => p.Offset == StripAt);
        Assert.Equal(3_000_000_000L, preview.Length);
    }

    /// <summary>Reports a larger file than the bytes backing it (the strip itself is never read, only its first bytes).</summary>
    private sealed class HugeFileSource(IRawHeaderSource inner, long length) : IRawHeaderSource
    {
        public long Length { get; } = length;

        public ReadOnlySpan<byte> Read(long offset, int count) => inner.Read(offset, count);
    }
}
