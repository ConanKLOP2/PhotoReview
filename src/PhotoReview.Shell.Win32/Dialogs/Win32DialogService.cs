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

    /// <summary>WP-19b: zoom tuỳ chọn (<c>IZoomPromptService</c>); null khi huỷ.</summary>
    int? PromptCustomZoom(nint ownerHwnd, int currentPercent);
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
    private readonly SecondaryWindowFallback? _fallback;

    public Win32DialogService(Func<nint> ownerProvider, FolderPicker folderPicker, Lazy<ISecondaryWindowPort?> secondary,
        SecondaryWindowFallback? fallback = null)
        : this(ownerProvider, new NativeTaskDialogPort(), folderPicker, secondary, fallback)
    {
    }

    internal Win32DialogService(Func<nint> ownerProvider, ITaskDialogPort taskDialogs, FolderPicker folderPicker,
        Lazy<ISecondaryWindowPort?> secondary, SecondaryWindowFallback? fallback = null)
    {
        _ownerProvider = ownerProvider;
        _taskDialogs = taskDialogs;
        _folderPicker = folderPicker;
        _secondary = secondary;
        _fallback = fallback;
    }

    /// <summary>Cổng cửa sổ phụ, hoặc null khi vắng hay nạp cầu thất bại (khi đó <see cref="_fallback"/> trả lời).</summary>
    private ISecondaryWindowPort? Port()
    {
        try
        {
            return _secondary.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
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
        => Port() is { } port ? port.ShowBatchReview(_ownerProvider(), items) : _fallback?.BatchReview() ?? false;

    public void ShowRecovery()
    {
        if (Port() is { } port) port.ShowRecovery(_ownerProvider());
        else _fallback?.Recovery();
    }

    public void ShowDiagnostics()
    {
        if (Port() is { } port) port.ShowDiagnostics(_ownerProvider());
        else _fallback?.Diagnostics();
    }

    public bool ShowSettings() => ShowSettings(SettingsTarget.Default);

    public bool ShowSettings(SettingsTarget target)
        => Port() is { } port ? port.ShowSettings(_ownerProvider(), target) : _fallback?.OpenSettings() ?? false;

    public void ShowBenchmark(string? folder = null)
    {
        if (Port() is { } port) port.ShowBenchmark(_ownerProvider(), folder);
        else _fallback?.Benchmark();
    }

    public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
    {
        if (Port() is { } port) port.ShowSkippedFiles(_ownerProvider(), entries);
        else _fallback?.SkippedFiles();
    }
}
