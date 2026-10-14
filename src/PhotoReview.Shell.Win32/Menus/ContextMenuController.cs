using System.Globalization;
using PhotoReview.App;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.Shell.Win32.Menus;

/// <summary>
/// WP-24: những gì menu chuột phải cần từ cửa sổ chính (bản Win32 của các handler <c>*_Click</c> của <c>MainWindow.xaml.cs</c>).
/// Test cung cấp bản giả; cửa sổ thật thực thi ở <c>ShellMainWindow.Menus.cs</c>.
/// </summary>
internal interface IContextMenuActions
{
    /// <summary>Cài đặt hiện hành (đọc lại mỗi lần mở menu: đổi trong Settings hiện ngay, không cần khởi động lại).</summary>
    AppSettings Settings { get; }

    bool HasImage { get; }

    /// <summary><c>MainViewModel.CanCopyCurrentFilePath</c>: mục Copy chỉ hiện khi có ảnh đang mở có đường dẫn.</summary>
    bool CanCopyPath { get; }

    bool CanUndo { get; }

    bool CanOpenInExternalEditor { get; }

    bool IsCompareVisible { get; }

    /// <summary><c>ViewerState.EffectiveZoom</c>: null khi đang Fit (mục "Set current zoom" tắt).</summary>
    double? EffectiveZoom { get; }

    /// <summary>Lệnh chạy chung với phím tắt (<c>ShellInput.ExecuteAsync</c>): Undo, Recycle, Refresh, thư mục, Fit width/height.</summary>
    Task ExecuteAsync(ReviewCommand command);

    /// <summary><c>ClickZoomFit_Click</c>: Fit to window.</summary>
    Task FitAsync();

    /// <summary><c>ZoomToLevel_Click</c>: zoom tới <see cref="AppSettings.ClickZoomPercent"/> (không đổi mức).</summary>
    Task ZoomToConfiguredLevelAsync();

    /// <summary><c>ApplyZoomMenuSelectionAsync</c>: preset của submenu Zoom.</summary>
    Task ApplyZoomPresetAsync(int percent);

    /// <summary><c>OpenClickZoomCustomDialogAsync</c>: mục "Custom..." (hộp thoại qua <c>IZoomPromptService</c>).</summary>
    Task CustomZoomAsync();

    /// <summary><c>AlsoSetClickLevel_Click</c>: ghi <see cref="AppSettings.SetZoomAlsoSetsClickLevel"/> và lưu.</summary>
    void SetZoomAlsoSetsClickLevel(bool value);

    /// <summary><c>SetCurrentZoomAsClickLevel_Click</c>: ghi <see cref="AppSettings.ClickZoomPercent"/> và lưu (không zoom lại).</summary>
    void SetClickZoomPercent(int percent);

    void OpenInExternalEditor();

    /// <summary>FEAT-EXTERNAL-EDITORS: một mục của submenu editor (<c>ExternalEditor.Open.&lt;id&gt;</c>).</summary>
    void OpenInExternalEditor(string editorId) => OpenInExternalEditor();

    /// <summary>FEAT-EXTERNAL-EDITORS: "Add more editors...".</summary>
    void AddExternalEditor()
    {
    }

    /// <summary>FEAT-EXTERNAL-EDITORS: "Set default editor...".</summary>
    void ChooseDefaultExternalEditor()
    {
    }

    void CopyFileName();

    void CopyFullPathname();

    void ShowSettings();

    /// <summary>Một lệnh thất bại: log và hiện "action failed" (như <c>MainWindow.RunGuardedAsync</c>).</summary>
    void ReportActionFailed(string actionName, Exception exception);

    /// <summary>Dựng/hiện menu ném ngoại lệ: shell-errors.log + dòng trạng thái "Menu error: ...". Mặc định như một lệnh thất bại.</summary>
    void ReportMenuFailed(Exception exception) => ReportActionFailed("Menu", exception);
}

/// <summary>
/// WP-24 (NO-WPF-EXEC-PLAN, thẻ WP-24): menu chuột phải của viewer. Dựng mô hình bằng <see cref="ContextMenuModelBuilder"/> từ
/// cài đặt + trạng thái THỜI ĐIỂM MỞ (như <c>ImageContextMenu_Opened</c>), hiện bằng <see cref="IPopupMenuHost"/> (modal), rồi chạy
/// lệnh của id được chọn đúng như handler <c>*_Click</c> của <c>MainWindow</c> WPF. Chỉ dùng trên UI thread.
/// </summary>
internal sealed class ContextMenuController
{
    internal const string ZoomPresetPrefix = "Zoom.Preset.";

    /// <summary>FEAT-EXTERNAL-EDITORS: ids của submenu editor (cùng chuỗi với <c>ContextMenuModelBuilder</c>; test so khớp hai bên).</summary>
    internal const string ExternalEditorOpenPrefix = "ExternalEditor.Open.";
    internal const string ExternalEditorAddId = "ExternalEditor.Add";
    internal const string ExternalEditorSetDefaultId = "ExternalEditor.SetDefault";

    private readonly IContextMenuActions _actions;
    private readonly ILog? _log;

    public ContextMenuController(IContextMenuActions actions, IPopupMenuHost menuHost, ILog? log = null)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        MenuHost = menuHost ?? throw new ArgumentNullException(nameof(menuHost));
        _log = log;
    }

    /// <summary>Host hiện menu (test thay bằng bản giả chọn id; production = <see cref="NativePopupMenuHost"/>).</summary>
    public IPopupMenuHost MenuHost { get; set; }

    /// <summary>True trong lúc menu đang mở (vòng lặp modal): mở lại bị bỏ qua.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Mô hình menu của trạng thái hiện tại (= nội dung <c>ApplyContextMenuLayout</c> + <c>ZoomMenu_SubmenuOpened</c>).</summary>
    public IReadOnlyList<MenuItemModel> BuildModel()
    {
        var context = new ContextMenuBuildContext(_actions.Settings, _actions.HasImage, _actions.CanUndo,
            IsFit: _actions.EffectiveZoom is null, _actions.EffectiveZoom, _actions.CanOpenInExternalEditor, _actions.CanCopyPath,
            _actions.IsCompareVisible);
        return ContextMenuModelBuilder.Build(context);
    }

    /// <summary>
    /// Mở menu tại <paramref name="screenPointDip"/> (DIP của cửa sổ chủ, toạ độ màn hình) và chạy lệnh được chọn. Phần hiện menu
    /// đồng bộ (modal); Task trả về là việc chạy lệnh (đã bắt lỗi: không bao giờ ném). Menu đang mở hoặc rỗng thì không làm gì.
    /// </summary>
    public Task ShowAsync(PointD screenPointDip)
    {
        if (IsOpen)
        {
            return Task.CompletedTask;
        }

        string? chosen;
        IsOpen = true;
        try
        {
            var items = BuildModel();
            chosen = items.Count == 0 ? null : MenuHost.Show(items, screenPointDip);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Trước đây ngoại lệ bay ra WndProc của WM_CONTEXTMENU (menu "lỗi" mà không dấu vết): ghi lại và báo trạng thái.
            chosen = null;
            _actions.ReportMenuFailed(ex);
        }
        finally
        {
            IsOpen = false;
        }

        return chosen is null ? Task.CompletedTask : ExecuteGuardedAsync(chosen);
    }

    /// <summary>Chạy lệnh của một id mô hình (tên quy ước ở <see cref="ContextMenuModelBuilder"/>).</summary>
    public Task ExecuteAsync(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id switch
        {
            nameof(ContextMenuItemId.Undo) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.Undo)),
            nameof(ContextMenuItemId.MoveToRecycleBin) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.Recycle)),
            nameof(ContextMenuItemId.Fit) => _actions.FitAsync(),
            nameof(ContextMenuItemId.ZoomToLevel) => GuardedAsync(Tr.MainMenuZoom, _actions.ZoomToConfiguredLevelAsync),
            nameof(ContextMenuItemId.ZoomFitWidth) =>
                GuardedAsync(Tr.MainMenuZoom, () => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.FitWidth))),
            nameof(ContextMenuItemId.ZoomFitWidth2) =>
                GuardedAsync(Tr.MainMenuZoom, () => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.FitWidth2))),
            nameof(ContextMenuItemId.ZoomFitHeight) =>
                GuardedAsync(Tr.MainMenuZoom, () => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.FitHeight))),
            nameof(ContextMenuItemId.Refresh) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.Refresh)),
            nameof(ContextMenuItemId.OpenFolder) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.OpenFolder)),
            nameof(ContextMenuItemId.NextFolder) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.NextFolder)),
            nameof(ContextMenuItemId.PreviousFolder) => _actions.ExecuteAsync(new ReviewCommand(ReviewCommandType.PreviousFolder)),
            nameof(ContextMenuItemId.ExternalEditor) => Run(_actions.OpenInExternalEditor),
            nameof(ContextMenuItemId.CopyFileName) => Run(_actions.CopyFileName),
            nameof(ContextMenuItemId.CopyFullPath) => Run(_actions.CopyFullPathname),
            nameof(ContextMenuItemId.Settings) => Run(_actions.ShowSettings),
            ExternalEditorAddId => Run(_actions.AddExternalEditor),
            ExternalEditorSetDefaultId => Run(_actions.ChooseDefaultExternalEditor),
            _ when id.StartsWith(ExternalEditorOpenPrefix, StringComparison.Ordinal) =>
                Run(() => _actions.OpenInExternalEditor(id[ExternalEditorOpenPrefix.Length..])),
            "Zoom.Custom" => GuardedAsync(Tr.MainMenuZoom, _actions.CustomZoomAsync),
            "Zoom.AlsoSetClickLevel" => Run(() => _actions.SetZoomAlsoSetsClickLevel(!_actions.Settings.SetZoomAlsoSetsClickLevel)),
            "Zoom.SetCurrentAsClickLevel" => Run(SetCurrentZoomAsClickLevel),
            _ when TryParsePreset(id, out var percent) => GuardedAsync(Tr.MainMenuZoom, () => _actions.ApplyZoomPresetAsync(percent)),
            _ => Task.CompletedTask,
        };
    }

    private static bool TryParsePreset(string id, out int percent)
    {
        percent = 0;
        return id.StartsWith(ZoomPresetPrefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(ZoomPresetPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out percent);
    }

    /// <summary>"Set current zoom as click level": mức làm tròn + kẹp từ zoom thực; null (đang Fit) thì không làm gì.</summary>
    private void SetCurrentZoomAsClickLevel()
    {
        if (ZoomMenuRules.CalculateClickLevelFromEffectiveZoom(_actions.EffectiveZoom) is { } percent)
        {
            _actions.SetClickZoomPercent(percent);
        }
    }

    private Task Run(Action action) => GuardedAsync(string.Empty, () =>
    {
        action();
        return Task.CompletedTask;
    });

    private Task ExecuteGuardedAsync(string id) => GuardedAsync(id, () => ExecuteAsync(id));

    private async Task GuardedAsync(string actionName, Func<Task> action)
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
            _log?.Error($"Context menu command {actionName} failed", ex);
            _actions.ReportActionFailed(actionName, ex);
        }
    }
}
