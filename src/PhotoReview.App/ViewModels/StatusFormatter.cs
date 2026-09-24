using System.Globalization;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.ViewModels;

/// <summary>
/// Gom toàn bộ định dạng chuỗi thanh trạng thái (StatusText) và định dạng kích thước tệp tin
/// để tái sử dụng độc lập với tầng giao diện. Văn bản lấy từ catalog ngôn ngữ qua <see cref="Tr"/>
/// (ADR 0006); số được định dạng sẵn ở đây đúng như trước khi trích xuất.
/// </summary>
public static class StatusFormatter
{
    /// <summary>
    /// Định dạng kích thước tệp tin theo các đơn vị B, KB, MB, GB (đơn vị trung lập, không dịch).
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

    public static string ScanningFolder() => Tr.StatusScanningFolder;

    public static string NoSupportedImages() => Tr.StatusNoSupportedImages;

    public static string FolderOpenFailed(string message) => Tr.StatusFolderOpenFailed(message);

    public static string IndexOnly(int index, int count) => Tr.StatusPosition(index + 1, count);

    public static string Loading(int index, int count, long initialSize) =>
        Tr.StatusLoading(index + 1, count, FormatFileSize(initialSize));

    public static string LoadingFullRes(int index, int count, long initialSize) =>
        Tr.StatusLoadingFullRes(index + 1, count, FormatFileSize(initialSize));

    public static string Ready(int index, int count, long size, string fileName) =>
        Tr.StatusReady(index + 1, count, FormatFileSize(size), fileName);

    public static string WithDimensions(int index, int count, long size, int width, int height, string fileName) =>
        Tr.StatusWithDimensions(index + 1, count, FormatFileSize(size), width, height, fileName);

    public static string Zoom(int index, int count, long size, double zoom) =>
        Tr.StatusZoom(index + 1, count, FormatFileSize(size), zoom.ToString("0.##", CultureInfo.CurrentCulture));

    /// <param name="leftSize">Fragment from <see cref="CompareSizeSuffix"/> or empty.</param>
    /// <param name="rightSize">Fragment from <see cref="CompareSizeSuffix"/> or empty.</param>
    /// <param name="hashText">Fragment from <see cref="CompareHashText"/> (includes its leading separator).</param>
    public static string Compare(int index, int count, string leftName, string leftSize, string rightName, string rightSize, string hashText) =>
        Tr.StatusCompare(index + 1, count, leftName, leftSize, rightName, rightSize, hashText);

    /// <summary>" (1,048,576 B)": file size in bytes for the compare status (number in the current culture).</summary>
    public static string CompareSizeSuffix(long bytes) =>
        Tr.CompareSizeSuffix(bytes.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Hash state fragment for the compare status: null = hash comparison off.</summary>
    public static string CompareHashText(bool? match) => match switch
    {
        null => Tr.CompareHashOff,
        true => Tr.CompareHashSame,
        false => Tr.CompareHashDifferent,
    };

    public static string ImageError(string fileName, string message) =>
        Tr.StatusImageError(fileName, message);

    public static string NoImagesRemaining() => Tr.StatusNoImagesRemaining;

    public static string AllImagesProcessed() => Tr.StatusAllImagesProcessed;

    public static string CacheCleared() => Tr.StatusCacheCleared;

    public static string DuplicateCheckFailed(string message) => Tr.StatusDuplicateCheckFailed(message);

    public static string DuplicateCheckCanceledFolderChanged() => Tr.StatusDuplicateCheckCanceledFolderChanged;

    public static string NoDuplicatesFound() => Tr.StatusNoDuplicatesFound;

    public static string BatchCanceled() => Tr.StatusBatchCanceled;

    public static string BatchDone(int succeeded, int failures) => Tr.StatusBatchDone(succeeded, failures);

    public static string SiblingFolderBoundary(int direction) =>
        direction > 0 ? Tr.StatusSiblingFolderLast : Tr.StatusSiblingFolderFirst;

    public static string ActionCompleted(string actionName) => Tr.StatusActionCompleted(actionName);

    public static string ActionInvalidOperation(string actionName) => Tr.StatusActionInvalidOperation(actionName);

    public static string ActionNoDestination(string actionName) => Tr.StatusActionNoDestination(actionName);

    public static string ActionFailed(string actionName, string? message) => Tr.StatusActionFailed(actionName, message);

    public static string CopiedTo(string? fileName) => Tr.StatusCopiedTo(fileName);

    public static string FileProcessingFailed(string fileName, string message) =>
        Tr.StatusFileProcessingFailed(fileName, message);

    public static string UndoNoMoves() => Tr.CoreUndoNoMoveToUndo;

    public static string UndoNoActions() => Tr.CoreUndoNothingToUndo;

    public static string UndoFailed(string message) => Tr.CoreUndoFailed(message);

    /// <summary>Fallback when the obsolete Move-only undo fails without a message.</summary>
    public static string UndoFailedGeneric() => Tr.StatusUndoFailedGeneric;

    /// <summary>Fallback when undo fails without a message.</summary>
    public static string NothingToUndo() => Tr.StatusNothingToUndo;

    public static string RecycleRestoreFailed(string fileName) => Tr.CoreUndoRecycleRestoreFailed(fileName);
}