using System.IO;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Gom nguyên văn toàn bộ định dạng chuỗi thanh trạng thái (StatusText) và định dạng kích thước tệp tin
/// từ MainWindow để tái sử dụng độc lập với tầng giao diện.
/// </summary>
public static class StatusFormatter
{
    /// <summary>
    /// Định dạng kích thước tệp tin theo các đơn vị B, KB, MB, GB.
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public static string ScanningFolder() => "Đang quét folder ảnh…";

    public static string NoSupportedImages() => "Không tìm thấy ảnh hỗ trợ trong folder này.";

    public static string FolderOpenFailed(string message) => $"Không mở được folder: {message}";

    public static string IndexOnly(int index, int count) => $"{index + 1}/{count}";

    public static string Loading(int index, int count, long initialSize) =>
        $"{index + 1}/{count} · {FormatFileSize(initialSize)} · Đang tải";

    public static string LoadingFullRes(int index, int count, long initialSize) =>
        $"{index + 1}/{count} · {FormatFileSize(initialSize)} · Đang tải bản rõ";

    public static string Ready(int index, int count, long size, string fileName) =>
        $"{index + 1}/{count} · {FormatFileSize(size)} · {fileName}";

    public static string WithDimensions(int index, int count, long size, int width, int height, string fileName) =>
        $"{index + 1}/{count} · {FormatFileSize(size)} · {width}×{height} · {fileName}";

    public static string Zoom(int index, int count, long size, double zoom) =>
        $"{index + 1}/{count} · {FormatFileSize(size)} · Zoom {zoom:0.##}x";

    public static string Compare(int index, int count, string leftName, string leftSize, string rightName, string rightSize, string hashText) =>
        $"{index + 1}/{count} | Compare | {leftName}{leftSize} ↔ {rightName}{rightSize}{hashText} | click để chọn";

    public static string ImageError(string fileName, string message) =>
        $"Lỗi ảnh: {fileName} — {message}";

    public static string NoImagesRemaining() => "Không còn ảnh trong thư mục";

    public static string AllImagesProcessed() => "Đã xử lý hết ảnh trong folder.";

    public static string CacheCleared() => "Đã xóa cache preview.";

    public static string DuplicateCheckCanceledFolderChanged() => "Đã hủy: folder đã đổi trong lúc kiểm tra trùng lặp.";

    public static string NoDuplicatesFound() => "Không có duplicate cùng hash phù hợp.";

    public static string BatchCanceled() => "Đã hủy xử lý hàng loạt.";

    public static string BatchDone(int succeeded, int failures) =>
        $"Batch hoàn tất: {succeeded} thành công, {failures} lỗi.";

    public static string SiblingFolderBoundary(int direction) =>
        direction > 0 ? "Đã ở folder cuối cùng cùng cấp." : "Đã ở folder đầu tiên cùng cấp.";

    public static string ActionCompleted(string actionName) =>
        $"Đã thực hiện: {actionName}";

    public static string ActionInvalidOperation(string actionName) =>
        $"Không thực hiện được {actionName}: Operation không hợp lệ.";

    public static string ActionFailed(string actionName, string message) =>
        $"Không thực hiện được {actionName}: {message}";

    public static string FileProcessingFailed(string fileName, string message) =>
        $"Không xử lý được {fileName}: {message}";

    public static string UndoNoMoves() => "Không có Move nào để hoàn tác.";

    public static string UndoNoActions() => "Không có Move/Delete vừa thực hiện để hoàn tác.";

    public static string UndoFailed(string message) => $"Không thể Undo: {message}";

    public static string RecycleRestoreFailed(string fileName) =>
        $"Không thể khôi phục Recycle Bin: {fileName}";
}