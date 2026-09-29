using System.IO;
using PhotoReview.Imaging.Raw;
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
        if (!Directory.Exists(CorpusDir)) return;

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

        Assert.True(parsedCount >= 18, $"Expected at least 18 TIFF corpus files, parsed {parsedCount}");
    }
}
