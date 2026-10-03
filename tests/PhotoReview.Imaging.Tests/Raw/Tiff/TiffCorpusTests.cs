using System.IO;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Raw.Tiff;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw.Tiff;

[Trait("Category", "Native")]
public sealed class TiffCorpusTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    public void AllCorpusTiffSamples_CanBeParsed()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var registry = new RawContainerReaderRegistry();
        var files = Directory.GetFiles(CorpusDir, "*.*");
        int parsedCount = 0;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".cr2" or ".nef" or ".arw" or ".dng" or ".orf" or ".rw2"))
                continue;

            using var fs = File.OpenRead(file);
            byte[] header = new byte[64];
            int read = fs.Read(header, 0, header.Length);
            fs.Position = 0;

            var reader = registry.FindReader(header.AsSpan(0, read), ext);
            Assert.NotNull(reader);

            using var headerSource = new SourceRawHeaderSource(fs);
            var info = reader.Read(headerSource, CancellationToken.None);

            // RAW survey: "Phone DNGs may have no JPEG preview (only raw) -> full decode only".
            // Leica M8 DNG also has no separate JPEG preview in IFD.
            if (info.Format == RawFormat.Dng && info.Previews.Count == 0)
            {
                Assert.True(info.SensorWidth > 0 || info.Previews.Count > 0, $"File {Path.GetFileName(file)} ({info.Format}) had no previews and no sensor dimensions.");
            }
            else
            {
                Assert.True(info.Previews.Count > 0, $"File {Path.GetFileName(file)} ({info.Format}) had 0 previews found.");
                Assert.True(info.Previews[0].Length > 0);
            }
            parsedCount++;
        }

        // The full-corpus size is only demanded in strict mode: a partial local corpus (fetch-raw-samples -FormatFilter/-Limit) just skips.
        if (!RawCorpus.RequireNonEmpty(parsedCount, "TIFF-based")) return;
        if (RawCorpus.IsStrict) Assert.True(parsedCount >= 18, $"Expected at least 18 TIFF corpus files, parsed {parsedCount}");
    }

    // ------------------------------------------------------------------ corpus helpers

    private static IEnumerable<string> CorpusFiles(params string[] extensions)
    {
        return RawCorpus.Files(extensions);
    }

    private static RawContainerInfo ReadContainer(string file)
    {
        var registry = new RawContainerReaderRegistry();
        using var fs = File.OpenRead(file);
        byte[] header = new byte[64];
        int read = fs.Read(header, 0, header.Length);
        fs.Position = 0;

        var reader = registry.FindReader(header.AsSpan(0, read), Path.GetExtension(file));
        Assert.NotNull(reader);
        using var headerSource = new SourceRawHeaderSource(fs);
        return reader.Read(headerSource, CancellationToken.None);
    }

    private static byte[] ReadRange(string file, long offset, long length)
    {
        using var fs = File.OpenRead(file);
        Assert.True(offset >= 0 && length > 0 && offset + length <= fs.Length, $"{Path.GetFileName(file)}: range {offset}+{length} outside file of {fs.Length} bytes.");
        fs.Position = offset;
        byte[] bytes = new byte[length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    private static void AssertLossyJpeg(string file, EmbeddedPreview preview)
    {
        byte[] bytes = ReadRange(file, preview.Offset, preview.Length);
        Assert.True(bytes[0] == 0xFF && bytes[1] == 0xD8, $"{Path.GetFileName(file)}: preview at {preview.Offset} does not start with SOI.");
        Assert.True(
            PreviewSelector.TryReadJpegFrame(new InMemoryRawHeaderSource(bytes), 0, bytes.Length, out int width, out int height, out _) && width > 0 && height > 0,
            $"{Path.GetFileName(file)}: preview at {preview.Offset} has no SOF0/1/2 frame (lossless or corrupt).");
    }

    // ------------------------------------------------------------------ DNG

    [Fact]
    public void AllDngCorpusFiles_EveryPreviewIsLossyJpegAndSelectedPreviewStartsWithSoi()
    {
        foreach (var file in CorpusFiles(".dng"))
        {
            var info = ReadContainer(file);
            foreach (var preview in info.Previews)
                AssertLossyJpeg(file, preview);

            using var fs = File.OpenRead(file);
            using var source = new SourceRawHeaderSource(fs);
            var chosen = PreviewSelector.SelectPreview(source, info.Previews, DecodeBox.Unbounded, info.Orientation);
            if (chosen is not null)
                AssertLossyJpeg(file, chosen);
        }
    }

    [Fact]
    public void PentaxK7DngCorpus_LossyPreviewIsChosenOverLosslessSensorStripAndCropSetsSensorSize()
    {
        var file = RawCorpus.TryGetFirst("*.dng", "K-7");
        if (file is null) return;

        var info = ReadContainer(file);
        using var fs = File.OpenRead(file);
        using var source = new SourceRawHeaderSource(fs);
        var chosen = PreviewSelector.SelectPreview(source, info.Previews, DecodeBox.Unbounded, info.Orientation);

        Assert.NotNull(chosen);
        Assert.Equal(1_252_117, chosen.Length); // the SubIFD preview, not the 19 MB lossless sensor strip
        Assert.Equal((4672, 3104), (info.SensorWidth, info.SensorHeight));
    }

    // ------------------------------------------------------------------ NEF

    [Theory]
    // The raw SubIFD (3904x2616, 7424x4924, 8288x5520) includes masked margins; the active area is the full-size JpgFromRaw frame.
    [InlineData("D40X", 3872, 2592, 13508)]
    [InlineData("D800", 7360, 4912, 25336)]
    [InlineData("Z 7", 8256, 5504, 76188)]
    public void NefCorpus_SensorSizeIsActiveAreaOfFullSizeJpegAndMakerNotePreviewIsListed(string model, int width, int height, long makerNotePreviewOffset)
    {
        var file = RawCorpus.TryGetFirst("*.nef", model);
        if (file is null) return;

        var info = ReadContainer(file);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
        Assert.Contains(info.Previews, p => p.Offset == makerNotePreviewOffset);
        foreach (var preview in info.Previews)
            AssertLossyJpeg(file, preview);
    }

    // ------------------------------------------------------------------ ARW

    // Raw IFD sizes (6048x4024, 7392x4920, 4928x3276) include masked margins; the active area comes from
    // DefaultCropSize (A7M3) or the Exif PixelXDimension/PixelYDimension (A7R, NEX-6).
    [Theory]
    [InlineData("ILCE-7M3", 6000, 4000)]
    [InlineData("ILCE-7R", 7360, 4912)]
    [InlineData("NEX-6", 4912, 3264)]
    public void ArwCorpus_SensorSizeIsActiveAreaNotMaskedRawIfd(string model, int width, int height)
    {
        var file = RawCorpus.TryGetFirst("*.arw", model);
        if (file is null) return;

        var info = ReadContainer(file);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
    }

    // ------------------------------------------------------------------ ORF

    [Fact]
    public void AllOrfCorpusFiles_MakerNotePreviewIsWholeJpegLocatedRelativeToMakerNote()
    {
        // Olympus CameraSettings PreviewImageStart is relative to the MakerNote start; all three bodies place the JPEG at 52224.
        var expectedLengths = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["OM-1"] = 962_481,
            ["E-M1"] = 922_386,
            ["E-P3"] = 1_137_515,
        };

        int checkedFiles = 0;
        foreach (var file in CorpusFiles(".orf"))
        {
            var info = ReadContainer(file);
            var expected = expectedLengths.First(kv => Path.GetFileName(file).Contains(kv.Key, StringComparison.Ordinal));

            var preview = Assert.Single(info.Previews, p => p.Offset == 52224);
            Assert.Equal(expected.Value, preview.Length);

            byte[] bytes = ReadRange(file, preview.Offset, preview.Length);
            Assert.Equal(new byte[] { 0xFF, 0xD8 }, bytes[..2]);
            Assert.Equal(new byte[] { 0xFF, 0xD9 }, bytes[^2..]);

            using var stream = new MemoryStream(bytes);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.True(frame.PixelWidth > 0 && frame.PixelHeight > 0);

            foreach (var other in info.Previews)
                AssertLossyJpeg(file, other);
            checkedFiles++;
        }

        if (RawCorpus.IsStrict && Directory.Exists(CorpusDir))
            Assert.Equal(3, checkedFiles); // the full corpus has three ORF samples; a partial local corpus is not an error
    }

    // ------------------------------------------------------------------ CR2 / RW2

    [Theory]
    [InlineData("350D", 3456, 2304)]
    [InlineData("5D Mark IV", 6720, 4480)]
    [InlineData("sRAW", 2592, 1728)]
    public void Cr2Corpus_SensorSizeIsTheImageSizeNotTheIfd0PreviewSize(string model, int width, int height)
    {
        var file = RawCorpus.TryGetFirst("*.cr2", model);
        if (file is null) return;

        var info = ReadContainer(file);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
    }

    [Theory]
    [InlineData("GH5", 5184, 3888)]
    [InlineData("DC-S1", 6000, 4000)]
    [InlineData("GF1", 4000, 3000)]
    public void Rw2Corpus_SensorSizeExcludesMaskedBorders(string model, int width, int height)
    {
        var file = RawCorpus.TryGetFirst("*.rw2", model);
        if (file is null) return;

        var info = ReadContainer(file);

        Assert.Equal((width, height), (info.SensorWidth, info.SensorHeight));
    }

    // ------------------------------------------------------------------ all TIFF-family files

    [Fact]
    public void AllTiffCorpusFiles_EveryListedPreviewLiesInsideFileAndStartsWithSoi()
    {
        foreach (var file in CorpusFiles(".cr2", ".nef", ".arw", ".dng", ".orf", ".rw2"))
        {
            var info = ReadContainer(file);
            foreach (var preview in info.Previews)
            {
                byte[] head = ReadRange(file, preview.Offset, Math.Min(preview.Length, 2));
                Assert.True(head is [0xFF, 0xD8], $"{Path.GetFileName(file)}: preview at {preview.Offset} does not start with SOI.");
            }
        }
    }
}