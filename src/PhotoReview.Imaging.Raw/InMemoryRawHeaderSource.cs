using System.IO;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// In-memory <see cref="IRawHeaderSource"/> backed by a <see cref="ReadOnlyMemory{Byte}"/> buffer.
/// Useful for unit testing container readers and parsing synthetic RAW headers entirely in RAM.
/// </summary>
public sealed class InMemoryRawHeaderSource : IRawHeaderSource
{
    private readonly ReadOnlyMemory<byte> _memory;
    private long _totalBytesRead;

    public InMemoryRawHeaderSource(ReadOnlyMemory<byte> memory)
    {
        _memory = memory;
    }

    public InMemoryRawHeaderSource(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _memory = bytes;
    }

    /// <inheritdoc />
    public long Length => _memory.Length;

    /// <summary>Total bytes read from this source.</summary>
    public long TotalBytesRead => _totalBytesRead;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Read(long offset, int count)
    {
        if (offset < 0 || count < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count must be non-negative.");

        // offset > Length - count is the overflow-free form of offset + count > Length.
        if (offset > _memory.Length - count)
            throw new InvalidDataException($"Attempted to read past end of memory (offset: {offset}, count: {count}, length: {_memory.Length}).");

        if (count > RawContainerLimits.MaxHeaderBytes - _totalBytesRead)
        {
            throw new InvalidDataException($"Header read exceeded hard limit of {RawContainerLimits.MaxHeaderBytes} bytes.");
        }

        _totalBytesRead += count;
        return _memory.Span.Slice((int)offset, count);
    }
}
