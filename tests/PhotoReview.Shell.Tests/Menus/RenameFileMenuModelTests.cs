using PhotoReview.App.Menus;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.TestSupport;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>FEAT-RENAME (PR3): the "Rename..." entry of the right-click menu: with / without a photo, shortcut text, hiding, placement.</summary>
[Collection("GlobalState")] // switches the ambient localizer
[Trait("Category", "HotPath")]
public sealed class RenameFileMenuModelTests
{
    private static ContextMenuBuildContext Context(AppSettings settings, bool hasImage = true, bool canCopyPath = true, bool compare = false) =>
        new(settings, hasImage, CanUndo: true, IsFit: true, null, CanOpenInExternalEditor: true, CanCopyPath: canCopyPath, IsCompareVisible: compare);

    private static List<string> Ids(IReadOnlyList<MenuItemModel> items) =>
        [.. items.Select(i => i.Kind == MenuItemKind.Separator ? "-" : i.Id)];

    [Fact]
    public void WithAPhoto_TheEntryIsACommandWithTheF2Text_AndTheLocalizedLabel()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);
        var entry = ContextMenuModelBuilder.Build(Context(new AppSettings())).Single(i => i.Id == nameof(ContextMenuItemId.RenameFile));

        Assert.Equal(MenuItemKind.Command, entry.Kind);
        Assert.Equal("Rename…", entry.Text);
        Assert.Equal("F2", entry.ShortcutText);
        Assert.True(entry.IsEnabled);
        Assert.Empty(entry.Children);
        Assert.False(string.IsNullOrWhiteSpace(entry.AutomationName));
    }

    [Fact]
    public void Vietnamese_HasItsOwnLabel()
    {
        using var scope = TestLocalization.Use(TestLocalization.Vietnamese);
        var entry = ContextMenuModelBuilder.Build(Context(new AppSettings())).Single(i => i.Id == nameof(ContextMenuItemId.RenameFile));

        Assert.Equal("Đổi tên…", entry.Text);
        Assert.Equal(Tr.MainMenuRenameFile, entry.Text);
    }

    [Fact]
    public void WithoutAPhoto_TheEntryIsNotThere_NorIsItsSeparatorLeftBehind()
    {
        var ids = Ids(ContextMenuModelBuilder.Build(Context(new AppSettings { HiddenContextMenuItems = [] }, hasImage: false, canCopyPath: false)));

        Assert.DoesNotContain("RenameFile", ids);
        Assert.DoesNotContain("CopyFileName", ids);
        Assert.Equal(["ExternalEditor", "-", "Settings"], ids.Skip(ids.IndexOf("ExternalEditor"))); // the copy group (its separator) is gone
        Assert.DoesNotContain(ids.Zip(ids.Skip(1)), pair => pair is ("-", "-"));
    }

    [Fact]
    public void AClearedKey_ShowsNoAcceleratorText_ButTheEntryStays()
    {
        var settings = new AppSettings();
        settings.Shortcuts.RenameFile = string.Empty;

        var entry = ContextMenuModelBuilder.Build(Context(settings)).Single(i => i.Id == nameof(ContextMenuItemId.RenameFile));

        Assert.Null(entry.ShortcutText);
        Assert.True(entry.IsEnabled);
    }

    [Fact]
    public void ARebind_IsShownInTheMenu()
    {
        var settings = new AppSettings();
        settings.Shortcuts.RenameFile = "F6";

        var entry = ContextMenuModelBuilder.Build(Context(settings)).Single(i => i.Id == nameof(ContextMenuItemId.RenameFile));

        Assert.Equal("F6", entry.ShortcutText);
    }

    [Fact]
    public void HidingTheItem_RemovesOnlyIt()
    {
        var settings = new AppSettings();
        ContextMenuItems.ApplyHidden(settings, [ContextMenuItemId.RenameFile]);

        var ids = Ids(ContextMenuModelBuilder.Build(Context(settings)));

        Assert.DoesNotContain("RenameFile", ids);
        Assert.Contains("CopyFileName", ids);
        Assert.Contains("CopyFullPath", ids);
    }

    [Fact]
    public void ItSitsInTheCopyGroup_RightBeforeCopyFileName_ByDefault()
    {
        var ids = Ids(ContextMenuModelBuilder.Build(Context(new AppSettings())));

        var rename = ids.IndexOf("RenameFile");
        Assert.True(rename > 0);
        Assert.Equal(["RenameFile", "CopyFileName", "CopyFullPath", "-", "Settings"], ids.Skip(rename));
        Assert.Equal("-", ids[rename - 1]); // its own group starts after the editor group's separator
    }

    [Fact]
    public void CompareVisible_StillOffersIt_TheViewModelRenamesTheSelectedPhoto()
    {
        var entry = ContextMenuModelBuilder.Build(Context(new AppSettings(), compare: true)).Single(i => i.Id == nameof(ContextMenuItemId.RenameFile));

        Assert.True(entry.IsEnabled);
    }

    [Fact]
    public void TheSettingsMousePage_ListsTheItemAsATickBox_WithItsOwnLabel()
    {
        using var scope = TestLocalization.Use(TestLocalization.English);

        var label = PhotoReview.App.Presenters.SettingsPresenter.ContextMenuItemLabel(ContextMenuItemId.RenameFile);

        Assert.Equal(Tr.SettingsContextMenuItemRenameFile, label);
        Assert.NotEqual(nameof(ContextMenuItemId.RenameFile), label); // not the raw enum name fallback
    }
}
