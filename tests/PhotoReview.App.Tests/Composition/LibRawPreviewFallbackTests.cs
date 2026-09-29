using System;
using System.IO;
using PhotoReview.App.Composition;
using PhotoReview.Imaging.Raw;
using Xunit;

namespace PhotoReview.App.Tests.Composition;

public sealed class LibRawPreviewFallbackTests
{
    [Theory]
    [InlineData(RawFormat.Cr2)]
    [InlineData(RawFormat.Cr3)]
    [InlineData(RawFormat.Nef)]
    [InlineData(RawFormat.Nrw)]
    [InlineData(RawFormat.Arw)]
    [InlineData(RawFormat.Dng)]
    [InlineData(RawFormat.Raf)]
    [InlineData(RawFormat.Rw2)]
    [InlineData(RawFormat.Pef)]
    [InlineData(RawFormat.Unknown)]
    public void ReadJpegThumbnail_NonOrfFormat_ThrowsNotSupportedWithoutOpeningTheFile(RawFormat format)
    {
        var error = Assert.Throws<NotSupportedException>(() =>
            new LibRawPreviewFallback().ReadJpegThumbnail(@"Z:\does\not\exist.raw", format));

        Assert.Contains(format.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadJpegThumbnail_BlankPathForOrf_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new LibRawPreviewFallback().ReadJpegThumbnail(" ", RawFormat.Orf));
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ReadJpegThumbnail_OrfCorpusFile_ReturnsCompleteJpeg()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tests/Fixtures/raw-corpus/Olympus - E-P3 - 16bit (4_3).ORF"));
        if (!File.Exists(path)) return;

        var thumbnail = new LibRawPreviewFallback().ReadJpegThumbnail(path, RawFormat.Orf);

        Assert.True(thumbnail.Length > 4);
        Assert.Equal((byte)0xFF, thumbnail.Span[0]);
        Assert.Equal((byte)0xD8, thumbnail.Span[1]);
        Assert.Equal((byte)0xD9, thumbnail.Span[^1]);
    }

    [Fact]
    [Trait("Category", "Native")]
    public void ReadJpegThumbnail_MissingOrfFile_ThrowsInvalidDataFromLibRaw()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".orf");

        Assert.Throws<InvalidDataException>(() => new LibRawPreviewFallback().ReadJpegThumbnail(missing, RawFormat.Orf));
    }
}
