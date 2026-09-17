using System.IO;

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
    public static bool IsSupportedImage(string path) => ImageFileTypes.IsSupported(path);

    public static DragDropInputResult Parse(IEnumerable<string>? paths)
    {
        var validPaths = (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
        if (validPaths.Count == 0)
            return new(DragDropInputKind.Invalid, null, null, 0, "Không có file hoặc folder hợp lệ.");

        var folder = validPaths.FirstOrDefault(Directory.Exists);
        if (folder is not null)
        {
            var ignored = validPaths.Count(path => !string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
            return new(DragDropInputKind.Folder, Normalize(folder), null, ignored,
                ignored > 0 ? "Chỉ mở folder đầu tiên được thả." : null);
        }

        var images = validPaths.Where(path => File.Exists(path) && IsSupportedImage(path)).ToList();
        if (images.Count == 0)
            return new(DragDropInputKind.Invalid, null, null, validPaths.Count, "Không tìm thấy ảnh được hỗ trợ.");

        var initialImage = Normalize(images[0]);
        var folderPath = Path.GetDirectoryName(initialImage);
        if (folderPath is null)
            return new(DragDropInputKind.Invalid, null, null, validPaths.Count, "Không xác định được folder của ảnh.");

        var acceptedImages = images.Where(path => string.Equals(Path.GetDirectoryName(Normalize(path)), folderPath, StringComparison.OrdinalIgnoreCase)).ToList();
        var ignoredCount = validPaths.Count - acceptedImages.Count;
        var mixedFolders = acceptedImages.Count != images.Count;
        var warning = mixedFolders ? "Các ảnh khác folder; chỉ mở folder của ảnh đầu tiên." : ignoredCount > 0 ? "Một số file không được hỗ trợ và đã bị bỏ qua." : null;
        return new(DragDropInputKind.Image, folderPath, initialImage, ignoredCount, warning);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
