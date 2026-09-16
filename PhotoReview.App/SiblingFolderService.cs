using System.IO;

namespace PhotoReview.App;

public static class SiblingFolderService
{
    public static IReadOnlyList<string> GetSorted(string currentFolder)
    {
        var fullCurrent = Path.GetFullPath(currentFolder);
        var parent = Directory.GetParent(fullCurrent);
        if (parent is null) return [];
        try
        {
            return Directory.EnumerateDirectories(parent.FullName)
                .OrderBy(path => ImageSortService.NaturalKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppLog.Error($"Failed to enumerate sibling folders of {parent.FullName}", ex);
            return [];
        }
    }

    public static string? GetTarget(string currentFolder, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var folders = GetSorted(currentFolder);
        var current = Path.GetFullPath(currentFolder);
        var index = folders.ToList().FindIndex(path => string.Equals(Path.GetFullPath(path), current, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        return index >= 0 && target >= 0 && target < folders.Count ? folders[target] : null;
    }
}
