using System.IO;
using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Robustness;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Hostile-input behavior of the header sources, <see cref="TiffStructure"/> readers, <see cref="RawExif"/> and
/// <see cref="PreviewSelector"/>: offsets near the integer limits, blocks claiming to extend past EOF, and reads that
/// exceed the header budget must fail cleanly (InvalidDataException) or be ignored, never wrap or throw other types.
/// </summary>
public sealed class RawHeaderHostileInputTests
{
    // ---------------------------------------------------------------- header sources

    [Theory]
    [InlineData(long.MaxValue, 1)]
    [InlineData(long.MaxValue - 1, 4)]
    [InlineData(long.MaxValue - 3, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    public void InMemoryRawHeaderSource_Read_OffsetPlusCountWrapping_ThrowsInvalidData(long offset, int count)
    {
        var source = new InMemoryRawHeaderSource(new byte[64]);

        Assert.Throws<InvalidDataException>(() => source.Read(offset, count));
    }

    [Theory]
    [InlineData(long.MaxValue, 1)]
    [InlineData(long.MaxValue - 1, 4)]
    [InlineData(long.MaxValue - 3, int.MaxValue)]
    [InlineData(1, int.MaxValue)]
    public void SourceRawHeaderSource_Read_OffsetPlusCountWrapping_ThrowsInvalidData(long offset, int count)
    {
        using var source = new SourceRawHeaderSource(new MemoryStream(new byte[64]));

        Assert.Throws<InvalidDataException>(() => source.Read(offset, count));
    }

    [Fact]
    public void SourceRawHeaderSource_Read_CountAboveBudget_ThrowsBeforeAllocatingTheBuffer()
    {
        // 1 GiB of virtual zeros: the length check passes, so only the budget check can reject the read.
        using var source = new SourceRawHeaderSource(new ZeroStream(1L << 30));
        int count = 64 * 1024 * 1024;

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => source.Read(0, count));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < count / 4, $"Allocated {allocated} bytes before rejecting a {count}-byte read.");
        Assert.Equal(0, source.TotalBytesRead);
    }

    [Fact]
    public void SourceRawHeaderSource_Read_ExactlyMaxHeaderBytes_IsNotRejectedUpFront()
    {
        using var source = new SourceRawHeaderSource(new ZeroStream(1L << 30));

        var span = source.Read(0, RawContainerLimits.MaxHeaderBytes);

        Assert.Equal(RawContainerLimits.MaxHeaderBytes, span.Length);
    }

    [Fact]
    public void InMemoryRawHeaderSource_Read_NewBlocksBeyondBudget_ThrowInvalidData()
    {
        var source = new InMemoryRawHeaderSource(new byte[RawContainerLimits.MaxHeaderBytes + SourceRawHeaderSource.BlockSize]);

        source.Read(0, RawContainerLimits.MaxHeaderBytes);

        Assert.Throws<InvalidDataException>(() => source.Read(RawContainerLimits.MaxHeaderBytes, 1));
    }

    [Fact]
    public void InMemoryRawHeaderSource_Read_AlreadyTouchedBlocks_AreFreeLikeTheProductionSource()
    {
        var source = new InMemoryRawHeaderSource(new byte[RawContainerLimits.MaxHeaderBytes]);

        source.Read(0, RawContainerLimits.MaxHeaderBytes);
        source.Read(0, 1);
        source.Read(RawContainerLimits.MaxHeaderBytes - 10, 10);

        Assert.Equal(RawContainerLimits.MaxHeaderBytes, source.TotalBytesRead);
    }

    [Fact]
    public void InMemoryRawHeaderSource_TotalBytesRead_MatchesSourceRawHeaderSourceForTheSameReads()
    {
        var bytes = new byte[300_000];
        var memory = new InMemoryRawHeaderSource(bytes);
        using var stream = new SourceRawHeaderSource(new MemoryStream(bytes));

        foreach (var (offset, count) in new[] { (0L, 8), (100L, 4), (70_000L, 10), (65_530L, 20), (299_990L, 10) })
        {
            memory.Read(offset, count);
            stream.Read(offset, count);
        }

        Assert.Equal(stream.TotalBytesRead, memory.TotalBytesRead);
    }

    // ---------------------------------------------------------------- TiffStructure

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MaxValue - 3)]
    [InlineData(8)]
    public void TiffStructure_ReadU16_OffsetNearOrPastEnd_ReturnsZero(int offset)
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Equal(0, TiffStructure.ReadU16(data, offset, littleEndian: true));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MaxValue - 2)]
    [InlineData(int.MaxValue - 3)]
    [InlineData(5)]
    public void TiffStructure_ReadU32_OffsetNearOrPastEnd_ReturnsZero(int offset)
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Equal(0u, TiffStructure.ReadU32(data, offset, littleEndian: false));
    }

    [Fact]
    public void TiffStructure_ReadU16AndU32_LastValidOffsets_StillRead()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Equal(0x0708, TiffStructure.ReadU16(data, 6, littleEndian: false));
        Assert.Equal(0x08070605u, TiffStructure.ReadU32(data, 4, littleEndian: true));
    }

    // ---------------------------------------------------------------- RawExif

    [Fact]
    public void TryReadExif_TiffBlockClaimingToExtendPastEof_ParsesTheAvailableBytes()
    {
        var tiff = MakeTiff("CamA");
        var source = new InMemoryRawHeaderSource(tiff);
        var info = InfoWith(new ExifBlock(0, tiff.Length + 100_000, IsTiffHeader: true));

        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("CamA", exif.CameraMake);
    }

    [Fact]
    public void TryReadExif_BlockStartingNearEofAndOverlong_ReturnsNullWithoutThrowing()
    {
        var source = new InMemoryRawHeaderSource(new byte[100]);
        var info = InfoWith(new ExifBlock(96, 5_000, IsTiffHeader: true), new ExifBlock(99, long.MaxValue, IsTiffHeader: false));

        Assert.Null(RawExif.TryReadExif(source, info));
    }

    [Fact]
    public void TryReadExif_SourceThrowsInvalidData_ReturnsNull()
    {
        var info = InfoWith(new ExifBlock(0, 64, IsTiffHeader: true));

        Assert.Null(RawExif.TryReadExif(new ThrowingSource(), info));
    }

    [Fact]
    public void TryReadExif_FailureOnFirstBlock_StillTriesLaterBlocks()
    {
        var tiff = MakeTiff("CamB");
        var source = new FailFirstReadSource(new InMemoryRawHeaderSource(tiff));
        var info = InfoWith(new ExifBlock(0, tiff.Length, IsTiffHeader: true), new ExifBlock(0, tiff.Length, IsTiffHeader: true));

        var exif = RawExif.TryReadExif(source, info);

        Assert.NotNull(exif);
        Assert.Equal("CamB", exif.CameraMake);
    }

    // ---------------------------------------------------------------- PreviewSelector

    [Fact]
    public void SelectPreview_UnknownSizePreviewClaimingToExtendPastEof_ResolvesFromAvailableBytes()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        var source = new InMemoryRawHeaderSource(jpeg);
        var preview = new EmbeddedPreview(0, 0, jpeg.Length + 1_000_000, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown);

        var selected = PreviewSelector.SelectPreview(source, [preview], DecodeBox.Unbounded, orientation: 1);

        Assert.NotNull(selected);
        Assert.Equal((640, 480), (selected.Width, selected.Height));
    }

    [Fact]
    public void SelectPreview_UnknownSizePreviewStartingNearEofAndOverlong_DoesNotThrow()
    {
        var source = new InMemoryRawHeaderSource(new byte[100]);
        var preview = new EmbeddedPreview(0, 98, 10_000, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown);

        var selected = PreviewSelector.SelectPreview(source, [preview], DecodeBox.Unbounded, orientation: 1);

        Assert.Equal(preview, selected);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Minimal TIFF whose IFD0 holds a 3-character Make (inline ASCII), enough for a non-empty summary.</summary>
    private static byte[] MakeTiff(string make) =>
        JpegBytes.Tiff(little: true, (0x010F, 2, 4, System.Text.Encoding.ASCII.GetBytes(make + "\0")));

    private static RawContainerInfo InfoWith(params ExifBlock[] blocks) =>
        new(RawFormat.Cr2, 0, 0, 1, [], blocks);

    private sealed class ThrowingSource : IRawHeaderSource
    {
        public long Length => 1024;

        public ReadOnlySpan<byte> Read(long offset, int count) =>
            throw new InvalidDataException("Header read exceeded hard limit.");
    }

    private sealed class FailFirstReadSource(IRawHeaderSource inner) : IRawHeaderSource
    {
        private bool _failed;

        public long Length => inner.Length;

        public ReadOnlySpan<byte> Read(long offset, int count)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidDataException("Simulated failure.");
            }

            return inner.Read(offset, count);
        }
    }

    /// <summary>Read-only seekable stream of <see cref="Length"/> zero bytes that allocates nothing.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, Math.Max(0, length - Position));
            Array.Clear(buffer, offset, n);
            Position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => length + offset,
            };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}