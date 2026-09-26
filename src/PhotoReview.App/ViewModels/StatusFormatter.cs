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
    /// Định dạng kích thước tệp tin theo các đơn vị byte/KB/MB/GB; từ đơn vị lấy từ catalog (<c>unit.*</c>).
    /// </summary>
    public static string FormatFileSize(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < 3)
        {
            value /= 1024;
            unit++;
        }
        var number = value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture);
        return unit switch
        {
            0 => Tr.UnitSizeByte(number),
            1 => Tr.UnitSizeKilobyte(number),
            2 => Tr.UnitSizeMegabyte(number),
            _ => Tr.UnitSizeGigabyte(number),
        };
    }

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

    /// <summary>Hash state fragment when a hash could not be computed (file changed or unreadable); the previews stay visible.</summary>
    public static string CompareHashUnknown() => Tr.CompareHashUnknown;

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

    /// <summary>Fallback when undo fails without a message.</summary>
    public static string NothingToUndo() => Tr.StatusNothingToUndo;
}