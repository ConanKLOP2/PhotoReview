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
            // Hidden/System folders (e.g. a ".thumbnails" cache) are not navigation targets. IgnoreInaccessible stays
            // false (the default of EnumerationOptions is true) so a failure still reaches the catch below.
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
            };
            var folders = Directory.EnumerateDirectories(parent.FullName, "*", options).ToList();
            // The folder the user opened deliberately may itself be Hidden/System: it must stay in the list so
            // the caller can locate it and still step to its visible siblings.
            if (!folders.Contains(fullCurrent, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(fullCurrent);
            }

            return folders.OrderBy(Path.GetFileName, ManagedNaturalComparer.Instance).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            log?.Error($"Failed to enumerate sibling folders of {parent.FullName}", ex);
            return [];
        }
    }
}
