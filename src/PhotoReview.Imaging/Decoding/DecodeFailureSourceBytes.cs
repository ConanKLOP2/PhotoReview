namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Hands the source bytes a primary decoder already read from disk to <see cref="FallbackImageDecoder"/> when that
/// decoder gives up (e.g. TurboJpeg on an ICC/CMYK/12-bit JPEG), so the fallback decodes from memory instead of
/// opening and reading the file a second time. Carried in <see cref="Exception.Data"/> so no exception type changes.
/// </summary>
public static class DecodeFailureSourceBytes
{
    private const string DataKey = "PhotoReview.Imaging.SourceBytes";

    /// <summary>Attaches <paramref name="bytes"/> (the whole source file) to <paramref name="exception"/>.</summary>
    public static void Attach(Exception exception, ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(exception);
        exception.Data[DataKey] = bytes;
    }

    /// <summary>True when a decoder attached the source bytes it read before failing.</summary>
    public static bool TryGet(Exception exception, out ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Data[DataKey] is ReadOnlyMemory<byte> attached)
        {
            bytes = attached;
            return true;
        }
        bytes = default;
        return false;
    }
}
