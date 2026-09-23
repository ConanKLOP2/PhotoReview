using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Wraps a <see cref="ReadOnlyMemory{Byte}"/> in a non-owning, read-only <see cref="MemoryStream"/>
/// without copying. <see cref="SourceBytesCache"/> and the DIAG_PREREAD path always hand decoders a
/// memory region backed by a plain array starting at offset 0, so this is expected to hit the fast
/// path; the copy fallback only exists for memory shapes decoders don't currently produce (e.g. a
/// slice), so a future caller can't silently regress into an unbounded copy going unnoticed.
/// </summary>
internal static class ReadOnlyMemoryStreamFactory
{
    public static MemoryStream Create(ReadOnlyMemory<byte> bytes) =>
        MemoryMarshal.TryGetArray(bytes, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);
}
