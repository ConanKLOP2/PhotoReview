namespace PhotoReview.Core.Abstractions;

/// <summary>
/// A file or directory that a folder scan could not read (access denied, locked, I/O error) and
/// skipped instead of failing the whole load (ADR 0007 section 3). <see cref="Reason"/> is a
/// short technical reason (the OS exception message, kept verbatim for logs); <see cref="Kind"/> is the stable
/// category the UI wraps in a translated sentence.
/// </summary>
public sealed record SkippedEntry(string Path, string Reason, SkippedKind Kind = SkippedKind.FileUnreadable);

/// <summary>Why a scan entry was skipped (language-neutral; the window maps it to catalog text).</summary>
public enum SkippedKind
{
    /// <summary>The file could not be opened for reading (locked, access denied, I/O error).</summary>
    FileUnreadable,

    /// <summary>The directory listing itself failed part-way; the rest of the folder was not scanned.</summary>
    ListingInterrupted,
}
