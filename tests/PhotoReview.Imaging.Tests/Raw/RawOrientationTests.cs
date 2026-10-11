using System.IO;
using System.Windows.Media.Imaging;
using PhotoReview.Core.Abstractions;
using PhotoReview.Imaging.Raw;
using PhotoReview.Imaging.Tests.Fixtures;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>
/// RAF and RW2 keep their orientation in EXIF (RAF: only inside the embedded JPEG; RW2: IFD0 0x0112 or the embedded
/// JPEG). The container readers must report it, otherwise a portrait shot is decoded and measured sideways.
/// </summary>
public sealed class RawOrientationTests
{
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 480;

    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    private static byte[] OrientedJpeg(ushort orientation)
    {
        using var temp = new TempRoot("raw-orientation");
        var path = FixtureGenerator.GenerateJpegWithOrientation(temp.Combine("preview.jpg"), PreviewWidth, PreviewHeight, orientation);
        return File.ReadAllBytes(path);
    }

    private static RawContainerInfo ReadContainer(byte[] file, string extension)
    {
        var registry = new RawContainerReaderRegistry();
        var reader = registry.FindReader(file.AsSpan(0, Math.Min(64, file.Length)), extension);
        Assert.NotNull(reader);
        return reader.Read(new InMemoryRawHeaderSource(file), CancellationToken.None);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void RafReader_EmbeddedJpegExifOrientation_IsReported(ushort orientation)
    {
        var raf = SyntheticRawBuilder.BuildRaf(OrientedJpeg(orientation));

        Assert.Equal(orientation, ReadContainer(raf, ".raf").Orientation);
    }

    [Fact]
    public void RafReader_JpegWithoutExif_DefaultsToNormalOrientation()
    {
        var raf = SyntheticRawBuilder.BuildRaf(SyntheticRawBuilder.CreateMinimalJpeg(640, 480));

        Assert.Equal(1, ReadContainer(raf, ".raf").Orientation);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(8)]
    public void Rw2Reader_Ifd0Orientation_IsReported(ushort orientation)
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(SyntheticRawBuilder.CreateMinimalJpeg(640, 480), ifd0Orientation: orientation);

        Assert.Equal(orientation, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void Rw2Reader_NoIfd0Orientation_FallsBackToEmbeddedJpegExif(ushort orientation)
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(OrientedJpeg(orientation));

        Assert.Equal(orientation, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Fact]
    public void Rw2Reader_Ifd0OrientationWinsOverEmbeddedJpegExif()
    {
        var rw2 = SyntheticRawBuilder.BuildRw2(OrientedJpeg(8), ifd0Orientation: 6);

        Assert.Equal(6, ReadContainer(rw2, ".rw2").Orientation);
    }

    [Theory]
    [InlineData(".raf", 6, true)]
    [InlineData(".raf", 8, true)]
    [InlineData(".raf", 1, false)]
    [InlineData(".rw2", 6, true)]
    [InlineData(".rw2", 8, true)]
    [InlineData(".rw2", 1, false)]
    public void RawDecoder_PortraitRafAndRw2_ReadInfoAndDecodeAgreeOnOrientation(string extension, ushort orientation, bool expectTransposed)
    {
        var jpeg = OrientedJpeg(orientation);
        var file = extension == ".raf" ? SyntheticRawBuilder.BuildRaf(jpeg) : SyntheticRawBuilder.BuildRw2(jpeg);
        using var temp = new TempRoot("raw-orientation-decode");
        var path = temp.File("portrait" + extension, file);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder());

        var info = decoder.ReadInfo(path);
        var decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));

        var expectedWidth = expectTransposed ? PreviewHeight : PreviewWidth;
        var expectedHeight = expectTransposed ? PreviewWidth : PreviewHeight;
        Assert.Equal(orientation, info.Orientation);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
        Assert.Equal(orientation, decoded.Orientation);
        Assert.Equal(expectedWidth, decoded.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        Assert.Equal(expectedWidth, decoded.OriginalWidth);
        Assert.Equal(expectedHeight, decoded.OriginalHeight);
    }

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        if (!RawCorpus.RequireDirectory()) return data;
        foreach (var file in Directory.GetFiles(CorpusDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (PhotoReview.Core.Catalog.ImageFileTypes.RawExtensions.Contains(Path.GetExtension(file)))
                data.Add(Path.GetFileName(file));
        }
        RawCorpus.RequireNonEmpty(data.Count, "RAW");
        return data;
    }

    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RawDecoder_CorpusFile_DecodedDimensionsHonorReadInfoOrientation(string fileName)
    {
        var path = Path.Combine(CorpusDir, fileName);
        var decoder = new RawDecoder(new WpfBitmapImageDecoder());
        var info = decoder.ReadInfo(path);
        Assert.True(info.Width > 0 && info.Height > 0, $"{fileName}: ReadInfo returned {info.Width}x{info.Height}");

        IDecodedImage decoded;
        try
        {
            decoded = decoder.Decode(new DecodeRequest(path, DecodeBox.Unbounded));
        }
        catch (NotSupportedException)
        {
            return; // preview codec unsupported by the inner WPF decoder (needs the LibRaw thumbnail fallback)
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("No embedded preview", StringComparison.Ordinal))
        {
            return; // RAW-only DNG (no JPEG preview): decoded by the full-RAW path, covered by LibRaw tests
        }

        Assert.Equal(info.Orientation, decoded.Orientation);
        // The embedded preview may be a differently cropped/scaled view of the sensor, but its landscape/portrait
        // shape must match the orientation-adjusted shape ReadInfo reports.
        // Square shapes are compared too: a 0x0 (unknown) ReadInfo or a square-vs-rectangular mismatch must fail.
        Assert.Equal(Math.Sign(info.Width - info.Height), Math.Sign(decoded.PixelWidth - decoded.PixelHeight));
    }

    /// <summary>
    /// ReadInfo must never report an unknown size for a real file, whatever the format. Fuji RAF used to yield 0x0
    /// because its embedded JPEG frame header sits past the first 64 KiB. For RAF the size is compared with WIC's own
    /// reading of the embedded JPEG header (independent oracle); other formats must agree with the container's sensor size.
    /// </summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RawDecoder_CorpusFile_ReadInfoReportsPositiveDimensionsMatchingTheContainer(string fileName)
    {
        var path = Path.Combine(CorpusDir, fileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var info = new RawDecoder(new WpfBitmapImageDecoder()).ReadInfo(path);

        Assert.True(info.Width > 0 && info.Height > 0, $"{fileName}: ReadInfo returned {info.Width}x{info.Height}");

        using var stream = File.OpenRead(path);
        using var source = new SourceRawHeaderSource(stream);
        var probe = new byte[64];
        stream.ReadExactly(probe);
        var container = new RawContainerReaderRegistry().FindReader(probe, extension)!.Read(source, CancellationToken.None);
        var transposed = ExifOrientation.IsTransposed(container.Orientation);
        if (container.SensorWidth > 0 && container.SensorHeight > 0)
        {
            Assert.Equal(transposed ? container.SensorHeight : container.SensorWidth, info.Width);
            Assert.Equal(transposed ? container.SensorWidth : container.SensorHeight, info.Height);
            return;
        }

        // No sensor size in the container (RAF): the embedded JPEG frame is the size.
        var preview = Assert.Single(container.Previews);
        using var jpeg = new MemoryStream(ReadRange(stream, preview.Offset, preview.Length), writable: false);
        var frame = BitmapDecoder.Create(jpeg, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        Assert.Equal(transposed ? frame.PixelHeight : frame.PixelWidth, info.Width);
        Assert.Equal(transposed ? frame.PixelWidth : frame.PixelHeight, info.Height);
    }

    private static byte[] ReadRange(Stream stream, long offset, long length)
    {
        var bytes = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RafAndRw2CorpusFile_ContainerOrientationMatchesEmbeddedJpegExif(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".raf" or ".rw2")) return;

        var bytes = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var info = ReadContainer(bytes, extension);
        var preview = Assert.Single(info.Previews);
        using var stream = new MemoryStream(bytes, checked((int)preview.Offset), checked((int)preview.Length), writable: false);
        BitmapFrame frame;
        try
        {
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return; // WIC cannot open this camera's preview JPEG, so there is no independent oracle for it
        }

        Assert.Equal(WpfExifOrientation.Read(frame.Metadata as BitmapMetadata), info.Orientation);
    }

    /// <summary>
    /// Every corpus orientation is 1 (the default), so the test above cannot fail when orientation reading breaks. Here the
    /// real orientation carrier is rewritten in an in-memory copy: the embedded JPEG EXIF (RAF), IFD0 0x0112 (RW2).
    /// </summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RafAndRw2CorpusFile_RewrittenOrientation_IsReportedByContainerReader(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".raf" or ".rw2")) return;

        var original = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var originalInfo = ReadContainer(original, extension);
        foreach (ushort orientation in new ushort[] { 6, 8, 3 })
        {
            var rewritten = (byte[])original.Clone();
            Assert.True(RawOrientationPatcher.TryWriteOrientation(rewritten, extension, originalInfo, orientation), $"{fileName}: no orientation to rewrite");

            Assert.Equal(orientation, ReadContainer(rewritten, extension).Orientation);
        }
    }

    /// <summary>The RAF orientation lives only in the embedded JPEG: independent WIC oracle over the rewritten JPEG.</summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RafCorpusFile_RewrittenEmbeddedJpegOrientation_MatchesWicAndReader(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".raf", StringComparison.OrdinalIgnoreCase)) return;

        var original = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var originalInfo = ReadContainer(original, ".raf");
        var rewritten = (byte[])original.Clone();
        Assert.True(RawOrientationPatcher.TryWriteOrientation(rewritten, ".raf", originalInfo, 8));
        var preview = Assert.Single(originalInfo.Previews);
        using var stream = new MemoryStream(rewritten, checked((int)preview.Offset), checked((int)preview.Length), writable: false);
        BitmapFrame frame;
        try
        {
            frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return; // WIC cannot open this camera preview JPEG, so there is no independent oracle for it
        }

        Assert.Equal(8, WpfExifOrientation.Read(frame.Metadata as BitmapMetadata));
        Assert.Equal(8, ReadContainer(rewritten, ".raf").Orientation);
    }

    /// <summary>
    /// RW2 without IFD0 Orientation must fall back to the embedded JPEG EXIF: hide the real IFD0 tag (renamed to an
    /// unknown tag id) and rewrite the JPEG orientation, both on the real file.
    /// </summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Rw2CorpusFile_WithoutIfd0Orientation_FallsBackToEmbeddedJpegExif(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".rw2", StringComparison.OrdinalIgnoreCase)) return;

        var original = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var originalInfo = ReadContainer(original, ".rw2");
        var file = (byte[])original.Clone();
        Assert.True(RawOrientationPatcher.TryRenameIfd0Tag(file, 0, 0x0112, 0xFFF0), $"{fileName}: expected an IFD0 orientation tag to hide");
        Assert.True(RawOrientationPatcher.TryWriteOrientation(file, ".rw2", originalInfo, 6), $"{fileName}: no embedded JPEG EXIF orientation to rewrite");

        Assert.Equal(6, ReadContainer(file, ".rw2").Orientation);
    }

    /// <summary>
    /// The corpus holds only landscape shots (orientation 1). Rewriting the real orientation tag of every corpus file to 6
    /// (portrait) turns each real container into a portrait sample: every reader must then report 6, ReadInfo must swap
    /// the visual size and the decoded preview must come out transposed relative to the landscape original.
    /// </summary>
    [Trait("Category", "Native")]
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RawDecoder_CorpusFileRewrittenToPortrait_ReadInfoAndDecodeAreTransposed(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var original = File.ReadAllBytes(Path.Combine(CorpusDir, fileName));
        var originalInfo = ReadContainer(original, extension);
        if (originalInfo.Previews.Count == 0) return; // RAW-only DNG: no embedded preview to orient

        var portrait = (byte[])original.Clone();
        Assert.True(RawOrientationPatcher.TryWriteOrientation(portrait, extension, originalInfo, 6), $"{fileName} has no orientation tag to rewrite");

        Assert.Equal(1, originalInfo.Orientation);
        Assert.Equal(6, ReadContainer(portrait, extension).Orientation);

        var landscapeDecoder = new RawDecoder(new WpfBitmapImageDecoder(new InMemorySourceReader(original)), new InMemorySourceReader(original));
        var portraitDecoder = new RawDecoder(new WpfBitmapImageDecoder(new InMemorySourceReader(portrait)), new InMemorySourceReader(portrait));
        var landscapeInfo = landscapeDecoder.ReadInfo("landscape" + extension);
        var portraitInfo = portraitDecoder.ReadInfo("portrait" + extension);
        Assert.Equal(6, portraitInfo.Orientation);
        Assert.True(landscapeInfo.Width > 0 && landscapeInfo.Height > 0, $"{fileName}: ReadInfo returned {landscapeInfo.Width}x{landscapeInfo.Height}");
        Assert.Equal(landscapeInfo.Height, portraitInfo.Width);
        Assert.Equal(landscapeInfo.Width, portraitInfo.Height);

        IDecodedImage landscapeImage;
        IDecodedImage portraitImage;
        try
        {
            landscapeImage = landscapeDecoder.Decode(new DecodeRequest("landscape" + extension, DecodeBox.Unbounded));
            portraitImage = portraitDecoder.Decode(new DecodeRequest("portrait" + extension, DecodeBox.Unbounded));
        }
        catch (NotSupportedException)
        {
            return; // preview codec unsupported by the inner WPF decoder (needs the LibRaw thumbnail fallback)
        }

        Assert.Equal(6, portraitImage.Orientation);
        Assert.Equal(landscapeImage.PixelHeight, portraitImage.PixelWidth);
        Assert.Equal(landscapeImage.PixelWidth, portraitImage.PixelHeight);
    }

    private sealed class InMemorySourceReader(byte[] bytes) : ISourceReader
    {
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
            new MemoryStream(bytes, writable: false);
    }
}
