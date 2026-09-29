namespace PhotoReview.Imaging.Raw;

/// <summary>Provides an alternate encoded JPEG preview when a supported RAW container's embedded preview cannot be decoded.</summary>
public interface IRawPreviewFallback
{
    /// <summary>
    /// Reads a bounded JPEG thumbnail from the source for the specified RAW format. The returned payload length is
    /// an estimate of thumbnail source bytes consumed; a native provider may perform additional metadata reads.
    /// </summary>
    ReadOnlyMemory<byte> ReadJpegThumbnail(string path, RawFormat format);
}
