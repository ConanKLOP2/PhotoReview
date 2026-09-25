using System.IO;
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
    public static DragDropInputResult Parse(IEnumerable<string>? paths)
    {
        var validPaths = (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (validPaths.Count == 0)
            return new(DragDropInputKind.Invalid, null, null, 0, Tr.CoreDragDropNoValidInput);

        var folder = validPaths.FirstOrDefault(Directory.Exists);
        if (folder is not null)
        {
            var ignored = validPaths.Count(path => !string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
            return new(DragDropInputKind.Folder, Normalize(folder), null, ignored,
                ignored > 0 ? Tr.CoreDragDropOnlyFirstFolder : null);
        }

        var images = validPaths.Where(path => File.Exists(path) && ImageFileTypes.IsSupported(path)).ToList();
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
}
