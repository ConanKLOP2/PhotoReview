using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// <see cref="IRawHeaderSource"/> implementation over <see cref="ISourceReader"/>.
/// Reads 64 KB-aligned blocks on demand, caches read blocks for the lifetime of the header source,
/// and enforces <see cref="RawContainerLimits.MaxHeaderBytes"/> to fail closed on hostile/circular structures.
/// </summary>
public sealed class SourceRawHeaderSource : IRawHeaderSource, IDisposable
{
    public const int BlockSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly long _length;
    private readonly Dictionary<long, byte[]> _blockCache = [];
    private byte[]? _multiBlockBuffer;
    private long _totalBytesRead;

    public SourceRawHeaderSource(string path, ISourceReader sourceReader, SourceReadPriority priority = SourceReadPriority.Viewer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sourceReader);

        _stream = sourceReader.OpenSource(path, priority, BlockSize);
        _length = _stream.Length;
    }

    public SourceRawHeaderSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _length = stream.Length;
    }

    /// <inheritdoc />
    public long Length => _length;

    /// <summary>Total bytes read from the underlying stream (exposed for test verification).</summary>
    public long TotalBytesRead => _totalBytesRead;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Read(long offset, int count)
    {
        if (offset < 0 || count < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset and count must be non-negative.");

        // offset > Length - count is the overflow-free form of offset + count > Length.
        if (offset > _length - count)
            throw new InvalidDataException($"Attempted to read past end of stream (offset: {offset}, count: {count}, length: {_length}).");

        // Reject before allocating the multi-block buffer: no read may exceed the total header budget.
        if (count > RawContainerLimits.MaxHeaderBytes)
            throw new InvalidDataException(
                $"RAW header read of {count} bytes exceeds hard limit of {RawContainerLimits.MaxHeaderBytes} bytes.");

        if (count == 0)
            return ReadOnlySpan<byte>.Empty;

        long startBlockIndex = offset / BlockSize;
        long endBlockIndex = (offset + count - 1) / BlockSize;

        // Fast path: single block
        if (startBlockIndex == endBlockIndex)
        {
            var block = GetOrReadBlock(startBlockIndex);
            int blockOffset = (int)(offset % BlockSize);
            return new ReadOnlySpan<byte>(block, blockOffset, count);
        }

        // Multi-block span: allocate or resize buffer and copy segments
        if (_multiBlockBuffer == null || _multiBlockBuffer.Length < count)
        {
            _multiBlockBuffer = new byte[count];
        }

        int destOffset = 0;
        long remaining = count;
        long currOffset = offset;

        for (long b = startBlockIndex; b <= endBlockIndex; b++)
        {
            var block = GetOrReadBlock(b);
            int inBlockOffset = (int)(currOffset % BlockSize);
            int bytesFromBlock = (int)Math.Min(remaining, block.Length - inBlockOffset);

            Array.Copy(block, inBlockOffset, _multiBlockBuffer, destOffset, bytesFromBlock);
            destOffset += bytesFromBlock;
            currOffset += bytesFromBlock;
            remaining -= bytesFromBlock;
        }

        return new ReadOnlySpan<byte>(_multiBlockBuffer, 0, count);
    }

    private byte[] GetOrReadBlock(long blockIndex)
    {
        if (_blockCache.TryGetValue(blockIndex, out var cached))
            return cached;

        long blockStartOffset = blockIndex * BlockSize;
        int bytesToRead = (int)Math.Min(BlockSize, _length - blockStartOffset);

        if (bytesToRead > RawContainerLimits.MaxHeaderBytes - _totalBytesRead)
        {
            throw new InvalidDataException(
                $"RAW header read exceeded hard limit of {RawContainerLimits.MaxHeaderBytes} bytes.");
        }

        var block = new byte[bytesToRead];
        _stream.Position = blockStartOffset;
        int totalRead = 0;
        while (totalRead < bytesToRead)
        {
            int read = _stream.Read(block, totalRead, bytesToRead - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        if (totalRead != bytesToRead)
        {
            throw new InvalidDataException($"Unexpected EOF while reading block at {blockStartOffset}.");
        }

        _totalBytesRead += bytesToRead;
        _blockCache[blockIndex] = block;
        return block;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _blockCache.Clear();
    }
}
