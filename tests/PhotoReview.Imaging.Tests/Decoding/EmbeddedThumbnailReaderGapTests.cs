using System.IO;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>Mutation-gap tests for <see cref="EmbeddedThumbnailReader"/>: size acceptance boundaries and shared-open behaviour.</summary>
public sealed class EmbeddedThumbnailReaderGapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoReview-ThumbGap-" + Guid.NewGuid().ToString("N"));

    public EmbeddedThumbnailReaderGapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory(DisplayName = "IsAcceptableSize accepts positive sizes up to exactly MaxThumbnailPixels and rejects the rest")]
    [InlineData(160, 120, true)]
    [InlineData(1, 1, true)]
    [InlineData(2048, 2048, true)]          // exactly 4 MP
    [InlineData(2048, 2049, false)]         // one row over
    [InlineData(4194304, 1, true)]          // exactly 4 MP, extreme aspect
    [InlineData(4194305, 1, false)]
    [InlineData(0, 100, false)]
    [InlineData(100, 0, false)]
    [InlineData(-1, 100, false)]
    [InlineData(100, -1, false)]
    [InlineData(-5, -5, false)]             // product positive but both negative
    [InlineData(0, 0, false)]
    [InlineData(100000, 100000, false)]     // product above int range, w/h == 1 would pass a division mutant
    public void IsAcceptableSize_Boundaries(int width, int height, bool expected) =>
        Assert.Equal(expected, EmbeddedThumbnailReader.IsAcceptableSize(width, height));

    [Fact(DisplayName = "TryRead works while another handle holds the file open for write and delete sharing")]
    public void TryRead_SharedWriterOpen_StillReads()
    {
        var path = Path.Combine(_dir, "shared.jpg");
        File.WriteAllBytes(path, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 480, thumbnailSize: 160));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var image = EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance);

        Assert.NotNull(image);
        Assert.Equal(160, image.PixelWidth);
    }

    [Fact(DisplayName = "TryRead works while another handle holds the file open for write only sharing delete")]
    public void TryRead_WriterWithDeleteShare_StillReads()
    {
        var path = Path.Combine(_dir, "shared2.jpg");
        File.WriteAllBytes(path, EmbeddedThumbnailJpegFixture.CreateWithThumbnail(mainSize: 480, thumbnailSize: 160));

        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.NotNull(EmbeddedThumbnailReader.TryRead(path, WpfBitmapSourceCodec.Instance));
    }
}
