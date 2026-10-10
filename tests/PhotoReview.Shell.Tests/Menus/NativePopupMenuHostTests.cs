using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-18: host menu native trên một cửa sổ ẩn (lớp STATIC, không bao giờ hiện). Phím được PostMessage vào hàng đợi TRƯỚC khi
/// mở menu: vòng lặp modal của TrackPopupMenuEx đọc chúng nên menu tự đóng/chọn, không SendInput, không chờ cố định.
/// </summary>
[Trait("Category", "Native")]
public sealed class NativePopupMenuHostTests
{
    private const uint WsPopup = 0x80000000;
    private const nint VkEscape = 0x1B;
    private const nint VkReturn = 0x0D;
    private const nint VkDown = 0x28;
    private const nint VkRight = 0x27;

    private static MenuItemModel Command(string id, string text, bool enabled = true) =>
        new(id, MenuItemKind.Command, text, null, enabled, false, [], null);

    private static MenuItemModel Separator() => new("Separator.1", MenuItemKind.Separator, string.Empty, null, false, false, [], null);

    private static MenuItemModel Submenu(string id, string text, params MenuItemModel[] children) =>
        new(id, MenuItemKind.Submenu, text, null, true, false, children, null);

    private static nint CreateHiddenOwner() =>
        User32.CreateWindowEx(0, "STATIC", "PhotoReview menu test", WsPopup, 0, 0, 10, 10, 0, 0, Kernel32.GetModuleHandle(0), 0);

    private static void PostKeys(nint owner, params nint[] keys)
    {
        foreach (var key in keys)
        {
            Assert.True(User32.PostMessage(owner, WindowMessages.WmKeyDown, key, 0));
        }
    }

    private static string? ShowWithKeys(IReadOnlyList<MenuItemModel> items, params nint[] keys)
    {
        var owner = CreateHiddenOwner();
        Assert.NotEqual(0, owner);
        try
        {
            var host = new NativePopupMenuHost(() => owner);
            PostKeys(owner, keys);
            return host.Show(items, new PointD(10, 10));
        }
        finally
        {
            User32.DestroyWindow(owner);
        }
    }

    [Fact]
    public void Show_EscapePressed_ClosesAndReturnsNull()
    {
        var result = ShowWithKeys([Command("A", "Alpha"), Command("B", "Beta")], VkEscape);

        Assert.Null(result);
    }

    [Fact]
    public void Show_DownThenReturn_ReturnsTheFirstItemId()
    {
        var result = ShowWithKeys([Command("A", "Alpha"), Command("B", "Beta")], VkDown, VkReturn);

        Assert.Equal("A", result);
    }

    [Fact]
    public void Show_DisabledItemChosenWithKeyboard_NeverReturnsItsId()
    {
        var items = new[] { Command("A", "Alpha"), Separator(), Command("Off", "Disabled", enabled: false), Command("B", "Beta") };

        var result = ShowWithKeys(items, VkDown, VkDown, VkReturn);

        Assert.Null(result);
    }

    [Fact]
    public void Show_SubmenuChildChosenWithKeyboard_ReturnsTheChildId()
    {
        var items = new[] { Submenu("Zoom", "Zoom", Command("Zoom.Custom", "Custom…")) };

        var result = ShowWithKeys(items, VkDown, VkRight, VkDown, VkReturn);

        Assert.Equal("Zoom.Custom", result);
    }

    [Fact]
    public void Show_EmptyItems_ReturnsNullWithoutOpeningAMenu()
    {
        var owner = CreateHiddenOwner();
        try
        {
            var host = new NativePopupMenuHost(() => owner);

            Assert.Null(host.Show([], new PointD(0, 0)));
        }
        finally
        {
            User32.DestroyWindow(owner);
        }
    }

    [Fact]
    public void Show_NoOwnerWindow_ReturnsNull()
    {
        var host = new NativePopupMenuHost(() => 0);

        Assert.Null(host.Show([Command("A", "Alpha")], new PointD(0, 0)));
    }
}
