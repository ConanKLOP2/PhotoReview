using PhotoReview.App.Menus;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>FEAT-EXTERNAL-EDITORS: the "Open in External Editor" entry as a dynamic submenu (0 / 1 / N editors, default mark, layout).</summary>
[Trait("Category", "HotPath")]
public sealed class ExternalEditorsMenuModelTests
{
    private static ContextMenuBuildContext Context(AppSettings settings, bool hasImage = true) =>
        new(settings, hasImage, CanUndo: true, IsFit: true, null, CanOpenInExternalEditor: true, CanCopyPath: hasImage, IsCompareVisible: false);

    private static AppSettings Declared(int count, string? defaultId = null)
    {
        var settings = new AppSettings { HiddenContextMenuItems = [] };
        for (var i = 0; i < count; i++)
            settings.ExternalEditors.Add(new ExternalEditor { Id = "e" + i, Name = "Editor " + i, Path = $@"C:\Editors\e{i}.exe" });
        settings.DefaultExternalEditorId = defaultId ?? string.Empty;
        return settings;
    }

    private static MenuItemModel EditorEntry(IReadOnlyList<MenuItemModel> items) =>
        items.Single(i => i.Id == nameof(ContextMenuItemId.ExternalEditor));

    [Fact]
    public void NoEditorDeclared_KeepsTheSingleCommandThatOpensSettings()
    {
        var entry = EditorEntry(ContextMenuModelBuilder.Build(Context(Declared(0))));

        Assert.Equal(MenuItemKind.Command, entry.Kind);
        Assert.Equal(Tr.MainMenuOpenInExternalEditor, entry.Text);
        Assert.Empty(entry.Children);
        Assert.True(entry.IsEnabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    public void EditorsDeclared_BecomeASubmenuWithOneEntryEachThenAddAndSetDefault(int count)
    {
        var entry = EditorEntry(ContextMenuModelBuilder.Build(Context(Declared(count, "e0"))));

        Assert.Equal(MenuItemKind.Submenu, entry.Kind);
        Assert.Equal(count + 3, entry.Children.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(ContextMenuController.ExternalEditorOpenPrefix + "e" + i, entry.Children[i].Id);
            Assert.Equal("Editor " + i, entry.Children[i].Text);
            Assert.Equal(MenuItemKind.Toggle, entry.Children[i].Kind);
        }

        Assert.Equal(MenuItemKind.Separator, entry.Children[count].Kind);
        Assert.Equal(ContextMenuController.ExternalEditorAddId, entry.Children[count + 1].Id);
        Assert.Equal(Tr.MainMenuExternalEditorAdd, entry.Children[count + 1].Text);
        Assert.Equal(ContextMenuController.ExternalEditorSetDefaultId, entry.Children[count + 2].Id);
        Assert.Equal(Tr.MainMenuExternalEditorSetDefault, entry.Children[count + 2].Text);
    }

    [Fact]
    public void ExactlyTheDefaultEditorIsMarked_AndOnlyItCarriesTheDefaultEditorKey()
    {
        var settings = Declared(3, "e1");
        settings.Shortcuts.OpenInDefaultEditor = "F9";

        var children = EditorEntry(ContextMenuModelBuilder.Build(Context(settings))).Children;

        Assert.Equal([false, true, false], children.Take(3).Select(c => c.IsChecked));
        Assert.Equal([null, "F9", null], children.Take(3).Select(c => c.ShortcutText));
    }

    [Fact]
    public void UnknownDefaultId_MarksTheFirstEditor()
    {
        var children = EditorEntry(ContextMenuModelBuilder.Build(Context(Declared(3, "gone")))).Children;

        Assert.Equal([true, false, false], children.Take(3).Select(c => c.IsChecked));
    }

    [Fact]
    public void NoImage_DisablesTheEditorEntries_ButNotAddOrSetDefault()
    {
        var children = EditorEntry(ContextMenuModelBuilder.Build(Context(Declared(2, "e0"), hasImage: false))).Children;

        Assert.All(children.Take(2), c => Assert.False(c.IsEnabled));
        Assert.True(children[3].IsEnabled);
        Assert.True(children[4].IsEnabled);
    }

    [Fact]
    public void LegacyPathOnly_IsOneImplicitEditor()
    {
        var settings = new AppSettings { HiddenContextMenuItems = [], ExternalEditorPath = @"C:\Old\Retoucher.exe" };

        var children = EditorEntry(ContextMenuModelBuilder.Build(Context(settings))).Children;

        Assert.Equal(1 + 3, children.Count);
        Assert.Equal("Retoucher", children[0].Text);
        Assert.True(children[0].IsChecked);
    }

    [Fact]
    public void BlankName_FallsBackToTheProgramFileName()
    {
        var settings = new AppSettings { HiddenContextMenuItems = [] };
        settings.ExternalEditors.Add(new ExternalEditor { Id = "x", Name = " ", Path = @"C:\Tools\Capture One\CaptureOne.exe" });

        var children = EditorEntry(ContextMenuModelBuilder.Build(Context(settings))).Children;

        Assert.Equal("CaptureOne", children[0].Text);
    }

    [Fact]
    public void LayoutHidingTheEntry_RemovesTheWholeSubmenu()
    {
        var settings = Declared(3, "e0");
        ContextMenuItems.ApplyHidden(settings, [ContextMenuItemId.ExternalEditor]);

        var items = ContextMenuModelBuilder.Build(Context(settings));

        Assert.DoesNotContain(items, i => i.Id == nameof(ContextMenuItemId.ExternalEditor));
        Assert.DoesNotContain(items, i => i.Id.StartsWith("ExternalEditor.", StringComparison.Ordinal));
    }
}
