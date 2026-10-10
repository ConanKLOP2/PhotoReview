using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// Cổng tới cửa sổ phụ WPF nạp muộn (C-13b). Phản chiếu <c>ISecondaryWindowHost</c> của Shell.WpfBridge nhưng nằm trong
/// Shell.Win32 (Shell.Win32 không tham chiếu WpfBridge trực tiếp, L-SHELL); <c>WpfBridgeLoader</c> (WP-19b) bọc host thật
/// thành cổng này và đưa vào <see cref="Win32DialogService"/> qua <see cref="Lazy{T}"/>.
/// </summary>
internal interface ISecondaryWindowPort
{
    bool ShowSettings(nint ownerHwnd, SettingsTarget target);

    void ShowRecovery(nint ownerHwnd);

    void ShowDiagnostics(nint ownerHwnd);

    void ShowBenchmark(nint ownerHwnd, string? folder);

    void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries);

    bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items);
}

/// <summary>
/// <see cref="IDialogService"/> cho shell Win32 (C-13, WP-19a): xác nhận/thông báo/lỗi bằng <c>TaskDialogIndirect</c>,
/// chọn thư mục bằng <see cref="FolderPicker"/>, các cửa sổ còn lại uỷ cho <see cref="ISecondaryWindowPort"/> (nạp muộn).
/// </summary>
internal sealed class Win32DialogService : IDialogService
{
    private readonly Func<nint> _ownerProvider;
    private readonly ITaskDialogPort _taskDialogs;
    private readonly FolderPicker _folderPicker;
    private readonly Lazy<ISecondaryWindowPort?> _secondary;

    public Win32DialogService(Func<nint> ownerProvider, FolderPicker folderPicker, Lazy<ISecondaryWindowPort?> secondary)
        : this(ownerProvider, new NativeTaskDialogPort(), folderPicker, secondary)
    {
    }

    internal Win32DialogService(Func<nint> ownerProvider, ITaskDialogPort taskDialogs, FolderPicker folderPicker,
        Lazy<ISecondaryWindowPort?> secondary)
    {
        _ownerProvider = ownerProvider;
        _taskDialogs = taskDialogs;
        _folderPicker = folderPicker;
        _secondary = secondary;
    }

    public bool ShowConfirmation(string title, string message)
        => _taskDialogs.Show(_ownerProvider(), title, message, TaskDialogKind.Confirmation) == Interop.WindowMessages.IdYes;

    public void ShowMessage(string title, string message)
        => _taskDialogs.Show(_ownerProvider(), title, message, TaskDialogKind.Information);

    public void ShowError(string title, string message)
        => _taskDialogs.Show(_ownerProvider(), title, message, TaskDialogKind.Error);

    public string? PickFolder(string? initialFolder = null)
        => _folderPicker.Pick(Tr.DialogPickFolderTitle, initialFolder);

    public bool ShowBatchReview(IReadOnlyList<string> paths)
        => ShowBatchReview(paths.Select(path => new BatchReviewItem(path, null)).ToList());

    public bool ShowBatchReview(IReadOnlyList<BatchReviewItem> items)
        => _secondary.Value?.ShowBatchReview(_ownerProvider(), items) ?? false;

    public void ShowRecovery() => _secondary.Value?.ShowRecovery(_ownerProvider());

    public void ShowDiagnostics() => _secondary.Value?.ShowDiagnostics(_ownerProvider());

    public bool ShowSettings() => ShowSettings(SettingsTarget.Default);

    public bool ShowSettings(SettingsTarget target)
        => _secondary.Value?.ShowSettings(_ownerProvider(), target) ?? false;

    public void ShowBenchmark(string? folder = null) => _secondary.Value?.ShowBenchmark(_ownerProvider(), folder);

    public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
        => _secondary.Value?.ShowSkippedFiles(_ownerProvider(), entries);
}
