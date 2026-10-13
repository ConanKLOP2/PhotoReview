using PhotoReview.App.Input;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Win32;

// WP-24: menu chuột phải của cửa sổ chính. Bản Win32 của ContextMenu của Grid gốc trong MainWindow.xaml + các handler *_Click ở
// MainWindow.xaml.cs; phần quyết định (mô hình, lệnh theo id) ở Menus/ContextMenuController.cs.
internal sealed partial class ShellMainWindow : IContextMenuActions
{
    private ContextMenuController? _contextMenu;

    /// <summary>
    /// Controller menu chuột phải (test đặt <see cref="ContextMenuController.MenuHost"/> bằng bản giả chọn id; production dùng
    /// menu native của <see cref="NativePopupMenuHost"/>).
    /// </summary>
    internal ContextMenuController ContextMenu => _contextMenu ?? throw new InvalidOperationException("The context menu is not registered.");

    private void RegisterContextMenu()
    {
        _contextMenu = new ContextMenuController(this, new NativePopupMenuHost(() => _window.IsDestroyed ? 0 : _window.Hwnd), _log);
        _window.AddMessageHandler(new ContextMenuMessageHandler(_window, _contextMenu));
    }

    // IContextMenuActions ---------------------------------------------------------------------

    AppSettings IContextMenuActions.Settings => _viewModel.Settings;

    bool IContextMenuActions.HasImage => _viewModel.HasImages;

    bool IContextMenuActions.CanCopyPath => _viewModel.CanCopyCurrentFilePath;

    // Undo luôn bật như bản WPF (rỗng thì là no-op): xem ContextMenuModelBuilder.
    bool IContextMenuActions.CanUndo => true;

    bool IContextMenuActions.CanOpenInExternalEditor => _viewModel.CanOpenInExternalEditor;

    bool IContextMenuActions.IsCompareVisible => _viewModel.Compare.IsVisible;

    double? IContextMenuActions.EffectiveZoom => _viewModel.Viewer.EffectiveZoom;

    Task IContextMenuActions.ExecuteAsync(ReviewCommand command) => Input.ExecuteAsync(command);

    Task IContextMenuActions.FitAsync() => Input.ApplyFitAsync();

    Task IContextMenuActions.ZoomToConfiguredLevelAsync() => Input.Pointer.SetClickZoomLevelAsync(_viewModel.Settings.ClickZoomPercent);

    Task IContextMenuActions.ApplyZoomPresetAsync(int percent) => ApplyZoomMenuSelectionAsync(percent);

    Task IContextMenuActions.CustomZoomAsync() => OpenClickZoomCustomDialogAsync();

    void IContextMenuActions.SetZoomAlsoSetsClickLevel(bool value)
    {
        var settings = _viewModel.Settings;
        settings.SetZoomAlsoSetsClickLevel = value;
        SaveSettings(settings, "Could not save the 'also set as click zoom level' setting");
    }

    void IContextMenuActions.SetClickZoomPercent(int percent)
    {
        var settings = _viewModel.Settings;
        settings.ClickZoomPercent = percent;
        SaveSettings(settings, "Could not save the click zoom level setting");
    }

    void IContextMenuActions.OpenInExternalEditor() => _viewModel.OpenInExternalEditor();

    void IContextMenuActions.CopyFileName() => _viewModel.CopyFileName();

    void IContextMenuActions.CopyFullPathname() => _viewModel.CopyFullPathname();

    void IContextMenuActions.ShowSettings() => _viewModel.ShowSettings();

    void IContextMenuActions.ReportMenuFailed(Exception exception) => ReportMenuFailed("ContextMenu", exception);

    /// <summary>
    /// Dựng/hiện menu ném ngoại lệ: ghi <c>shell-errors.log</c> (kể cả khi log chung tắt) + log chung + dòng trạng thái không chặn
    /// "Menu error: ..." (thay vì nuốt im lặng). Có thể gọi từ lúc modal; chỉ chạm view model trên UI thread.
    /// </summary>
    internal void ReportMenuFailed(string context, Exception exception)
    {
        ShellErrorLog.Report(context, exception);
        _log.Error($"{context} failed", exception);
        _viewModel.StatusText = Tr.StatusMenuFailed(UserFacingError.Describe(exception));
    }

    void IContextMenuActions.ReportActionFailed(string actionName, Exception exception) =>
        ((IShellInputHost)this).ReportActionFailed(actionName, exception);
}
