using System.IO;
using PhotoReview.Imaging.Raw.Tiff;
using static PhotoReview.Imaging.Tests.Raw.Tiff.TiffBytes;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

/// <summary>
/// JPEGInterchangeFormat (0x0201/0x0202) previews must start with SOI, and a declared IFD size that the JPEG's own frame
/// header contradicts must not survive (a bogus pointer with a bogus size used to win "largest preview"); the Exif pixel
/// size helper must not leave a half-set size behind.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TiffPreviewHardeningTests
{
    private const int JpegAt = 1000;

    public static TheoryData<string> Readers => new() { "cr2", "nef", "arw", "dng", "orf" };

    private static IRawContainerReader NewReader(string kind) => kind switch
    {
        "cr2" => new Cr2ContainerReader(),
        "nef" => new NefContainerReader(),
        "arw" => new ArwContainerReader(),
        "dng" => new DngContainerReader(),
        _ => new OrfContainerReader(),
    };

    /// <summary>IFD0 (orientation only) -> IFD at 100 declaring 9000x6000 with a JPEG pointer at <see cref="JpegAt"/>.</summary>
    private static byte[] Build(string kind, byte[]? jpeg)
    {
        ReadOnlySpan<byte> signature = kind == "orf" ? "IIRO"u8 : default;
        var file = new TiffBytes(true, 3000).Header(8, signature)
            .Ifd(8, 100, Short(0x0112, 1))
            .Ifd(100, 0, Short(0x0100, 9000), Short(0x0101, 6000), Long(0x0201, JpegAt), Long(0x0202, (uint)(jpeg?.Length ?? 200)));
        if (jpeg is not null) file.Put(JpegAt, jpeg);
        return file.ToArray();
    }

    private static RawContainerInfo Read(string kind, byte[] data) =>
        NewReader(kind).Read(new InMemoryRawHeaderSource(data), CancellationToken.None);

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_JpegInterchangePointerToZeroPadding_IsNotAPreview(string kind)
    {
        var info = Read(kind, Build(kind, jpeg: null));

        Assert.DoesNotContain(info.Previews, p => p.Offset == JpegAt);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_DeclaredSizeContradictingTheJpegFrame_UsesTheFrameSize(string kind)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        var info = Read(kind, Build(kind, jpeg));

        var preview = Assert.Single(info.Previews, p => p.Offset == JpegAt);
        Assert.Equal(320, preview.Width);
        Assert.Equal(240, preview.Height);
    }

    /// <summary>
    /// A JPEG whose APP segments sit one 64 KB block apart: walking to the frame header charges more distinct blocks than the
    /// 8 MiB header budget allows, so the walker throws <see cref="InvalidDataException"/> before it reaches the SOF.
    /// </summary>
    private static byte[] BudgetExhaustingJpeg()
    {
        const int Segments = 140;
        var tail = SyntheticRawBuilder.CreateMinimalJpeg(320, 240);
        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8]);
        var app = new byte[2 + 65535];
        app[0] = 0xFF;
        app[1] = 0xE0;
        app[2] = 0xFF;
        app[3] = 0xFF;
        for (int i = 0; i < Segments; i++) ms.Write(app);
        ms.Write(tail, 2, tail.Length - 2);
        return ms.ToArray();
    }

    private static byte[] BuildWithJpeg(string kind, byte[] jpeg)
    {
        ReadOnlySpan<byte> signature = kind == "orf" ? "IIRO"u8 : default;
        var file = new TiffBytes(true, JpegAt + jpeg.Length).Header(8, signature)
            .Ifd(8, 100, Short(0x0112, 1))
            .Ifd(100, 0, Short(0x0100, 9000), Short(0x0101, 6000), Long(0x0201, JpegAt), Long(0x0202, (uint)jpeg.Length));
        file.Put(JpegAt, jpeg);
        return file.ToArray();
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Read_PreviewJpegExhaustingHeaderBudget_ContainerStillParsesAndKeepsDeclaredSize(string kind)
    {
        var info = Read(kind, BuildWithJpeg(kind, BudgetExhaustingJpeg()));

        var preview = Assert.Single(info.Previews, p => p.Offset == JpegAt);
        Assert.True(preview.Width is 0 or 9000, $"{kind}: width {preview.Width}");
        Assert.True(preview.Height is 0 or 6000, $"{kind}: height {preview.Height}");
    }

    [Fact]
    public void TryReadLossyFrame_JpegExhaustingHeaderBudget_ReturnsFalseInsteadOfThrowing()
    {
        var jpeg = BudgetExhaustingJpeg();
        var source = new InMemoryRawHeaderSource(jpeg);

        Assert.False(JpegMarkerProbe.TryReadLossyFrame(source, 0, jpeg.Length, out _, out _));
    }

    [Fact]
    public void Read_NefFullSizeJpegFrameWalkExhaustingHeaderBudget_ContainerStillParses()
    {
        // IFD0 carries an unsized JPEG preview; the next IFD is the 9000x6000 raw image, so the reader walks the preview to
        // compare its frame with the raw size - and that walk runs out of header budget.
        var jpeg = BudgetExhaustingJpeg();
        var file = new TiffBytes(true, JpegAt + jpeg.Length).Header(8)
            .Ifd(8, 100, Short(0x0112, 1), Long(0x0201, JpegAt), Long(0x0202, (uint)jpeg.Length))
            .Ifd(100, 0, Short(0x0100, 9000), Short(0x0101, 6000));
        file.Put(JpegAt, jpeg);

        var info = new NefContainerReader().Read(new InMemoryRawHeaderSource(file.ToArray()), CancellationToken.None);

        Assert.Equal((9000, 6000), (info.SensorWidth, info.SensorHeight));
    }

    [Fact]
    public void TryReadExifPixelDimensions_WidthOnly_ReturnsFalseAndZeroOutputs()
    {
        var file = new TiffBytes(true, 200).Header(8).Ifd(8, 0, Long(0xA002, 5000));

        bool ok = TiffHeaderNavigator.TryReadExifPixelDimensions(new InMemoryRawHeaderSource(file.ToArray()), 8, true, out int w, out int h);

        Assert.False(ok);
        Assert.Equal(0, w);
        Assert.Equal(0, h);
    }

    /// <summary>IFD0 has a width-only Exif IFD; the next IFD is the raw image (6048x4024) whose Exif IFD holds 6000x4000.</summary>
    private static byte[] BuildWidthOnlyThenComplete() =>
        new TiffBytes(true, 1000).Header(8)
            .Ifd(8, 100, Short(0x0112, 1), Long(0x8769, 300))
            .Ifd(100, 0, Short(0x0100, 6048), Short(0x0101, 4024), Long(0x8769, 400))
            .Ifd(300, 0, Long(0xA002, 5000))
            .Ifd(400, 0, Long(0xA002, 6000), Long(0xA003, 4000))
            .ToArray();

    [Theory]
    [InlineData("nef")]
    [InlineData("arw")]
    public void Read_WidthOnlyExifIfdFirst_DoesNotBlockALaterCompleteExifSize(string kind)
    {
        var info = Read(kind, BuildWidthOnlyThenComplete());

        Assert.Equal(6000, info.SensorWidth);
        Assert.Equal(4000, info.SensorHeight);
    }
}
