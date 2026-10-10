using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>WP-18: phần thuần của host menu native (chữ, cờ MF_*, đổi command id về id mô hình).</summary>
[Trait("Category", "HotPath")]
public sealed class NativeMenuMappingTests
{
    private static MenuItemModel Item(string text, string? shortcut = null, bool enabled = true, bool isChecked = false) =>
        new("X", MenuItemKind.Command, text, shortcut, enabled, isChecked, [], null);

    [Fact]
    public void DisplayText_WithShortcut_AppendsTabAndShortcut()
    {
        Assert.Equal("Next folder\tPageDown", NativeMenuMapping.DisplayText(Item("Next folder", "PageDown")));
    }

    [Fact]
    public void DisplayText_WithoutShortcut_IsJustTheText()
    {
        Assert.Equal("Settings…", NativeMenuMapping.DisplayText(Item("Settings…")));
    }

    [Fact]
    public void DisplayText_Ampersand_IsDoubledSoItIsNotAMnemonic()
    {
        Assert.Equal("Copy && paste", NativeMenuMapping.DisplayText(Item("Copy & paste")));
    }

    [Fact]
    public void ItemFlags_EnabledUnchecked_IsPlainString()
    {
        Assert.Equal(WindowMessages.MfString, NativeMenuMapping.ItemFlags(Item("a")));
    }

    [Fact]
    public void ItemFlags_DisabledAndChecked_SetsGrayedAndChecked()
    {
        var flags = NativeMenuMapping.ItemFlags(Item("a", enabled: false, isChecked: true));

        Assert.Equal(WindowMessages.MfGrayed | WindowMessages.MfChecked, flags);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "first")]
    [InlineData(2, "second")]
    [InlineData(3, null)]
    [InlineData(-1, null)]
    public void IdForCommand_MapsOneBasedCommandToId(int command, string? expected)
    {
        Assert.Equal(expected, NativeMenuMapping.IdForCommand(["first", "second"], command));
    }
}
