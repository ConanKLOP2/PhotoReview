using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// Cổng tới cửa sổ phụ (C-13b). Thực thi: <c>Win32SecondaryWindowPort</c> (hộp thoại Win32 gốc, WP-35) qua
/// <c>SecondaryWindowRouter</c>; đưa vào <see cref="Win32DialogService"/> qua <see cref="Lazy{T}"/>. WP-35 GĐ5: Shell.Win32
/// không còn tham chiếu Shell.WpfBridge (cầu WPF chỉ còn là mã dùng trong test của dự án đó cho tới WP-37).
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

    /// <summary>
    /// WP-35: cổng này tự thực thi cửa sổ <paramref name="kind"/>? <see cref="SecondaryWindowRouter"/> hỏi để chọn cổng Win32 hay
    /// cổng tạm theo từng cửa sổ. Mặc định true (các cổng giả của test làm được mọi cửa sổ).
    /// </summary>
    bool Supports(SecondaryWindowKind kind) => true;

    /// <summary>
    /// WP-35 GĐ2: editor Action profiles (modal, lồng được trong Settings). Trả danh sách đã sửa khi người dùng Apply; null khi huỷ
    /// hoặc cổng không có (mặc định: chỉ cổng Win32 mở nó, từ bên trong cửa sổ Settings).
    /// </summary>
    IReadOnlyList<PhotoReview.Core.Settings.ReviewAction>? ShowActionProfiles(nint ownerHwnd,
        IReadOnlyList<PhotoReview.Core.Settings.ReviewAction> actions) => null;

    /// <summary>FEAT-EXTERNAL-EDITORS: danh sách trình chỉnh sửa (modal, mở từ Settings); null khi huỷ hoặc cổng không có.</summary>
    Native.ExternalEditorsEdit? ShowExternalEditors(nint ownerHwnd, IReadOnlyList<PhotoReview.Core.Settings.ExternalEditor> editors,
        string? defaultId) => null;

    /// <summary>FEAT-EXTERNAL-EDITORS ("Add more editors..."): chọn .exe + nhập tên; null khi huỷ hoặc cổng không có.</summary>
    PhotoReview.Core.Settings.ExternalEditor? PromptAddExternalEditor(nint ownerHwnd, string? initialFolder) => null;

    /// <summary>FEAT-EXTERNAL-EDITORS ("Set default editor..."): danh sách chọn một; id hoặc null.</summary>
    string? PromptDefaultExternalEditor(nint ownerHwnd, IReadOnlyList<PhotoReview.Core.Settings.ExternalEditor> editors,
        string? currentId) => null;

    /// <summary>FEAT-RENAME ("Rename..." / F2): hộp nhập tên mới (modal); chữ đã được kiểm hợp lệ, hoặc null khi huỷ.</summary>
    string? PromptRename(nint ownerHwnd, RenamePromptRequest request) => null;
}

/// <summary>Các cửa sổ phụ mà <see cref="ISecondaryWindowPort"/> có thể thực thi.</summary>
internal enum SecondaryWindowKind
{
    Settings,
    Recovery,
    Diagnostics,
    Benchmark,
    SkippedFiles,
    BatchReview,
    ZoomPrompt,

    /// <summary>WP-35 GĐ2: editor Action profiles (modal).</summary>
    ActionProfiles,

    /// <summary>FEAT-EXTERNAL-EDITORS: danh sách trình chỉnh sửa + hai hộp thoại nhỏ của menu chuột phải.</summary>
    ExternalEditors,

    /// <summary>FEAT-RENAME: hộp nhập tên mới của "Rename..." / F2 (modal).</summary>
    RenamePrompt,
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

    public PhotoReview.Core.Settings.ExternalEditor? PromptAddExternalEditor(string? initialFolder = null)
        => Port()?.PromptAddExternalEditor(_ownerProvider(), initialFolder);

    public string? PromptDefaultExternalEditor(IReadOnlyList<PhotoReview.Core.Settings.ExternalEditor> editors, string? currentId)
        => Port()?.PromptDefaultExternalEditor(_ownerProvider(), editors, currentId);

    public string? PromptRename(RenamePromptRequest request) => Port()?.PromptRename(_ownerProvider(), request);
}
