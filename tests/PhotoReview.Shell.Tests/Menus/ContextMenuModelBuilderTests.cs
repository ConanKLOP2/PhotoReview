using PhotoReview.App.Menus;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-18 (NO-WPF-EXEC-PLAN-WP): mô hình menu chuột phải phải TƯƠNG ĐƯƠNG menu WPF (MainWindow.ImageContextMenu_Opened +
/// ApplyContextMenuLayout + BuildZoomMenu): cùng thứ tự id, cùng dấu phân cách, cùng văn bản/phím tắt/trạng thái chọn.
/// Luật hiện/ẩn lấy từ <see cref="ContextMenuItems.Compute"/> - test so với chính hàm đó và với danh sách id viết tay theo XAML.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ContextMenuModelBuilderTests
{
    private static ContextMenuBuildContext Context(AppSettings? settings = null, bool hasImage = true, bool canCopyPath = true,
        double? effectiveZoom = 1.0) =>
        new(settings ?? new AppSettings(), hasImage, CanUndo: true, IsFit: effectiveZoom is null, effectiveZoom,
            CanOpenInExternalEditor: true, canCopyPath, IsCompareVisible: false);

    private static AppSettings Hiding(params ContextMenuItemId[] hidden)
    {
        var settings = new AppSettings();
        ContextMenuItems.ApplyHidden(settings, hidden);
        return settings;
    }

    /// <summary>Id theo thứ tự, mỗi dấu phân cách là "-" (khớp dạng danh sách id của G-MENU, bỏ qua mục con).</summary>
    private static List<string> TopIds(IReadOnlyList<MenuItemModel> items) =>
        [.. items.Select(i => i.Kind == MenuItemKind.Separator ? "-" : i.Id)];

    private static List<string> ZoomIds(IReadOnlyList<MenuItemModel> items) =>
        [.. items.Single(i => i.Id == nameof(ContextMenuItemId.ZoomSubmenu)).Children
            .Select(i => i.Kind == MenuItemKind.Separator ? "-" : i.Id)];

    [Fact]
    public void Build_DefaultSettings_MatchesDefaultWpfMenu()
    {
        // MainWindow.xaml với tập ẩn mặc định (Recycle, Fit, Zoom to N%, Zoom ẩn): Undo | Refresh | thư mục | editor | copy | Settings.
        var items = ContextMenuModelBuilder.Build(Context());

        Assert.Equal(
            ["Undo", "-", "Refresh", "-", "OpenFolder", "NextFolder", "PreviousFolder", "-", "ExternalEditor", "-",
                "CopyFileName", "CopyFullPath", "-", "Settings"],
            TopIds(items));
    }

    [Fact]
    public void Build_NothingHidden_ListsEveryItemInXamlOrderWithZoomSubmenu()
    {
        var settings = Hiding();

        var items = ContextMenuModelBuilder.Build(Context(settings));

        Assert.Equal(
            ["Undo", "MoveToRecycleBin", "-", "Fit", "ZoomToLevel", "ZoomSubmenu", "Refresh", "-", "OpenFolder", "NextFolder",
                "PreviousFolder", "-", "ExternalEditor", "-", "CopyFileName", "CopyFullPath", "-", "Settings"],
            TopIds(items));
        Assert.Equal(
            ["ZoomFitWidth", "ZoomFitWidth2", "ZoomFitHeight", "-", "Zoom.Preset.50", "Zoom.Preset.70", "Zoom.Preset.100",
                "Zoom.Preset.150", "Zoom.Preset.200", "Zoom.Preset.300", "Zoom.Preset.400", "Zoom.Custom", "-",
                "Zoom.AlsoSetClickLevel", "Zoom.SetCurrentAsClickLevel"],
            ZoomIds(items));
    }

    [Fact]
    public void Build_NoPhoto_HidesCopyItemsAndTheirSeparator()
    {
        var items = ContextMenuModelBuilder.Build(Context(Hiding(), hasImage: false, canCopyPath: false));

        Assert.DoesNotContain("CopyFileName", TopIds(items));
        Assert.DoesNotContain("CopyFullPath", TopIds(items));
        Assert.Equal(["ExternalEditor", "-", "Settings"], TopIds(items).Skip(TopIds(items).IndexOf("ExternalEditor")));
    }

    [Fact]
    public void Build_OnlySettingsLeft_IsASingleItemWithoutSeparators()
    {
        var all = ContextMenuItems.Items.Where(i => !i.Locked && i.Parent is null).Select(i => i.Id).ToArray();

        var items = ContextMenuModelBuilder.Build(Context(Hiding(all)));

        Assert.Equal(["Settings"], TopIds(items));
    }

    [Fact]
    public void Build_ZoomSubmenuChildrenHidden_CollapsesSubmenuLikeWpf()
    {
        var settings = Hiding(ContextMenuItemId.ZoomFitWidth, ContextMenuItemId.ZoomFitWidth2, ContextMenuItemId.ZoomFitHeight,
            ContextMenuItemId.ZoomPresets, ContextMenuItemId.ZoomLevelOptions);

        var items = ContextMenuModelBuilder.Build(Context(settings));

        Assert.DoesNotContain("ZoomSubmenu", TopIds(items));
    }

    [Fact]
    public void Build_LegacyFlagsConfig_UsesSameHiddenSetAsWpf()
    {
        // HiddenContextMenuItems == null: bộ cờ cũ quyết định (ShowZoomMenuItems=false ẩn Fit/Zoom, ShowFolderMenuItems=false ẩn thư mục).
        var settings = new AppSettings { HiddenContextMenuItems = null, ShowZoomMenuItems = true, ShowFolderMenuItems = false, ShowRecycleMenuItem = true };

        var items = ContextMenuModelBuilder.Build(Context(settings));

        Assert.Equal(
            ["Undo", "MoveToRecycleBin", "-", "Fit", "ZoomToLevel", "ZoomSubmenu", "Refresh", "-", "ExternalEditor", "-",
                "CopyFileName", "CopyFullPath", "-", "Settings"],
            TopIds(items));
    }

    [Fact]
    public void Build_EveryHiddenSubset_MatchesComputeAndNeverLeavesStraySeparators()
    {
        var unlocked = ContextMenuItems.Items.Where(i => !i.Locked).Select(i => i.Id).ToArray();
        var subsets = 1 << unlocked.Length;
        for (var mask = 0; mask < subsets; mask += 3) // bước 3: phủ đều ~10k tập, mọi bit đều đổi trạng thái
        {
            var hidden = new HashSet<ContextMenuItemId>();
            for (var bit = 0; bit < unlocked.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0) hidden.Add(unlocked[bit]);
            }
            foreach (var hasPhoto in new[] { true, false })
            {
                var settings = new AppSettings();
                ContextMenuItems.ApplyHidden(settings, hidden);
                var items = ContextMenuModelBuilder.Build(Context(settings, hasImage: hasPhoto, canCopyPath: hasPhoto));

                var expected = ContextMenuItems.Compute(ContextMenuItems.EffectiveHidden(settings), new ContextMenuContext(hasPhoto));
                Assert.Equal(expected.Entries.Select(e => e.Item?.ToString() ?? "-"), TopIds(items));
                AssertNoStraySeparators(items);
                AssertNoEmptySubmenu(items);
            }
        }
    }

    private static void AssertNoStraySeparators(IReadOnlyList<MenuItemModel> items)
    {
        if (items.Count == 0) return;
        Assert.NotEqual(MenuItemKind.Separator, items[0].Kind);
        Assert.NotEqual(MenuItemKind.Separator, items[^1].Kind);
        for (var i = 1; i < items.Count; i++)
        {
            Assert.False(items[i].Kind == MenuItemKind.Separator && items[i - 1].Kind == MenuItemKind.Separator, "double separator");
        }
        foreach (var submenu in items.Where(i => i.Kind == MenuItemKind.Submenu)) AssertNoStraySeparators(submenu.Children);
    }

    private static void AssertNoEmptySubmenu(IReadOnlyList<MenuItemModel> items)
    {
        foreach (var submenu in items.Where(i => i.Kind == MenuItemKind.Submenu))
        {
            Assert.NotEmpty(submenu.Children);
        }
    }

    [Fact]
    public void Build_TextShortcutAndAutomationName_AreReadFromSettingsAndTr()
    {
        var settings = Hiding();
        settings.ClickZoomPercent = 150;
        settings.Shortcuts.ClickZoom = "D3";
        settings.Shortcuts.Refresh = string.Empty; // cleared shortcut -> no accelerator text, like InputGestureText = ""

        var items = ContextMenuModelBuilder.Build(Context(settings));

        var zoomTo = items.Single(i => i.Id == "ZoomToLevel");
        Assert.Equal(Tr.MainMenuZoomToLevel(150), zoomTo.Text);
        Assert.Equal(Tr.MainMenuZoomToLevelAutomationName(150), zoomTo.AutomationName);
        Assert.Equal("D3", zoomTo.ShortcutText);
        Assert.Null(items.Single(i => i.Id == "Refresh").ShortcutText);
        Assert.Equal(settings.Shortcuts.SendToRecycleBin, items.Single(i => i.Id == "MoveToRecycleBin").ShortcutText);
        Assert.Equal(settings.Shortcuts.ToggleFit, items.Single(i => i.Id == "Fit").ShortcutText);
        Assert.Equal(settings.Shortcuts.NextFolder, items.Single(i => i.Id == "NextFolder").ShortcutText);
        Assert.Equal(settings.Shortcuts.PreviousFolder, items.Single(i => i.Id == "PreviousFolder").ShortcutText);
        Assert.Null(items.Single(i => i.Id == "Undo").ShortcutText);
        Assert.Equal(Tr.MainMenuUndo, items.Single(i => i.Id == "Undo").Text);
        Assert.Equal(Tr.MainMenuSettingsAutomationName, items.Single(i => i.Id == "Settings").AutomationName);
    }

    [Fact]
    public void Build_ZoomSubmenu_ChecksTheClickLevelPresetAndReflectsAlsoSetToggle()
    {
        var settings = Hiding();
        settings.ClickZoomPercent = 200;
        settings.SetZoomAlsoSetsClickLevel = false;
        settings.Shortcuts.FitWidth2 = "D4";

        var items = ContextMenuModelBuilder.Build(Context(settings));

        var zoom = items.Single(i => i.Id == "ZoomSubmenu");
        Assert.Equal(MenuItemKind.Submenu, zoom.Kind);
        var presets = zoom.Children.Where(c => c.Id.StartsWith("Zoom.Preset.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Zoom.Preset.200"], presets.Where(p => p.IsChecked).Select(p => p.Id));
        Assert.All(presets, p => Assert.Equal(MenuItemKind.Toggle, p.Kind));
        Assert.Equal(Tr.MainMenuClickZoomLevelPreset(150), presets.Single(p => p.Id == "Zoom.Preset.150").Text);
        Assert.False(zoom.Children.Single(c => c.Id == "Zoom.AlsoSetClickLevel").IsChecked);
        Assert.Equal("D4", zoom.Children.Single(c => c.Id == "ZoomFitWidth2").ShortcutText);
        Assert.Equal(Tr.MainMenuClickZoomLevelCustom, zoom.Children.Single(c => c.Id == "Zoom.Custom").Text);
    }

    [Fact]
    public void Build_CustomClickLevelNotAPreset_ChecksNoPreset()
    {
        var settings = Hiding();
        settings.ClickZoomPercent = 123;

        var zoom = ContextMenuModelBuilder.Build(Context(settings)).Single(i => i.Id == "ZoomSubmenu");

        Assert.DoesNotContain(zoom.Children, c => c.IsChecked && c.Id.StartsWith("Zoom.Preset.", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SetCurrentZoom_IsDisabledInFitAndEnabledOtherwise()
    {
        var settings = Hiding();

        var fit = ContextMenuModelBuilder.Build(Context(settings, effectiveZoom: null)).Single(i => i.Id == "ZoomSubmenu");
        var zoomed = ContextMenuModelBuilder.Build(Context(settings, effectiveZoom: 1.5)).Single(i => i.Id == "ZoomSubmenu");

        Assert.False(fit.Children.Single(c => c.Id == "Zoom.SetCurrentAsClickLevel").IsEnabled);
        Assert.True(zoomed.Children.Single(c => c.Id == "Zoom.SetCurrentAsClickLevel").IsEnabled);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(1.5, 150)]
    [InlineData(0.004, AppSettings.MinClickZoomPercent)]
    [InlineData(100.0, AppSettings.MaxClickZoomPercent)]
    [InlineData(0.125, 13)] // 12,5 % làm tròn ra xa 0 như MainWindowHelpers
    public void CalculateClickLevelFromEffectiveZoom_RoundsAndClamps(double? zoom, int? expected)
    {
        Assert.Equal(expected, ZoomMenuRules.CalculateClickLevelFromEffectiveZoom(zoom));
    }

    [Fact]
    public void Build_NonFiniteEffectiveZoom_IsTreatedAsFit()
    {
        Assert.Null(ZoomMenuRules.CalculateClickLevelFromEffectiveZoom(double.NaN));
    }

    [Fact]
    public void Build_UndoAndExternalEditor_StayEnabledLikeWpf()
    {
        var context = new ContextMenuBuildContext(new AppSettings(), HasImage: true, CanUndo: false, IsFit: true, EffectiveZoom: null,
            CanOpenInExternalEditor: false, CanCopyPath: true, IsCompareVisible: true);

        var items = ContextMenuModelBuilder.Build(context);

        Assert.True(items.Single(i => i.Id == "Undo").IsEnabled);
        Assert.True(items.Single(i => i.Id == "ExternalEditor").IsEnabled);
    }

    [Fact]
    public void BuildToolsMenu_WithImages_ListsSixCommandsInToolbarOrder()
    {
        var items = ContextMenuModelBuilder.BuildToolsMenu(hasImages: true);

        Assert.Equal(
            ["Tools.Recovery", "Tools.Diagnostics", "Tools.Benchmark", "Tools.ClearCache", "Tools.RemoveNumberedDuplicates",
                "Tools.RemoveOriginalDuplicates"],
            items.Select(i => i.Id));
        Assert.All(items, i =>
        {
            Assert.Equal(MenuItemKind.Command, i.Kind);
            Assert.True(i.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(i.Text));
            Assert.False(string.IsNullOrWhiteSpace(i.AutomationName));
        });
        Assert.Equal(Tr.MainToolsRecovery, items[0].Text);
    }

    [Fact]
    public void BuildToolsMenu_WithoutImages_DisablesOnlyTheDuplicateRemovals()
    {
        var items = ContextMenuModelBuilder.BuildToolsMenu(hasImages: false);

        Assert.Equal(
            ["Tools.RemoveNumberedDuplicates", "Tools.RemoveOriginalDuplicates"],
            items.Where(i => !i.IsEnabled).Select(i => i.Id));
    }

    [Fact]
    public void Build_AllIdsAreUniqueAcrossTheWholeTree()
    {
        var items = ContextMenuModelBuilder.Build(Context(Hiding()));

        var ids = Flatten(items).Where(i => i.Kind != MenuItemKind.Separator).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<MenuItemModel> Flatten(IEnumerable<MenuItemModel> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children)) yield return child;
        }
    }
}
