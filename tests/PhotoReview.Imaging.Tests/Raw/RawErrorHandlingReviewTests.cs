using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw.Tiff;
using PhotoReview.Imaging.Tests.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Error-handling review fixes: I/O failures are not swallowed as "corrupt header", single EXIF block for CR2, gate/resampler guards.</summary>
public sealed class RawErrorHandlingReviewTests
{
    /// <summary>Every read fails; <paramref name="ioFailure"/> chooses whether the InvalidDataException wraps an IOException (disk error) or not (budget/corrupt).</summary>
    private sealed class FailingSource(bool ioFailure) : IRawHeaderSource
    {
        public long Length => 1_000_000;

        public ReadOnlySpan<byte> Read(long offset, int count) =>
            throw (ioFailure ? new InvalidDataException("read failed", new IOException("disk")) : new InvalidDataException("budget exhausted"));
    }

    [Fact]
    public void ComputeExifBlock_IoFailure_Propagates()
    {
        var ex = Assert.Throws<InvalidDataException>(() => TiffHeaderNavigator.ComputeExifBlock(new FailingSource(true), true, 8));
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void ComputeExifBlock_NonIoFailure_KeepsDefaultBlock()
    {
        var block = TiffHeaderNavigator.ComputeExifBlock(new FailingSource(false), true, 8);
        Assert.Equal(RawContainerLimits.DefaultExifBlockBytes, block.Length);
    }

    private static EmbeddedPreview Jpeg(int width, int height) =>
        new(0, 1000, 5000, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown);

    [Theory]
    [InlineData(0, 0)]       // ResolveDimensions path
    [InlineData(4000, 3000)] // ResolveColorSpace path (size already known)
    public void SelectPreview_IoFailureWhileWalkingJpegHeader_Propagates(int width, int height)
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            PreviewSelector.SelectPreview(new FailingSource(true), [Jpeg(width, height)], DecodeBox.Unbounded, 1));
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4000, 3000)]
    public void SelectPreview_NonIoFailureWhileWalkingJpegHeader_StaysUnresolved(int width, int height)
    {
        var chosen = PreviewSelector.SelectPreview(new FailingSource(false), [Jpeg(width, height)], DecodeBox.Unbounded, 1, out var resolved);

        Assert.NotNull(chosen);
        Assert.False(resolved[0].HeaderResolved);
    }

    [Fact]
    public void Cr2_ManyIfdsWithExifPointer_YieldsOneExifBlock()
    {
        var tiff = new TiffBytes(littleEndian: true, 4096).Header(8);
        tiff.Ifd(8, 200, Short(0x0100, 4000), Short(0x0101, 3000), Long(0x8769, 1000));
        tiff.Ifd(200, 400, Short(0x0100, 100), Short(0x0101, 100), Long(0x8769, 1000));
        tiff.Ifd(400, 0, Short(0x0100, 50), Short(0x0101, 50), Long(0x8769, 1000));
        tiff.Ifd(1000, 0, Short(0x8827, 400));

        var info = new Cr2ContainerReader().Read(new InMemoryRawHeaderSource(tiff.ToArray()), CancellationToken.None);

        Assert.Single(info.ExifBlocks);
    }

    [Fact]
    public void FullDecodeGate_WaiterQueuedSeamThrows_DoesNotOrphanWaiterOrStickBusy()
    {
        var gate = new FullDecodeGate(2, _ => throw new InvalidOperationException("seam"));
        var holder = gate.Enter(SourceReadPriority.Viewer, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => gate.Enter(SourceReadPriority.Viewer, CancellationToken.None));
        Assert.Equal(0, gate.QueuedViewers);

        holder.Dispose();
        Assert.Equal(1, gate.SlotsAvailable);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void ResizeToBuffer_NonPositiveTarget_ThrowsInvalidData(int targetWidth, int targetHeight)
    {
        var rgb = new byte[10 * 10 * 3];
        Assert.Throws<InvalidDataException>(() =>
            RgbBgraResampler.ResizeToBuffer(rgb, 10, 10, targetWidth, targetHeight, 3, CancellationToken.None));
    }

    [Fact]
    public void ResizeToBuffer_NonPositiveSource_ThrowsInvalidData() =>
        Assert.Throws<InvalidDataException>(() =>
            RgbBgraResampler.ResizeToBuffer(new byte[3], 0, 1, 1, 1, 3, CancellationToken.None));
}
