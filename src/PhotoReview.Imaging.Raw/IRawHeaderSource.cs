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
    /// Failures surface as <see cref="System.IO.InvalidDataException"/> (range outside the source, exhausted budget, truncated or
    /// unreadable data; an I/O failure is the inner exception), so callers that catch only that type stay total;
    /// <see cref="OperationCanceledException"/> is never wrapped.
    /// The returned span is valid only until the next <see cref="Read"/> on the same source: a read spanning several
    /// blocks is assembled in a shared buffer that the next multi-block read reuses, so copy the bytes you need to keep.
    /// </summary>
    ReadOnlySpan<byte> Read(long offset, int count);
}
