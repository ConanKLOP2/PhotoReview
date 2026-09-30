using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// Adobe RGB detection (C1/C2): the EXIF interoperability marker must be found even when the container already gives the
/// preview size, and only the InteropIndex "R03" IFD entry counts (not stray bytes in MakerNote).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreviewSelectorColorSpaceTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    /// <summary>EXIF APP1 payload ("Exif\0\0" + TIFF): IFD0 -&gt; Exif IFD -&gt; Interop IFD with InteropIndex = <paramref name="interopIndex"/>.</summary>
    internal static byte[] ExifWithInteropIndex(string interopIndex, bool littleEndian = true, byte[]? extraTail = null)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void U16(int v) { if (littleEndian) w.Write((ushort)v); else { w.Write((byte)(v >> 8)); w.Write((byte)v); } }
        void U32(uint v) { if (littleEndian) w.Write(v); else { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); } }
        w.Write("Exif\0\0"u8);
        w.Write(littleEndian ? "II"u8 : "MM"u8);
        U16(42);
        U32(8);
        // IFD0 at 8: one entry, ExifIFD pointer -> 26
        U16(1); U16(0x8769); U16(4); U32(1); U32(26); U32(0);
        // Exif IFD at 26: one entry, Interop pointer -> 44
        U16(1); U16(0xA005); U16(4); U32(1); U32(44); U32(0);
        // Interop IFD at 44: InteropIndex ASCII x4 inline
        U16(1); U16(0x0001); U16(2); U32(4);
        var value = new byte[4];
        System.Text.Encoding.ASCII.GetBytes(interopIndex, 0, Math.Min(3, interopIndex.Length), value, 0);
        w.Write(value);
        U32(0);
        if (extraTail is not null) w.Write(extraTail);
        return ms.ToArray();
    }

    /// <summary>Minimal JPEG with an EXIF APP1 carrying <paramref name="exifPayload"/> right after SOI.</summary>
    internal static byte[] JpegWithExif(byte[] exifPayload, int width = 640, int height = 480)
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(width, height);
        var length = exifPayload.Length + 2;
        using var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)length]);
        ms.Write(exifPayload);
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    private static EmbeddedPreview Declared(byte[] jpeg, int width, int height) =>
        new(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, width, height, PreviewColorSpace.Unknown);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectPreview_DeclaredSizeWithInteropR03_IsAdobeRgb(bool littleEndian)
    {
        var jpeg = JpegWithExif(ExifWithInteropIndex("R03", littleEndian));

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), [Declared(jpeg, 1920, 1280)], DecodeBox.Unbounded, 1);

        Assert.NotNull(chosen);
        Assert.Equal((1920, 1280), (chosen.Width, chosen.Height));
        Assert.Equal(PreviewColorSpace.AdobeRgb, chosen.ColorSpace);
    }

    [Fact]
    public void SelectPreview_DeclaredSizeWithInteropR98_StaysUnknown()
    {
        var jpeg = JpegWithExif(ExifWithInteropIndex("R98"));

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), [Declared(jpeg, 1920, 1280)], DecodeBox.Unbounded, 1);

        Assert.Equal(PreviewColorSpace.Unknown, chosen!.ColorSpace);
    }

    [Fact]
    public void SelectPreview_TwoPreviews_ResolvesTheColourSpaceOfTheChosenOne()
    {
        var adobe = JpegWithExif(ExifWithInteropIndex("R03"), 1620, 1080);
        var small = SyntheticRawBuilder.CreateMinimalJpeg(160, 120);
        var file = new byte[adobe.Length + small.Length];
        adobe.CopyTo(file, 0);
        small.CopyTo(file, adobe.Length);
        EmbeddedPreview[] previews =
        [
            new(0, 0, adobe.Length, EmbeddedPreviewKind.Jpeg, 1620, 1080, PreviewColorSpace.Unknown),
            new(adobe.Length, 0, small.Length, EmbeddedPreviewKind.Jpeg, 160, 120, PreviewColorSpace.Unknown),
        ];

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(file), previews, DecodeBox.Unbounded, 1);

        Assert.Equal(1620, chosen!.Width);
        Assert.Equal(PreviewColorSpace.AdobeRgb, chosen.ColorSpace);
    }

    [Fact]
    public void SelectPreview_DeclaredSize_ReadsOnlyHeadersAndTheExifSegmentNotTheWholeJpeg()
    {
        var jpeg = JpegWithExif(ExifWithInteropIndex("R03"));
        var padded = new byte[jpeg.Length + 200_000];
        jpeg.CopyTo(padded, 0);
        var source = new InMemoryRawHeaderSource(padded);

        _ = PreviewSelector.SelectPreview(source, [new EmbeddedPreview(0, 0, padded.Length, EmbeddedPreviewKind.Jpeg, 1920, 1280, PreviewColorSpace.Unknown)],
            DecodeBox.Unbounded, 1);

        Assert.True(source.TotalBytesRead < 1_000, $"Read {source.TotalBytesRead} bytes.");
    }

    [Fact]
    public void SelectPreview_R03OrAdobeRgbTextOutsideTheInteropEntry_IsNotAdobeRgb()
    {
        // MakerNote-like binary that happens to contain both byte sequences: the old 64 KB substring scan flagged this.
        byte[] noise = [.. "xxR03xx Adobe RGB xx"u8];
        var jpeg = JpegWithExif(ExifWithInteropIndex("R98", extraTail: noise));

        var chosen = PreviewSelector.SelectPreview(new InMemoryRawHeaderSource(jpeg), [Declared(jpeg, 1920, 1280)], DecodeBox.Unbounded, 1);

        Assert.Equal(PreviewColorSpace.Unknown, chosen!.ColorSpace);
    }

    [Fact]
    public void TryReadJpegFrame_InteropR03_IsReportedTogetherWithTheFrame()
    {
        var jpeg = JpegWithExif(ExifWithInteropIndex("R03"), 800, 600);

        Assert.True(PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(jpeg), 0, jpeg.Length, out var w, out var h, out var colorSpace));

        Assert.Equal((800, 600), (w, h));
        Assert.Equal(PreviewColorSpace.AdobeRgb, colorSpace);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x45, 0x78, 0x69, 0x66, 0, 0, 0x49, 0x49, 0x2A, 0, 0xFF, 0xFF, 0xFF, 0x7F })] // IFD0 offset past the payload
    [InlineData(new byte[] { 0x45, 0x78, 0x69, 0x66, 0, 0, 0x4D, 0x4D, 0, 0x2B, 0, 0, 0, 8 })] // wrong TIFF magic
    public void IsAdobeRgbExif_MalformedPayload_ReturnsFalseWithoutThrowing(byte[] payload)
    {
        Assert.False(PreviewSelector.IsAdobeRgbExif(payload));
    }

    [Theory]
    [InlineData("Fujifilm - X-E2S - 14bit 14bit uncompressed (3_2).RAF")]
    [InlineData("Panasonic - DC-GH5 - 1_1.RW2")]
    public void Corpus_KnownAdobeRgbSample_IsDetectedWhetherOrNotTheContainerDeclaresTheSize(string fileName)
    {
        var path = Path.Combine(CorpusDir, fileName);
        if (!File.Exists(path)) return;

        using var source = new SourceRawHeaderSource(path, PhysicalSourceReader.Instance, SourceReadPriority.Viewer);
        var probe = source.Read(0, RawContainerLimits.InitialProbeLength(source.Length));
        var info = new RawContainerReaderRegistry().FindReader(probe, Path.GetExtension(path))!.Read(source, CancellationToken.None);
        var jpeg = info.Previews.Single(p => p.Kind == EmbeddedPreviewKind.Jpeg);

        var undeclared = PreviewSelector.SelectPreview(source, [jpeg with { Width = 0, Height = 0, ColorSpace = PreviewColorSpace.Unknown }], DecodeBox.Unbounded, 1);
        var declared = PreviewSelector.SelectPreview(source, [jpeg with { Width = 1920, Height = 1280, ColorSpace = PreviewColorSpace.Unknown }], DecodeBox.Unbounded, 1);

        Assert.Equal(PreviewColorSpace.AdobeRgb, undeclared!.ColorSpace);
        Assert.Equal(PreviewColorSpace.AdobeRgb, declared!.ColorSpace);
        Assert.Equal((1920, 1280), (declared.Width, declared.Height));
    }

    [Fact]
    public void Corpus_SrgbSamples_AreNotFlaggedAdobeRgb()
    {
        var path = Path.Combine(CorpusDir, "Canon - EOS 5D Mark IV - RAW (3_2).CR2");
        if (!File.Exists(path)) return;

        using var source = new SourceRawHeaderSource(path, PhysicalSourceReader.Instance, SourceReadPriority.Viewer);
        var probe = source.Read(0, RawContainerLimits.InitialProbeLength(source.Length));
        var info = new RawContainerReaderRegistry().FindReader(probe, Path.GetExtension(path))!.Read(source, CancellationToken.None);

        var chosen = PreviewSelector.SelectPreview(source, info.Previews, DecodeBox.Unbounded, info.Orientation);

        Assert.NotEqual(PreviewColorSpace.AdobeRgb, chosen!.ColorSpace);
    }
}
