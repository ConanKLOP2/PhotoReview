using System.IO;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

public static class SiblingFolderService
{
    public static IReadOnlyList<string> GetSorted(string currentFolder, INaturalComparer? naturalComparer = null, ILog? log = null)
    {
        var fullCurrent = Path.GetFullPath(currentFolder);
        var parent = Directory.GetParent(fullCurrent);
        if (parent is null) return [];
        try
        {
            var comparer = naturalComparer ?? ManagedNaturalComparer.Instance;
            return Directory.EnumerateDirectories(parent.FullName)
                .OrderBy(Path.GetFileName, comparer)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log?.Error($"Failed to enumerate sibling folders of {parent.FullName}", ex);
            return [];
        }
    }

    public static string? GetTarget(string currentFolder, int direction, INaturalComparer? naturalComparer = null, ILog? log = null)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var folders = GetSorted(currentFolder, naturalComparer, log);
        var current = Path.GetFullPath(currentFolder);
        var index = folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), current, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        return index >= 0 && target >= 0 && target < folders.Count ? folders[target] : null;
    }
}
