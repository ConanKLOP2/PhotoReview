using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// RV-C03 / error-handling review: shared best-effort cleanup for a Copy that failed after possibly creating its
/// destination. The caller must only invoke it for a destination this operation created, proven by
/// <see cref="CopyCreationProof.DestinationCreated"/> (a pre-check that found the path free proves nothing: a foreign file may appear later).
/// </summary>
internal static class PartialDestinationCleanup
{
    /// <summary>
    /// Deletes <paramref name="destination"/> only while it is still the file this operation created (R01a/b: same size and write
    /// time as <paramref name="created"/>, observed right after the copy; a foreign file that replaced it meanwhile is never ours) AND
    /// it is provably incomplete (strictly shorter than the source). A complete file is left for Recovery to judge. Never throws except
    /// <see cref="OutOfMemoryException"/>: a file that cannot be inspected or deleted stays.
    /// </summary>
    public static void RemoveIfPartial(IFileSystem fileSystem, string destination, long sourceSize, FileStat created)
    {
        ArgumentNullException.ThrowIfNull(created);
        try
        {
            if (fileSystem.GetFileStat(destination) is { } stat && stat.Length < sourceSize && IsSameFile(stat, created))
                fileSystem.Delete(destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Left in place; the Failed journal line still describes the operation, and the original error is what the caller reports.
        }
    }

    /// <summary>Exact compare: the destination was created by this operation on its own volume and observed through the same file system,
    /// so no rounding allowance applies (a tolerance would only widen what a replacement can pass as).</summary>
    internal static bool IsSameFile(FileStat now, FileStat created) =>
        now.Length == created.Length && now.LastWriteUtc == created.LastWriteUtc;
}
