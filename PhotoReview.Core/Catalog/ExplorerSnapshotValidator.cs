using System.IO;

namespace PhotoReview.Core.Catalog;

public static class ExplorerSnapshotValidator
{
    public static bool TryValidate(ExplorerViewSnapshot snapshot, IReadOnlyCollection<string> scannedFiles, out IReadOnlyList<string> ordered, out string? reason)
    {
        ordered = [];
        reason = null;
        if (snapshot.Status != ExplorerOrderStatus.Available)
        {
            reason = snapshot.Reason ?? snapshot.Status.ToString();
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
                reason = "Native view returned an empty path";
                return false;
            }

            var path = Path.GetFullPath(candidate);
            if (!SamePath(Path.GetDirectoryName(path) ?? string.Empty, folder))
            {
                reason = "Native view returned an item outside the folder";
                return false;
            }

            if (!seen.Add(path))
            {
                reason = "Native view returned a duplicate item";
                return false;
            }

            if (expected.Contains(path))
            {
                result.Add(path);
            }
        }

        if (result.Count != expected.Count || !expected.SetEquals(result))
        {
            reason = "Native view did not contain the complete image snapshot";
            return false;
        }

        ordered = result;
        return true;
    }

    public static string CanonicalizeFolder(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public static bool SamePath(string first, string second) =>
        string.Equals(CanonicalizeFolder(first), CanonicalizeFolder(second), StringComparison.OrdinalIgnoreCase);
}
