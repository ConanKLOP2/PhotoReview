namespace PhotoReview.Core.Abstractions;

/// <summary>
/// The two file primitives journal compaction (<see cref="PhotoReview.Core.FileActions.OperationJournal.TryCompact"/>) needs
/// beyond <see cref="IFileSystem"/>. They exist only on the real Windows file system
/// (<see cref="PhotoReview.Core.IO.PhysicalJournalCompactionFiles"/>); a journal built without them never compacts.
/// </summary>
public interface IJournalCompactionFiles
{
    /// <summary>
    /// Opens <paramref name="path"/> for reading while DENYING writers (share mode Read | Delete). Every journal append
    /// (<c>FileShare.Read</c>, any process) gets a sharing violation while the returned stream is open and retries
    /// (<c>OperationJournal.AppendAttempts</c>); readers and <see cref="IStagedReplacement.ReplaceAtomically"/> are not blocked.
    /// Throws <see cref="System.IO.IOException"/> (sharing violation) when a writer holds the file right now.
    /// </summary>
    Stream OpenReadDenyWriters(string path);

    /// <summary>Creates <paramref name="tempPath"/> (must not exist) as the replacement to be written and then renamed over the target.</summary>
    IStagedReplacement CreateStagedReplacement(string tempPath);
}

/// <summary>
/// A new file being written as the replacement of another. Disposing it without a successful
/// <see cref="ReplaceAtomically"/> deletes it; the target is never touched until that call succeeds.
/// </summary>
public interface IStagedReplacement : IDisposable
{
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Flushes everything written so far to the disk (fsync), whatever the journal durability mode.</summary>
    void FlushToDisk();

    /// <summary>
    /// Renames this file over <paramref name="destination"/> in ONE atomic file-system operation (POSIX-semantics rename):
    /// afterwards the destination name refers to this file's content, and open handles to the old file (readers, the
    /// <see cref="IJournalCompactionFiles.OpenReadDenyWriters"/> lock) keep reading the old content. On failure it throws
    /// and the destination is unchanged. A handle that denies delete sharing (an append in progress) makes it fail.
    /// </summary>
    void ReplaceAtomically(string destination);
}
