using System.IO;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

[Trait("Category", "Native")]
public sealed class Cr3AndRafCorpusTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    public void AllCorpusCr3AndRafSamples_CanBeParsed()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var registry = new RawContainerReaderRegistry();
        var files = Directory.GetFiles(CorpusDir, "*.*");
        int parsedCount = 0;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".cr3" or ".raf"))
                continue;

            using var fs = File.OpenRead(file);
            byte[] header = new byte[64];
            int read = fs.Read(header, 0, header.Length);
            fs.Position = 0;

            var reader = registry.FindReader(header.AsSpan(0, read), ext);
            Assert.NotNull(reader);

            using var headerSource = new SourceRawHeaderSource(fs);
            var info = reader.Read(headerSource, CancellationToken.None);

            Assert.NotNull(info);
            Assert.True(info.Previews.Count > 0, $"File {Path.GetFileName(file)} ({info.Format}) had 0 previews found.");
            Assert.True(info.Previews[0].Length > 0);
            parsedCount++;
        }

        // 2 CR3 + 3 RAF = 5 samples
        if (!RawCorpus.RequireNonEmpty(parsedCount, "CR3/RAF")) return;
        if (RawCorpus.IsStrict) Assert.True(parsedCount >= 5, $"Expected at least 5 CR3/RAF corpus files, parsed {parsedCount}");
    }
}
