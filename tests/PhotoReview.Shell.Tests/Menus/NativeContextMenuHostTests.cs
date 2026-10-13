using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-24: những điều host menu native phải giữ khi dùng làm menu chuột phải: neo theo DPI (phần thuần), vẽ tay (owner-draw, không uxtheme ordinal), và huỷ mọi HMENU (gốc + submenu) sau khi đóng - bằng Esc hay chọn mục. Cửa sổ chủ là lớp STATIC ẩn; phím
/// được PostMessage trước khi mở (vòng lặp modal của TrackPopupMenuEx đọc chúng).
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe partial class NativeContextMenuHostTests
{
    private const uint WsPopup = 0x80000000;
    private const nint VkEscape = 0x1B;
    private const nint VkReturn = 0x0D;
    private const nint VkDown = 0x28;

    [LibraryImport("user32.dll", EntryPoint = "IsMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsMenu(nint menu);

    private static MenuItemModel Command(string id, string text) => new(id, MenuItemKind.Command, text, null, true, false, [], null);

    private static MenuItemModel Submenu(string id, params MenuItemModel[] children) =>
        new(id, MenuItemKind.Submenu, id, null, true, false, children, null);

    private static readonly MenuItemModel[] ModelWithSubmenu =
    [
        Command("A", "Alpha"),
        Submenu("Zoom", Command("Zoom.Preset.50", "50 %"), Command("Zoom.Preset.100", "100 %")),
        Command("B", "Beta"),
    ];

    private static string? ShowWithKeys(Func<nint, NativePopupMenuHost> createHost, IReadOnlyList<MenuItemModel> items, params nint[] keys)
    {
        var owner = User32.CreateWindowEx(0, "STATIC", "PhotoReview context menu test", WsPopup, 0, 0, 10, 10, 0, 0,
            Kernel32.GetModuleHandle(0), 0);
        Assert.NotEqual(0, owner);
        try
        {
            var host = createHost(owner);
            foreach (var key in keys)
            {
                Assert.True(User32.PostMessage(owner, WindowMessages.WmKeyDown, key, 0));
            }

            return host.Show(items, new PointD(10, 10));
        }
        finally
        {
            User32.DestroyWindow(owner);
        }
    }

    [Theory]
    [InlineData(100.0, 50.0, 96u, 100, 50)]
    [InlineData(100.0, 50.4, 120u, 125, 63)]
    [InlineData(100.0, 50.0, 144u, 150, 75)]
    [InlineData(100.4, 50.4, 192u, 201, 101)]
    [InlineData(-300.0, 8.0, 120u, -375, 10)]
    [InlineData(100.0, 50.0, 0u, 100, 50)]
    public void ToScreenPixels_ScalesTheDipPointByTheWindowDpi(double x, double y, uint dpi, int expectedX, int expectedY)
    {
        var (px, py) = NativePopupMenuHost.ToScreenPixels(new PointD(x, y), dpi);

        Assert.Equal(expectedX, px);
        Assert.Equal(expectedY, py);
    }

    [Fact]
    public void Show_Cancelled_DestroysTheRootAndTheSubmenuHandles()
    {
        var created = new List<nint>();

        var result = ShowWithKeys(owner => new NativePopupMenuHost(() => owner) { MenuCreated = created.Add }, ModelWithSubmenu, VkEscape);

        Assert.Null(result);
        Assert.Equal(2, created.Count); // gốc + submenu Zoom
        Assert.All(created, handle => Assert.False(IsMenu(handle), "HMENU còn sống sau khi menu đóng"));
    }

    [Fact]
    public void Show_ItemChosen_DestroysTheHandlesToo()
    {
        var created = new List<nint>();

        var result = ShowWithKeys(owner => new NativePopupMenuHost(() => owner) { MenuCreated = created.Add }, ModelWithSubmenu,
            VkDown, VkReturn);

        Assert.Equal("A", result);
        Assert.Equal(2, created.Count);
        Assert.All(created, handle => Assert.False(IsMenu(handle)));
    }

    [Fact]
    public void Show_RepeatedOpenAndCancel_NeverAccumulatesLiveMenus()
    {
        var created = new List<nint>();

        for (var i = 0; i < 25; i++)
        {
            ShowWithKeys(owner => new NativePopupMenuHost(() => owner) { MenuCreated = created.Add }, ModelWithSubmenu, VkEscape);
        }

        Assert.Equal(50, created.Count);
        Assert.Equal(0, created.Count(IsMenu));
    }
}
