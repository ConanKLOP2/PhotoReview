using PhotoReview.App.Presenters;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi;

/// <summary>
/// WP-35 GĐ3a: những gì một trang Settings được phép làm khi chạy - truy cập điều khiển của chính nó theo id (chữ, tick, combobox,
/// thanh trượt, bật/tắt, ẩn/hiện), presenter, hộp thoại cảnh báo, file dialog, hàng đợi UI. Là mặt tiền mỏng trên <see cref="SettingsDialog"/>
/// để trang không chạm HWND/thông điệp Win32.
/// </summary>
internal sealed class SettingsPageContext
{
    private readonly SettingsDialog _dialog;

    internal SettingsPageContext(SettingsDialog dialog) => _dialog = dialog;

    /// <summary>Presenter dùng chung (cho RamCacheRange, ShellIntegration, CheckForUpdateAsync...).</summary>
    public SettingsPresenter Presenter => _dialog.Presenter;

    /// <summary>HWND cửa sổ Settings (owner của hộp thoại con).</summary>
    public nint Owner => _dialog.Hwnd;

    /// <summary>Hộp thoại Open/Save của shell (null trong test không cấp).</summary>
    public IShellFileDialog? Files => _dialog.Files;

    /// <summary>Tuỳ chọn/seam của cửa sổ (OpenUrl, StartExplorer, Version...).</summary>
    public SettingsDialogOptions Options => _dialog.Options;

    public string Text(int id) => _dialog.GetControlText(id);

    public void SetText(int id, string text) => _dialog.SetControlText(id, text);

    public bool IsChecked(int id) => _dialog.IsControlChecked(id);

    public void SetChecked(int id, bool value) => _dialog.SetControlChecked(id, value);

    public int ComboIndex(int id) => _dialog.ComboSelectedIndex(id);

    public void SetComboIndex(int id, int index) => _dialog.SetComboSelectedIndex(id, index);

    /// <summary>Nạp lại danh sách của combobox (danh sách động, ví dụ ngôn ngữ) và chọn <paramref name="selected"/>.</summary>
    public void SetComboItems(int id, IReadOnlyList<string> items, int selected) => _dialog.SetComboItems(id, items, selected);

    public int SliderValue(int id) => _dialog.SliderValue(id);

    public void SetSliderValue(int id, int value) => _dialog.SetSliderValue(id, value);

    /// <summary>Đặt lại khoảng của thanh trượt (RAM tối thiểu đổi theo cửa sổ preload).</summary>
    public void SetSliderRange(int id, int min, int max) => _dialog.SetSliderRange(id, min, max);

    public void SetEnabled(int id, bool enabled) => _dialog.SetControlEnabled(id, enabled);

    public bool IsEnabled(int id) => _dialog.IsControlEnabled(id);

    /// <summary>Ẩn/hiện một điều khiển hay cả nhóm (id GroupBox): phần ẩn không chiếm chỗ, bố cục tính lại.</summary>
    public void SetVisible(int id, bool visible) => _dialog.SetControlVisible(id, visible);

    public void Focus(int id) => _dialog.FocusControl(id);

    /// <summary>Tính lại bố cục trang (chữ đổi độ dài, phần tử ẩn/hiện).</summary>
    public void Relayout() => _dialog.RelayoutActivePage();

    /// <summary>Hộp thoại cảnh báo modal trên cửa sổ Settings.</summary>
    public void ShowWarning(string title, string message) => _dialog.Warn(title, message);

    /// <summary>Chạy <paramref name="action"/> trên UI thread của cửa sổ (an toàn gọi từ luồng nền, ví dụ kết quả kiểm tra cập nhật).</summary>
    public void PostToUi(Action action) => _dialog.PostToUi(action);

    /// <summary>Chạy một yêu cầu Explorer của presenter (mở thư mục/chọn file); lỗi hiện cảnh báo "không mở được".</summary>
    public void RunExplorer(SettingsExplorerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ErrorMessage is not null)
        {
            ShowWarning(PhotoReview.Core.Localization.Tr.DialogSettingsOpenFailedTitle, request.ErrorMessage);
            return;
        }

        if (request.Arguments is null)
        {
            return;
        }

        var error = Options.StartExplorer is { } start ? start(request.Arguments) : SettingsPresenter.TryStartExplorer(request.Arguments);
        if (error is not null)
        {
            ShowWarning(PhotoReview.Core.Localization.Tr.DialogSettingsOpenFailedTitle, error);
        }
    }

    /// <summary>Tên hiển thị phiên bản (chân cột trái; dùng cho kiểm tra cập nhật).</summary>
    public string? Version => _dialog.Options.Version;
}
