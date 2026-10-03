using PhotoReview.Core.Abstractions;

namespace PhotoReview.Core.Catalog;

public static class SiblingFolderService
{
    public static IReadOnlyList<string> GetSorted(string currentFolder, ILog? log = null)
    {
        var fullCurrent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));
        var parent = Directory.GetParent(fullCurrent);
        if (parent is null) return [];
        try
        {
            return Directory.EnumerateDirectories(parent.FullName)
                .OrderBy(Path.GetFileName, ManagedNaturalComparer.Instance)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log?.Error($"Failed to enumerate sibling folders of {parent.FullName}", ex);
            return [];
        }
    }
}
