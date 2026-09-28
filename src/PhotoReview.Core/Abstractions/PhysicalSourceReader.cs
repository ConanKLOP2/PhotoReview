using System.IO;

namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Default (production) <see cref="ISourceReader"/>: opens the real file, ignoring
/// <see cref="SourceReadPriority"/> entirely. Byte-for-byte the <see cref="FileStream"/> construction
/// every call site used before the seam existed (see <see cref="ISourceReader"/>'s own doc comment).
/// Stateless and thread-safe, so one process-wide instance is shared by every consumer that does not
/// have its own DI-supplied reader (e.g. a decoder constructed directly by a test).
/// </summary>
public sealed class PhysicalSourceReader : ISourceReader
{
    public static readonly PhysicalSourceReader Instance = new();

    public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024) =>
        new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
}
