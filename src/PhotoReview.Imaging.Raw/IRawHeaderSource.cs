namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Source providing random-access window reads into the container header without reading the whole file.
/// </summary>
public interface IRawHeaderSource
{
    /// <summary>Total byte length of the underlying source.</summary>
    long Length { get; }

    /// <summary>
    /// Reads <paramref name="count"/> bytes starting at <paramref name="offset"/>.
    /// Implementations may buffer block-aligned segments and must enforce <see cref="RawContainerLimits.MaxHeaderBytes"/>.
    /// </summary>
    ReadOnlySpan<byte> Read(long offset, int count);
}
