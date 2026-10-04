namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Parser for a specific camera RAW container format family.
/// </summary>
public interface IRawContainerReader
{
    /// <summary>Returns true if the first 64 bytes and file extension match this reader.</summary>
    bool CanRead(ReadOnlySpan<byte> first64Bytes, string extension);

    /// <summary>
    /// Reads container info (dimensions, previews, EXIF blocks) through bounded header reads.
    /// Throws <see cref="System.IO.InvalidDataException"/> if the container is corrupt or hostile.
    /// </summary>
    RawContainerInfo Read(IRawHeaderSource source, CancellationToken ct);
}
