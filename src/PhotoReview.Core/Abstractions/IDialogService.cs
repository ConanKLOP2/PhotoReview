namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc hiển thị hộp thoại xác nhận và thông báo cho người dùng.
/// </summary>
public interface IDialogService
{
    /// <summary>Hiển thị hộp thoại hỏi Có/Không hoặc Đồng ý/Hủy.</summary>
    bool ShowConfirmation(string title, string message);

    /// <summary>Hiển thị hộp thoại thông báo thông tin.</summary>
    void ShowMessage(string title, string message);

    /// <summary>Hiển thị hộp thoại báo lỗi.</summary>
    void ShowError(string title, string message);

    /// <summary>Hiển thị hộp thoại chọn thư mục.</summary>
    string? PickFolder(string? initialFolder = null);

    /// <summary>Hiển thị cửa sổ xem lại và xác nhận xử lý hàng loạt (Batch Review).</summary>
    bool ShowBatchReview(IReadOnlyList<string> paths);

    /// <summary>
    /// RV-A13: same dialog, but with the sizes the caller already knows (from the catalog's folder scan), so the dialog
    /// does not stat every file on the UI thread. The default forwards to the path-only overload.
    /// </summary>
    bool ShowBatchReview(IReadOnlyList<BatchReviewItem> items) => ShowBatchReview(items.Select(item => item.Path).ToList());

    /// <summary>Hiển thị cửa sổ khôi phục nhật ký thao tác (Recovery).</summary>
    void ShowRecovery();

    /// <summary>Hiển thị cửa sổ chẩn đoán hiệu năng (Diagnostics).</summary>
    void ShowDiagnostics();

    /// <summary>Hiển thị cửa sổ cài đặt cấu hình (Settings). Trả về true nếu người dùng lưu thay đổi.</summary>
    bool ShowSettings();

    /// <summary>
    /// Same dialog, opened on the page and field for <paramref name="target"/>. The default ignores the target, so existing
    /// implementations keep working; the WPF implementation selects the page and focuses the field.
    /// </summary>
    bool ShowSettings(SettingsTarget target) => ShowSettings();

    /// <summary>Hiển thị cửa sổ đo benchmark hiệu năng nạp ảnh.</summary>
    void ShowBenchmark(string? folder = null);

    /// <summary>Hiển thị danh sách tệp bị bỏ qua khi nạp thư mục (AR19).</summary>
    void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries);
}

/// <summary>Which part of the Settings dialog to open on.</summary>
public enum SettingsTarget
{
    /// <summary>The last page shown (default behaviour).</summary>
    Default,

    /// <summary>The Files page with the External Editor path field focused.</summary>
    ExternalEditor,
}

/// <summary>One line of the batch-review dialog: the file and its size in bytes when known (null = unknown or no longer there).</summary>
public readonly record struct BatchReviewItem(string Path, long? Length);
