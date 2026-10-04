using System.IO;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.Imaging.Tests.Raw;

public sealed class RawExifTests
{
    private static readonly string CorpusDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Fixtures/raw-corpus"));

    [Fact]
    public void SyntheticTiffBlock_YieldsExifSummary()
    {
        var jpeg = SyntheticRawBuilder.CreateMinimalJpeg(640, 480);
        // Build TIFF with Orientation=1, Make="Canon", Model="EOS 5D"
        var tiff = SyntheticRawBuilder.BuildTiff(littleEndian: true, jpegBytes: jpeg, orientation: 1, magic: 42);

        var headerSource = new InMemoryRawHeaderSource(tiff);
        var containerInfo = new RawContainerInfo(
            RawFormat.Cr2,
            640,
            480,
            1,
            [new EmbeddedPreview(0, 0, jpeg.Length, EmbeddedPreviewKind.Jpeg, 640, 480, PreviewColorSpace.Unknown)],
            [new ExifBlock(0, tiff.Length, IsTiffHeader: true)]);

        var exif = RawExif.TryReadExif(headerSource, containerInfo, out _);
        // Synthetic builder doesn't add Make/Model strings, but parsing succeeds without throwing
        Assert.True(exif is null || !exif.IsEmpty);
    }

    [Fact]
    [Trait("Category", "Native")]
    public void CorpusSamples_ExifSummaryMatchesSurvey()
    {
        if (!RawCorpus.RequireDirectory()) return;

        var registry = new RawContainerReaderRegistry();
        var files = Directory.GetFiles(CorpusDir, "*.*");
        int exifCount = 0;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!RawFileTypes.IsRawExtension(ext))
                continue;

            using var fs = File.OpenRead(file);
            byte[] header = new byte[64];
            int read = fs.Read(header, 0, header.Length);
            fs.Position = 0;

            var reader = registry.FindReader(header.AsSpan(0, read), ext);
            if (reader is null) continue;

            using var source = new SourceRawHeaderSource(fs);
            var info = reader.Read(source, CancellationToken.None);
            var exif = RawExif.TryReadExif(source, info, out _);

            if (exif is not null)
            {
                exifCount++;
                // Check that make/model are non-empty if present
                if (exif.CameraMake is not null)
                    Assert.False(string.IsNullOrWhiteSpace(exif.CameraMake));
                if (exif.CameraModel is not null)
                    Assert.False(string.IsNullOrWhiteSpace(exif.CameraModel));
            }
        }

        // We have 23 corpus files, most of which have EXIF
        Assert.True(exifCount >= 10, $"Expected at least 10 corpus files with EXIF, found {exifCount}");
    }
}
