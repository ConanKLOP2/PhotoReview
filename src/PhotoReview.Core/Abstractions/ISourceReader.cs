
namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Q-R29 option C-2: which lane a source-image byte read belongs to. Carried by the caller so a
/// throttling decorator (perf-harness only, see <see cref="ISourceReader"/>) can tell a foreground
/// read from a background one; the default (pass-through) <see cref="ISourceReader"/> implementation
/// ignores it entirely.
/// </summary>
public enum SourceReadPriority
{
    /// <summary>The photo currently on screen (or about to be): the user is waiting on this read.</summary>
    Viewer,
    /// <summary>Whole-folder preload's own read-ahead: never user-blocking by itself.</summary>
    Preload,
    /// <summary>Anything else that reads source bytes off the interactive path (e.g. compare/hash).</summary>
    Background,
}

/// <summary>
/// Q-R29 option C-2: the single choke point every source-image byte read goes through --
/// <see cref="PhotoReview.Imaging.Caching.SourceBytesCache"/>, the decoders
/// (<c>WicDirectDecoder</c>, <c>WpfBitmapImageDecoder</c>), and <c>PreviewImageService</c>'s
/// diagnostic pre-read all open the source file through this seam instead of their own
/// <see cref="FileStream"/> construction. The default implementation
/// (<see cref="PhysicalSourceReader"/>) is byte-for-byte the flags every one of those call sites used
/// before this seam existed (<c>FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
/// FileOptions.SequentialScan</c>) -- introducing this interface changes no production behavior by
/// itself. A throttling decorator (perf-harness only, never a production default; see
/// <c>PhotoReview.Benchmarking.SlowLinkSourceReader</c>) can wrap it to measure or cap bandwidth per
/// <see cref="SourceReadPriority"/> lane, which previously had no seam to attach to (see
/// docs/refactoring/decisions/Q-R29-C.md part 2).
/// </summary>
public interface ISourceReader
{
    /// <summary>
    /// Opens <paramref name="path"/> for a sequential, shared read of source image bytes.
    /// <paramref name="priority"/> is metadata only (see <see cref="SourceReadPriority"/>); it never
    /// changes what bytes are returned, only how a decorator may pace the read.
    /// </summary>
    Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024);
}
