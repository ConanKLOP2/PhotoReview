using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.FileActions;

/// <summary>
/// RV-C03 / error-handling review: shared best-effort cleanup for a Copy that failed after possibly creating its
/// destination. The caller must only invoke it for a destination this operation created (a pre-check proved it absent).
/// </summary>
internal static class PartialDestinationCleanup
{
    /// <summary>
    /// Deletes <paramref name="destination"/> only while it is provably incomplete (strictly shorter than the source). A complete
    /// file is left for Recovery to judge. Never throws except
    /// <see cref="OutOfMemoryException"/>: a file that cannot be inspected or deleted stays.
    /// </summary>
    public static void RemoveIfPartial(IFileSystem fileSystem, string destination, long sourceSize)
    {
        try
        {
            if (fileSystem.GetFileStat(destination) is { } stat && stat.Length < sourceSize)
                fileSystem.Delete(destination);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Left in place; the Failed journal line still describes the operation, and the original error is what the caller reports.
        }
    }
}
