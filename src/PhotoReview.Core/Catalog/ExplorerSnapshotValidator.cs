using System.IO;

namespace PhotoReview.Core.Catalog;

public static class ExplorerSnapshotValidator
{
    public static bool TryValidate(ExplorerViewSnapshot snapshot, IReadOnlyCollection<string> scannedFiles, out IReadOnlyList<string> ordered, out string? reason)
    {
        try
        {
            return TryValidateCore(snapshot, scannedFiles, out ordered, out reason);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // The snapshot comes from COM (Explorer): a null/blank folder, a null path list or a path the file system
            // rejects (NUL, too long) is an invalid snapshot, never a crash of the folder load.
            ordered = [];
            reason = ExplorerReason.IncompleteSnapshot;
            return false;
        }
    }

    private static bool TryValidateCore(ExplorerViewSnapshot snapshot, IReadOnlyCollection<string> scannedFiles, out IReadOnlyList<string> ordered, out string? reason)
    {
        ordered = [];
        reason = null;
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scannedFiles);
        if (snapshot.Status != ExplorerOrderStatus.Available)
        {
            reason = snapshot.Reason ?? snapshot.Status.ToString();
            return false;
        }

        if (snapshot.OrderedPaths is null)
        {
            reason = ExplorerReason.IncompleteSnapshot;
            return false;
        }

        string folder;
        HashSet<string> expected;
        try
        {
            folder = CanonicalizeFolder(snapshot.Folder);
            expected = new HashSet<string>(scannedFiles.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = ExplorerReason.OutsideFolder;
            return false;
        }
        var result = new List<string>(expected.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in snapshot.OrderedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                reason = ExplorerReason.EmptyPath;
                return false;
            }

            string path;
            try { path = NormalizePath(candidate); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Embedded NUL or otherwise unrepresentable text: never a member of the folder, and it must
                // not escape as an exception into the folder load.
                reason = ExplorerReason.OutsideFolder;
                return false;
            }
            if (!InFolder(path, folder))
            {
                reason = ExplorerReason.OutsideFolder;
                return false;
            }

            if (!seen.Add(path))
            {
                reason = ExplorerReason.DuplicateItem;
                return false;
            }

            if (expected.Contains(path))
            {
                result.Add(path);
            }
        }

        // result holds unique members of expected (seen rejects duplicates), so equal counts already mean equal sets.
        if (result.Count != expected.Count)
        {
            reason = ExplorerReason.IncompleteSnapshot;
            return false;
        }

        ordered = result;
        return true;
    }

    public static string CanonicalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(NormalizePath(folder));

    /// <summary>
    /// Full path with the Win32 extended-length prefix removed (<c>\\?\C:\x</c> becomes <c>C:\x</c>,
    /// <c>\\?\UNC\srv\share</c> becomes <c>\\srv\share</c>), so a shell that reports a long path in
    /// extended form still compares equal to the scanned path.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + full[8..];
        if (full.Length >= 6 && full.StartsWith(@"\\?\", StringComparison.Ordinal) && full[5] == ':') return full[4..];
        return full;
    }

    /// <summary>
    /// <see cref="SamePath"/>(directory of <paramref name="normalizedPath"/>, <paramref name="canonicalFolder"/>) without
    /// re-canonicalising per item: an already normalised path's directory that textually equals the canonical folder is
    /// the same folder; anything else falls back to the full comparison, so the result never differs.
    /// </summary>
    private static bool InFolder(string normalizedPath, string canonicalFolder)
    {
        var directory = Path.GetDirectoryName(normalizedPath.AsSpan());
        return directory.Equals(canonicalFolder, StringComparison.OrdinalIgnoreCase)
            || SamePath(directory.ToString(), canonicalFolder);
    }

    public static bool SamePath(string first, string second) =>
        string.Equals(CanonicalizeFolder(first), CanonicalizeFolder(second), StringComparison.OrdinalIgnoreCase);
}
