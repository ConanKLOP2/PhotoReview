using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.App.Composition;

/// <summary>Adapts the optional LibRaw thumbnail extractor to the RAW container preview contract.</summary>
internal sealed class LibRawPreviewFallback : IRawPreviewFallback
{
    public ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format)
    {
        if (format != RawFormat.Orf)
            throw new NotSupportedException($"LibRaw thumbnail fallback is not enabled for {format}.");

        return LibRawDecoder.ReadJpegThumbnail(path);
    }
}
