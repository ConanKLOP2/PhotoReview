using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Catalog;

public enum DragDropInputKind
{
    Invalid,
    Folder,
    Image
}

public sealed record DragDropInputResult(
    DragDropInputKind Kind,
    string? FolderPath,
    string? InitialImagePath,
    int IgnoredPathCount,
    string? Warning)
{
    public bool IsValid => Kind != DragDropInputKind.Invalid && FolderPath is not null;
}

public static class DragDropInputService
{
    public static DragDropInputResult Parse(IEnumerable<string>? paths, bool rawEnabled = false, bool webpHeicEnabled = false)
    {
        var validPaths = (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (validPaths.Count == 0)
            return new(DragDropInputKind.Invalid, null, null, 0, Tr.CoreDragDropNoValidInput);

        var folder = validPaths.FirstOrDefault(Directory.Exists);
        if (folder is not null)
        {
            var ignored = validPaths.Count(path => !IsSameFolder(path, folder));
            return new(DragDropInputKind.Folder, Normalize(folder), null, ignored,
                ignored > 0 ? Tr.CoreDragDropOnlyFirstFolder : null);
        }

        var images = validPaths.Where(path => File.Exists(path) && ImageFileTypes.IsSupported(path, rawEnabled, webpHeicEnabled)).ToList();
        if (images.Count == 0)
            return new(DragDropInputKind.Invalid, null, null, validPaths.Count, Tr.CoreDragDropNoSupportedImages);

        var initialImage = Normalize(images[0]);
        var folderPath = Path.GetDirectoryName(initialImage);
        if (folderPath is null)
            return new(DragDropInputKind.Invalid, null, null, validPaths.Count, Tr.CoreDragDropFolderUnknown);

        var acceptedImages = images.Where(path => string.Equals(Path.GetDirectoryName(Normalize(path)), folderPath, StringComparison.OrdinalIgnoreCase)).ToList();
        var ignoredCount = validPaths.Count - acceptedImages.Count;
        var mixedFolders = acceptedImages.Count != images.Count;
        var warning = mixedFolders ? Tr.CoreDragDropMixedFolders : ignoredCount > 0 ? Tr.CoreDragDropUnsupportedIgnored : null;
        return new(DragDropInputKind.Image, folderPath, initialImage, ignoredCount, warning);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>The same folder dropped twice, or spelled with and without a trailing separator, is one folder, not an ignored extra.</summary>
    private static bool IsSameFolder(string path, string folder)
    {
        if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Normalize(path)),
                Path.TrimEndingDirectorySeparator(Normalize(folder)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // a path the file system rejects is never the folder
        }
    }
}
