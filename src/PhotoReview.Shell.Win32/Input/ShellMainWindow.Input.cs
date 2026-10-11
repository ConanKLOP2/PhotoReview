using System.ComponentModel;
using PhotoReview.App;
using PhotoReview.App.Input;
using PhotoReview.App.Localization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Win32;

// WP-22: nhập liệu của cửa sổ chính (chuột, phím, wheel, touchpad, kéo-thả). Bản Win32 của các handler Window_KeyDown /
// ImageScroll_PreviewMouseWheel / MainImage_Preview* / Window_Drop của MainWindow WPF; phần quyết định ở Input/ShellInput.cs.
internal sealed partial class ShellMainWindow : IShellInputHost
{
    private ShellInput? _input;
    private ShellDropTarget? _dropTarget;
    private EventHandler<AppSettings>? _onSettingsChangedForInput;
    private PropertyChangedEventHandler? _onViewModelChangedForInput;

    /// <summary>
    /// Nhập liệu của cửa sổ (controller con trỏ/Fit/phím tắt). Điểm cắm của các gói sau: <see cref="ShellInput.OverlayHitTest"/>,
    /// <see cref="ShellInput.ActivationKeyOwned"/>, <see cref="ShellInput.EscapeInterceptor"/>, <see cref="ShellInput.PointerActivity"/>,
    /// <see cref="ShellInput.ExecuteAsync"/> (menu/nút chạy cùng đường lệnh với phím).
    /// </summary>
    internal ShellInput Input => _input ?? throw new InvalidOperationException("Input is not registered.");

    /// <summary>
    /// Ngưỡng kéo (SM_CXDRAG theo DPI cửa sổ) và vị trí con trỏ thật cho <see cref="ViewportController"/> (thẻ WP-16 để lại cho
    /// WP-22 nối interop). Hai hàm đọc DPI hiện tại mỗi lần gọi nên đổi màn hình khác DPI vẫn đúng.
    /// </summary>
    private static ViewportControllerOptions CreateViewportOptions(ShellWindow window, IDisplayClock? displayClock) => new()
    {
        DisplayClock = displayClock,
        DragMetricsPixels = () => InputNative.DragMetricsPixels(InputNative.DpiOf(window.DpiScale)),
        CursorScreenPixel = InputNative.CursorScreenPixel,
    };

    /// <summary>Đích kéo-thả đã đăng ký với OLE (test gọi Drop với data object giả); null sau khi cửa sổ đóng.</summary>
    internal ShellDropTarget? DropTarget => _dropTarget;

    /// <summary>Kho cài đặt mà cửa sổ nghe Changed (test lưu cài đặt để kiểm bảng phím tắt được dựng lại).</summary>
    internal SettingsStore? SettingsStore => _options.SettingsStore;

    private void RegisterInput()
    {
        var settingsStore = _options.SettingsStore;
        _viewModel.Settings = settingsStore?.Current ?? _viewModel.Settings; // R2-F-27: một đối tượng cài đặt cho cả cửa sổ
        _input = new ShellInput(_window, _viewport, _viewModel.Viewer, () => _viewModel.Settings, this, _options.InputEnvironment, _log);

        if (settingsStore is not null)
        {
            _onSettingsChangedForInput = (_, settings) =>
            {
                _viewModel.Settings = settings;
                _input.RebuildShortcuts(settings);
            };
            settingsStore.Changed += _onSettingsChangedForInput;
        }

        // feat/mouse-zoom: điều hướng dừng đà (đổi bản đầy đủ của cùng ảnh thì không).
        _onViewModelChangedForInput = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentIndex))
            {
                _input.NoteCurrentIndexChanged(_viewModel.CurrentIndex);
            }
        };
        _viewModel.PropertyChanged += _onViewModelChangedForInput;

        _dropTarget = new ShellDropTarget(OpenDroppedPathAsync, ReportDropFailure);
        if (!_dropTarget.Register(_window.Hwnd))
        {
            _log.Warn("Drag and drop is unavailable (RegisterDragDrop failed).");
        }

        _window.Closed += OnWindowClosedForInput;
    }

    private void OnWindowClosedForInput(object? sender, EventArgs e)
    {
        _window.Closed -= OnWindowClosedForInput;
        if (_onSettingsChangedForInput is { } handler && _options.SettingsStore is { } store)
        {
            store.Changed -= handler;
        }

        if (_onViewModelChangedForInput is { } vmHandler)
        {
            _viewModel.PropertyChanged -= vmHandler;
        }

        // Window_Closed: dừng đà (hạ hook khung render) và kết thúc pan; kéo-thả thôi nhận khi cửa sổ còn HWND.
        _dropTarget?.Dispose();
        _dropTarget = null;
        _input?.Dispose();
    }

    /// <summary><c>Window_Drop</c>: file đầu tiên của lần thả vào <c>OpenPathAsync</c>.</summary>
    private Task OpenDroppedPathAsync(string path) => _viewModel.OpenPathAsync(path);

    private void ReportDropFailure(Exception exception)
    {
        _log.Error("Drop open failed", exception);
        _viewModel.StatusText = StatusFormatter.FolderOpenFailed(UserFacingError.Describe(exception));
    }

    // IShellInputHost ------------------------------------------------------------------------

    bool IShellInputHost.HasImages => _viewModel.HasImages;

    bool IShellInputHost.IsFullscreen => _viewModel.Viewer.IsFullscreen;

    bool IShellInputHost.IsCompareVisible => _viewModel.Compare.IsVisible;

    bool IShellInputHost.CurrentHasComparePair => _viewModel.CurrentHasComparePair;

    bool IShellInputHost.CurrentHasCapturePair => _viewModel.CurrentHasCapturePair;

    Task IShellInputHost.NextAsync() => _viewModel.NextAsync();

    Task IShellInputHost.PreviousAsync() => _viewModel.PreviousAsync();

    void IShellInputHost.ZoomActualSize() => _viewModel.ZoomActualSize();

    bool IShellInputHost.CancelDuplicateCheck() => _viewModel.CancelDuplicateCheck();

    void IShellInputHost.ReportActionFailed(string actionName, Exception exception)
    {
        _log.Error($"{actionName} failed", exception);
        _viewModel.StatusText = StatusFormatter.ActionFailed(actionName, UserFacingError.Describe(exception));
    }

    /// <summary>Các nhánh không thuộc viewport của <c>MainWindow.ExecuteReviewCommandAsync</c>.</summary>
    async Task IShellInputHost.ExecuteAsync(ReviewCommand command)
    {
        switch (command.Type)
        {
            case ReviewCommandType.Fullscreen:
                _viewModel.ToggleFullscreen();
                break;
            case ReviewCommandType.ExitFullscreen:
                _viewModel.ExitFullscreen();
                break;
            case ReviewCommandType.Close:
                _window.Close();
                break;
            case ReviewCommandType.NextFolder:
                await RunGuardedAsync(Tr.MainMenuNextFolder, () => _viewModel.NavigateSiblingFolderAsync(1));
                break;
            case ReviewCommandType.PreviousFolder:
                await RunGuardedAsync(Tr.MainMenuPreviousFolder, () => _viewModel.NavigateSiblingFolderAsync(-1));
                break;
            case ReviewCommandType.FirstImage:
                await _viewModel.FirstAsync();
                break;
            case ReviewCommandType.LastImage:
                await _viewModel.LastAsync();
                break;
            case ReviewCommandType.ToggleInfoOverlay:
                _viewModel.ToggleInfoOverlay();
                break;
            case ReviewCommandType.Undo:
                await _viewModel.UndoAsync();
                break;
            case ReviewCommandType.ToggleCompare:
                _viewModel.ToggleCompare();
                break;
            case ReviewCommandType.ToggleCaptureMember:
                await _viewModel.ToggleCaptureGroupMemberAsync();
                break;
            case ReviewCommandType.RunAction:
                var actionIndex = command.ActionIndex;
                var profiles = _viewModel.Settings.Actions;
                await RunGuardedAsync(actionIndex >= 0 && actionIndex < profiles.Count ? profiles[actionIndex].Name : string.Empty,
                    () => _viewModel.RunActionAsync(actionIndex));
                break;
            case ReviewCommandType.Recycle:
                await _viewModel.RecycleAsync();
                break;
            case ReviewCommandType.Skip:
                await _viewModel.SkipAsync();
                break;
            case ReviewCommandType.Next:
                await _viewModel.NextAsync();
                break;
            case ReviewCommandType.Previous:
                await _viewModel.PreviousAsync();
                break;
            case ReviewCommandType.MoveToFolder:
                await RunGuardedAsync(Tr.ActionMoveToFolderName, () => _viewModel.MoveToFolderAsync(command.ForcePicker));
                break;
            case ReviewCommandType.CopyToFolder:
                await RunGuardedAsync(Tr.ActionCopyToFolderName, () => _viewModel.CopyToFolderAsync(command.ForcePicker));
                break;
            case ReviewCommandType.ToggleKeepZoom:
                ToggleKeepZoomAcrossImages();
                break;
            case ReviewCommandType.OpenFolder:
                await RunGuardedAsync(Tr.MainMenuOpenFolder.TrimEnd('…', '.', ' '), () => _viewModel.PickAndOpenFolderAsync());
                break;
            case ReviewCommandType.CustomZoom:
                await RunGuardedAsync(Tr.MainMenuZoom, OpenClickZoomCustomDialogAsync);
                break;
            case ReviewCommandType.Refresh:
                RefreshView();
                break;
        }
    }

    /// <summary><c>MainWindow.RunGuardedAsync</c>: lỗi vào log + status "action failed"; huỷ không phải lỗi.</summary>
    private async Task RunGuardedAsync(string actionName, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ((IShellInputHost)this).ReportActionFailed(actionName, ex);
        }
    }

    /// <summary><c>MainWindow.RefreshView</c>: dừng đà để khung tĩnh, render lại tốt nhất; zoom và offset không đổi.</summary>
    internal void RefreshView()
    {
        Input.Pointer.StopKinetic();
        _viewModel.RefreshView();
        _viewport.InvalidateLayout();
        _window.Invalidate();
    }

    /// <summary>Lật và lưu <see cref="AppSettings.KeepZoomAcrossImages"/> (PR-B), cùng mẫu với <c>ToggleInfoOverlay</c>.</summary>
    private void ToggleKeepZoomAcrossImages()
    {
        var settings = _viewModel.Settings;
        settings.KeepZoomAcrossImages = !settings.KeepZoomAcrossImages;
        SaveSettings(settings, "Could not save the keep-zoom setting");
    }

    /// <summary>Q-R43: dùng chung bởi mục "Custom..." của menu và phím tắt CustomZoom.</summary>
    internal async Task OpenClickZoomCustomDialogAsync()
    {
        var percent = _options.ZoomPrompt?.PromptCustomZoom(_viewModel.Settings.ClickZoomPercent);
        if (percent is { } chosen)
        {
            await ApplyZoomMenuSelectionAsync(chosen);
        }
    }

    /// <summary>
    /// Mục preset/Custom của submenu Zoom (PR-C): luôn zoom tới <paramref name="percent"/>; lưu làm
    /// <see cref="AppSettings.ClickZoomPercent"/> khi <see cref="AppSettings.SetZoomAlsoSetsClickLevel"/> bật.
    /// </summary>
    internal async Task ApplyZoomMenuSelectionAsync(int percent)
    {
        if (_viewModel.Settings.SetZoomAlsoSetsClickLevel)
        {
            await ApplyClickZoomLevelAsync(percent);
        }
        else
        {
            await Input.Pointer.SetClickZoomLevelAsync(percent);
        }
    }

    /// <summary>Lưu mức click-zoom mới rồi áp ngay (kể cả khi lưu thất bại: phiên này vẫn zoom).</summary>
    internal async Task ApplyClickZoomLevelAsync(int percent)
    {
        var settings = _viewModel.Settings;
        settings.ClickZoomPercent = percent;
        SaveSettings(settings, "Could not save the click zoom level setting");
        await Input.Pointer.SetClickZoomLevelAsync(percent);
    }

    private void SaveSettings(AppSettings settings, string failureText)
    {
        try
        {
            _options.SettingsStore?.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _log.Error(failureText, ex);
        }
    }
}
