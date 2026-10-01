using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// JpegMarkerProbe (DNG/NEF strip previews): fill bytes do not use up the segment budget and only 8-bit lossy frames
/// count. DNG: many SubIFDs must not stop the IFD1+ chain early.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class JpegProbeAndDngChainTests
{
    private const int StripAt = 1000;

    /// <summary>DNG whose IFD0 is a compression-7 single-strip image holding <paramref name="jpeg"/>.</summary>
    private static RawContainerInfo ReadStripDng(byte[] jpeg)
    {
        var file = new TiffBytes(true, StripAt + jpeg.Length + 16).Header(8)
            .Ifd(8, 0,
                Long(0x00FE, 0), Short(0x0100, 320), Short(0x0101, 240), Short(0x0103, 7), Short(0x0106, 2),
                Long(0x0111, StripAt), Long(0x0117, (uint)jpeg.Length))
            .Put(StripAt, jpeg);
        return new DngContainerReader().Read(new InMemoryRawHeaderSource(file.ToArray()), CancellationToken.None);
    }

    private static byte[] WithFillBytes(byte[] jpeg, int count) =>
        [.. jpeg[..2], .. Enumerable.Repeat((byte)0xFF, count), .. jpeg[2..]];

    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    [InlineData(5000)]
    public void StripJpeg_WithFillBytesBeforeMarker_IsStillAPreview(int fillBytes)
    {
        var info = ReadStripDng(WithFillBytes(SyntheticRawBuilder.CreateMinimalJpeg(320, 240), fillBytes));

        Assert.Single(info.Previews, p => p.Offset == StripAt);
    }

    [Fact]
    public void StripJpeg_With12BitPrecision_IsNotAPreview()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        int sof = Array.FindIndex(jpeg, i => i == 0xC0) - 1; // FF C0
        Assert.Equal(0x08, jpeg[sof + 4]);
        jpeg[sof + 4] = 12;

        var info = ReadStripDng(jpeg);

        Assert.DoesNotContain(info.Previews, p => p.Offset == StripAt);
    }

    [Fact]
    public void PreviewSelector_FillBytesBeforeFrameHeader_StillResolvesDimensions()
    {
        var jpeg = WithFillBytes(SyntheticRawBuilder.CreateMinimalJpeg(320, 240), 600);
        var preview = new EmbeddedPreview(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 0, 0, PreviewColorSpace.Unknown);

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), [preview], DecodeBox.Unbounded, 1);

        Assert.Equal(320, chosen!.Width);
        Assert.Equal(240, chosen.Height);
    }

    [Fact]
    public void Dng_SixtyFourSubIfds_DoNotStopTheIfdChainEarly()
    {
        const int subCount = 64;
        var file = new TiffBytes(true, 4000).Header(8)
            .Ifd(8, 2000, At(0x014A, 4, subCount, 1000))
            .Ifd(2000, 2100, Short(0x0100, 100), Short(0x0101, 80))
            .Ifd(2100, 0, Long(0x00FE, 0), Short(0x0100, 4000), Short(0x0101, 3000), Short(0x0106, 32803));
        for (int i = 0; i < subCount; i++)
        {
            file.U32(1000 + (i * 4), (uint)(2200 + (i * 8)));
            file.Ifd(2200 + (i * 8), 0);
        }

        var info = new DngContainerReader().Read(new InMemoryRawHeaderSource(file.ToArray()), CancellationToken.None);

        Assert.Equal((4000, 3000), (info.SensorWidth, info.SensorHeight));
    }
}
