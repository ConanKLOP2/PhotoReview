using System.IO;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// In-memory <see cref="IRawHeaderSource"/> backed by a <see cref="ReadOnlyMemory{Byte}"/> buffer.
/// Useful for unit testing container readers and parsing synthetic RAW headers entirely in RAM.
/// Budget semantics match <see cref="SourceRawHeaderSource"/> exactly: the header budget
/// (<see cref="RawContainerLimits.MaxHeaderBytes"/>) is charged once per distinct
/// <see cref="SourceRawHeaderSource.BlockSize"/>-byte block (the last block counts only the bytes it holds), so re-reading
/// bytes of an already-touched block is free and a test sees the same exhaustion behaviour as production.
/// </summary>
public sealed class InMemoryRawHeaderSource : IRawHeaderSource
{
    private const int BlockSize = SourceRawHeaderSource.BlockSize;

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly HashSet<long> _touchedBlocks = [];
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

    /// <summary>Bytes charged against the header budget so far (whole distinct 64 KB blocks, like <see cref="SourceRawHeaderSource.TotalBytesRead"/>).</summary>
    public long TotalBytesRead => _totalBytesRead;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Read(long offset, int count)
    {
        if (offset < 0 || count < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count must be non-negative.");

        // offset > Length - count is the overflow-free form of offset + count > Length.
        if (offset > _memory.Length - count)
            throw new InvalidDataException($"Attempted to read past end of memory (offset: {offset}, count: {count}, length: {_memory.Length}).");

        if (count > RawContainerLimits.MaxHeaderBytes)
            throw new InvalidDataException(
                $"RAW header read of {count} bytes exceeds hard limit of {RawContainerLimits.MaxHeaderBytes} bytes.");

        if (count == 0)
            return ReadOnlySpan<byte>.Empty;

        long startBlock = offset / BlockSize;
        long endBlock = (offset + count - 1) / BlockSize;

        // Charge the not-yet-touched blocks first (all or nothing, so a rejected read leaves the accounting unchanged).
        long newBytes = 0;
        for (long block = startBlock; block <= endBlock; block++)
        {
            if (!_touchedBlocks.Contains(block))
                newBytes += Math.Min(BlockSize, _memory.Length - (block * BlockSize));
        }

        if (newBytes > RawContainerLimits.MaxHeaderBytes - _totalBytesRead)
            throw new InvalidDataException($"RAW header read exceeded hard limit of {RawContainerLimits.MaxHeaderBytes} bytes.");

        for (long block = startBlock; block <= endBlock; block++)
            _touchedBlocks.Add(block);
        _totalBytesRead += newBytes;

        return _memory.Span.Slice((int)offset, count);
    }
}
