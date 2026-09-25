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

        var folder = CanonicalizeFolder(snapshot.Folder);
        var expected = new HashSet<string>(scannedFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(expected.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in snapshot.OrderedPaths)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                reason = ExplorerReason.EmptyPath;
                return false;
            }

            var path = Path.GetFullPath(candidate);
            if (!SamePath(Path.GetDirectoryName(path) ?? string.Empty, folder))
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

        if (result.Count != expected.Count || !expected.SetEquals(result))
        {
            reason = ExplorerReason.IncompleteSnapshot;
            return false;
        }

        ordered = result;
        return true;
    }

    public static string CanonicalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public static bool SamePath(string first, string second) =>
        string.Equals(CanonicalizeFolder(first), CanonicalizeFolder(second), StringComparison.OrdinalIgnoreCase);
}
