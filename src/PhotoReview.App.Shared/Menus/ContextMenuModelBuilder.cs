using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Menus;

/// <summary>
/// C-12 (WP-18): dựng mô hình menu chuột phải và menu Tools. Luật hiện/ẩn và dấu phân cách lấy từ
/// <see cref="ContextMenuItems.Compute"/> (Core, thuần) nên không nhân đôi quy tắc của <c>MainWindow.ApplyContextMenuLayout</c>.
/// Văn bản/phím tắt/trạng thái chọn sao theo <c>MainWindow.ImageContextMenu_Opened</c> + <c>ZoomMenu_SubmenuOpened</c>
/// (MainWindow WPF không đổi). Quy ước id: tên <see cref="ContextMenuItemId"/> cho mục cấp trên và con trực tiếp,
/// <c>Zoom.Preset.N</c>, <c>Zoom.Custom</c>, <c>Zoom.AlsoSetClickLevel</c>, <c>Zoom.SetCurrentAsClickLevel</c>,
/// <c>Separator.N</c> (trước nhóm N, cấp trên) / <c>Separator.Zoom.N</c> (trong submenu Zoom), <c>Tools.*</c>.
/// </summary>
public static class ContextMenuModelBuilder
{
    private static readonly IReadOnlyList<MenuItemModel> NoChildren = [];

    /// <summary>
    /// Dùng ContextMenuItems.Compute (Core) - cùng luật separator/ẩn hiện với MainWindow.ApplyContextMenuLayout + BuildZoomMenu.
    /// </summary>
    /// <remarks>
    /// Khác biệt có chủ ý so với <see cref="ContextMenuBuildContext"/>: Undo và "Open in External Editor" luôn bật như bản WPF
    /// (click Editor khi chưa cấu hình mở Settings; Undo rỗng là no-op), nên CanUndo/CanOpenInExternalEditor/IsFit/IsCompareVisible
    /// không đổi trạng thái mục. Copy cần ảnh: HasImage và CanCopyPath cùng đúng (= <c>MainViewModel.CanCopyCurrentFilePath</c>).
    /// </remarks>
    public static IReadOnlyList<MenuItemModel> Build(ContextMenuBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var hidden = ContextMenuItems.EffectiveHidden(settings);
        var menuContext = new ContextMenuContext(context.HasImage && context.CanCopyPath);

        var result = new List<MenuItemModel>();
        foreach (var entry in ContextMenuItems.Compute(hidden, menuContext).Entries)
        {
            result.Add(entry.Item is { } id
                ? TopLevelItem(id, context, hidden, menuContext)
                : Separator("Separator." + entry.Group));
        }
        return result;
    }

    /// <summary>Recovery, Diagnostics, Benchmark, ClearCache, RemoveNumbered, RemoveOriginal (cột nút của popup Tools).</summary>
    /// <remarks>Hai mục xoá trùng tắt khi không có ảnh (WPF: nhấn là no-op vì catalog rỗng); các mục khác luôn bật.</remarks>
    public static IReadOnlyList<MenuItemModel> BuildToolsMenu(bool hasImages) =>
    [
        Command("Tools.Recovery", Tr.MainToolsRecovery, null, Tr.MainToolsRecoveryAutomationName),
        Command("Tools.Diagnostics", Tr.MainToolsDiagnostics, null, Tr.MainToolsDiagnosticsAutomationName),
        Command("Tools.Benchmark", Tr.MainToolsBenchmark, null, Tr.MainToolsBenchmarkAutomationName),
        Command("Tools.ClearCache", Tr.MainToolsClearCache, null, Tr.MainToolsClearCacheAutomationName),
        Command("Tools.RemoveNumberedDuplicates", Tr.MainToolsRemoveNumberedDuplicates, null,
            Tr.MainToolsRemoveNumberedDuplicatesAutomationName, isEnabled: hasImages),
        Command("Tools.RemoveOriginalDuplicates", Tr.MainToolsRemoveOriginalDuplicates, null,
            Tr.MainToolsRemoveOriginalDuplicatesAutomationName, isEnabled: hasImages),
    ];

    private static MenuItemModel TopLevelItem(ContextMenuItemId id, ContextMenuBuildContext context,
        IReadOnlySet<ContextMenuItemId> hidden, ContextMenuContext menuContext)
    {
        var settings = context.Settings;
        var shortcuts = settings.Shortcuts;
        return id switch
        {
            ContextMenuItemId.Undo => Command(id, Tr.MainMenuUndo, null, Tr.MainMenuUndoAutomationName),
            ContextMenuItemId.MoveToRecycleBin => Command(id, Tr.MainMenuRecycle, shortcuts.SendToRecycleBin, Tr.MainMenuRecycleAutomationName),
            ContextMenuItemId.Fit => Command(id, Tr.MainMenuClickZoomLevelFit, shortcuts.ToggleFit, Tr.MainMenuClickZoomLevelFitAutomationName),
            ContextMenuItemId.ZoomToLevel => Command(id, Tr.MainMenuZoomToLevel(settings.ClickZoomPercent), shortcuts.ClickZoom,
                Tr.MainMenuZoomToLevelAutomationName(settings.ClickZoomPercent)),
            ContextMenuItemId.ZoomSubmenu => new MenuItemModel(id.ToString(), MenuItemKind.Submenu, Tr.MainMenuZoom, null, true, false,
                ZoomChildren(context, hidden, menuContext), Tr.MainMenuZoomAutomationName),
            ContextMenuItemId.Refresh => Command(id, Tr.MainMenuRefresh, shortcuts.Refresh, Tr.MainMenuRefreshAutomationName),
            ContextMenuItemId.OpenFolder => Command(id, Tr.MainMenuOpenFolder, null, Tr.MainMenuOpenFolderAutomationName),
            ContextMenuItemId.NextFolder => Command(id, Tr.MainMenuNextFolder, shortcuts.NextFolder, Tr.MainMenuNextFolderAutomationName),
            ContextMenuItemId.PreviousFolder => Command(id, Tr.MainMenuPreviousFolder, shortcuts.PreviousFolder, Tr.MainMenuPreviousFolderAutomationName),
            ContextMenuItemId.ExternalEditor => Command(id, Tr.MainMenuOpenInExternalEditor, null, Tr.MainMenuOpenInExternalEditorAutomationName),
            ContextMenuItemId.CopyFileName => Command(id, Tr.MainMenuCopyFileName, null, Tr.MainMenuCopyFileNameAutomationName),
            ContextMenuItemId.CopyFullPath => Command(id, Tr.MainMenuCopyFullPath, null, Tr.MainMenuCopyFullPathAutomationName),
            ContextMenuItemId.Settings => Command(id, Tr.MainMenuSettings, null, Tr.MainMenuSettingsAutomationName),
            _ => throw new InvalidOperationException($"Context menu item {id} is not a top-level item."),
        };
    }

    private static List<MenuItemModel> ZoomChildren(ContextMenuBuildContext context, IReadOnlySet<ContextMenuItemId> hidden,
        ContextMenuContext menuContext)
    {
        var settings = context.Settings;
        var shortcuts = settings.Shortcuts;
        var children = new List<MenuItemModel>();
        foreach (var entry in ContextMenuItems.Compute(hidden, menuContext, ContextMenuItemId.ZoomSubmenu).Entries)
        {
            switch (entry.Item)
            {
                case null:
                    children.Add(Separator("Separator.Zoom." + entry.Group));
                    break;
                case ContextMenuItemId.ZoomFitWidth:
                    children.Add(Command(ContextMenuItemId.ZoomFitWidth, Tr.MainMenuZoomFitWidth, shortcuts.FitWidth, Tr.MainMenuZoomFitWidthAutomationName));
                    break;
                case ContextMenuItemId.ZoomFitWidth2:
                    children.Add(Command(ContextMenuItemId.ZoomFitWidth2, Tr.MainMenuZoomFitWidth2, shortcuts.FitWidth2, Tr.MainMenuZoomFitWidth2AutomationName));
                    break;
                case ContextMenuItemId.ZoomFitHeight:
                    children.Add(Command(ContextMenuItemId.ZoomFitHeight, Tr.MainMenuZoomFitHeight, shortcuts.FitHeight, Tr.MainMenuZoomFitHeightAutomationName));
                    break;
                case ContextMenuItemId.ZoomPresets:
                    foreach (var percent in ZoomMenuRules.PresetPercents)
                    {
                        children.Add(new MenuItemModel("Zoom.Preset." + percent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            MenuItemKind.Toggle, Tr.MainMenuClickZoomLevelPreset(percent), null, true, percent == settings.ClickZoomPercent,
                            NoChildren, Tr.MainMenuClickZoomLevelPresetAutomationName(percent)));
                    }
                    children.Add(Command("Zoom.Custom", Tr.MainMenuClickZoomLevelCustom, null, Tr.MainMenuClickZoomLevelCustomAutomationName));
                    break;
                case ContextMenuItemId.ZoomLevelOptions:
                    children.Add(new MenuItemModel("Zoom.AlsoSetClickLevel", MenuItemKind.Toggle, Tr.MainMenuZoomAlsoSetClickLevel, null, true,
                        settings.SetZoomAlsoSetsClickLevel, NoChildren, Tr.MainMenuZoomAlsoSetClickLevelAutomationName));
                    children.Add(Command("Zoom.SetCurrentAsClickLevel", Tr.MainMenuZoomSetCurrentAsClickLevel, null,
                        Tr.MainMenuZoomSetCurrentAsClickLevelAutomationName,
                        isEnabled: ZoomMenuRules.CalculateClickLevelFromEffectiveZoom(context.EffectiveZoom) is not null));
                    break;
                default:
                    throw new InvalidOperationException($"Context menu item {entry.Item} is not a Zoom submenu item.");
            }
        }
        return children;
    }

    private static MenuItemModel Command(ContextMenuItemId id, string text, string? shortcut, string automationName, bool isEnabled = true) =>
        Command(id.ToString(), text, shortcut, automationName, isEnabled);

    private static MenuItemModel Command(string id, string text, string? shortcut, string automationName, bool isEnabled = true) =>
        new(id, MenuItemKind.Command, text, string.IsNullOrWhiteSpace(shortcut) ? null : shortcut, isEnabled, false, NoChildren, automationName);

    private static MenuItemModel Separator(string id) =>
        new(id, MenuItemKind.Separator, string.Empty, null, false, false, NoChildren, null);
}
