using System.IO;

namespace PhotoReview.Imaging.Raw;

/// <summary>Classifies the <see cref="InvalidDataException"/> a <see cref="IRawHeaderSource"/> raises.</summary>
internal static class RawHeaderErrors
{
    /// <summary>
    /// True when the header source wrapped a disk/share failure (not a malformed or over-budget RAW). Such an error must keep
    /// propagating so it is reported as an I/O problem instead of being swallowed as "preview unusable".
    /// </summary>
    public static bool IsIoFailure(InvalidDataException ex) => ex.InnerException is IOException or ObjectDisposedException;
}
