using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Integration.Tests.Input;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.TestSupport;

namespace PhotoReview.Shell.Integration.Tests.Menus;

/// <summary>
/// Menu chuột phải và menu Tools (⋮) của shell THẬT được chụp pixel (PrintWindow cửa sổ popup #32768): nền phải TỐI và chữ SÁNG
/// tương phản. Bắt lỗi "menu chỉ hiện màu trắng, rê chuột vào mới thấy chữ" (ForceDark uxtheme ordinal nửa vời trên Windows 11 build
/// 26300: chữ sáng trên nền trắng). Không phụ thuộc build OS: menu do app tự vẽ (owner-draw). Cũng kiểm icon của cửa sổ và đường
/// lỗi menu (shell-errors.log + dòng trạng thái "Menu error").
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellMenuRenderingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const nint VkEscape = 0x1B;
    private const nint VkDown = 0x28;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref ScreenPoint point);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static ShellRun Start(string label)
    {
        var root = ShellRun.NewRoot(label);
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_TEST_CONFIG") is { Length: > 0 } config && File.Exists(config))
        {
            // Chẩn đoán trên máy thật: chạy với BẢN SAO cấu hình của người dùng (đọc, không ghi lại bản gốc).
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.Copy(config, Path.Combine(root, "data", "config.json"));
        }

        var png = Path.Combine(root, "lib", "a", "image.png");
        TestPng.WriteTo(png, 64, 48);
        var run = ShellRun.Start(root, [png]);
        Assert.True(run.WaitForMain(), run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), run.Diagnostics);
        return run;
    }

    private static void PostContextMenuAtCenter(ShellRun run)
    {
        var size = run.OnUi(() => run.Main!.Window.ClientSizePixels);
        var pt = new ScreenPoint { X = size.Width / 2, Y = size.Height / 2 };
        Assert.True(ClientToScreen(run.Main!.Hwnd, ref pt));
        nint pack = (nint)(((uint)(ushort)(short)pt.Y << 16) | (ushort)(short)pt.X);
        Assert.True(User32.PostMessage(run.Main.Hwnd, WindowMessages.WmContextMenu, run.Main.Hwnd, pack));
    }

    /// <summary>
    /// Chờ menu mở, (tuỳ chọn) gửi phím, chụp lại tới khi <paramref name="ready"/> đúng (PrintWindow đôi khi trả bitmap đen/đang vẽ dở),
    /// rồi đóng bằng Esc. Trả ảnh cuối cùng (kể cả khi hết hạn, để phép kiểm báo số liệu).
    /// </summary>
    private static async Task<MenuCapture.Shot> CaptureAsync(ShellRun run, string name, Func<MenuCapture.Shot, bool> ready, params nint[] keys)
    {
        await Wait.UntilAsync(() => MenuCapture.FindMenuWindow() != 0, "menu mở", Bound);
        var hwnd = MenuCapture.FindMenuWindow();
        foreach (var key in keys)
        {
            Assert.True(User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmKeyDown, key, 1));
        }

        MenuCapture.Shot? last = null;
        try
        {
            await Wait.UntilAsync(() =>
            {
                if (MenuCapture.Capture(hwnd) is { } candidate)
                {
                    last = candidate;
                    return ready(candidate);
                }

                return false;
            }, "menu vẽ xong", Bound);
        }
        catch (TimeoutException)
        {
            // Ảnh cuối cùng đi vào phép kiểm bên dưới để lỗi có số liệu.
        }

        Assert.True(User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmKeyDown, VkEscape, 1));
        await Wait.UntilAsync(() => MenuCapture.FindMenuWindow() == 0, "menu đóng", Bound);
        Assert.NotNull(last);
        if (Environment.GetEnvironmentVariable("PHOTOREVIEW_TEST_MENU_DUMP") is { Length: > 0 } dump)
        {
            Directory.CreateDirectory(dump);
            MenuCapture.SaveBmp(last.Value, Path.Combine(dump, name + ".bmp"));
        }

        return last.Value;
    }

    private static int LightPixels(MenuCapture.Shot shot) => shot.Pixels.Count(p => MenuCapture.Luma(p) > 0.6);

    private static bool TextPainted(MenuCapture.Shot shot) => LightPixels(shot) >= 30;

    private static void AssertDarkReadable(MenuCapture.Shot shot)
    {
        var pixels = shot.Pixels;
        var background = pixels.GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;
        Assert.True(MenuCapture.Luma(background) < 0.25, $"nền menu không tối: {background:X6}");
        Assert.Equal(MenuOwnerDraw.Background, background); // đúng màu app tự vẽ (không phải theme hệ thống)
        var light = LightPixels(shot);
        Assert.True(light >= 30, $"không thấy chữ sáng (chỉ {light} điểm sáng)");
        Assert.True(light < pixels.Length / 5, "quá nhiều điểm sáng: nền trắng?");
        Assert.True(pixels.Max(MenuCapture.Luma) - MenuCapture.Luma(background) > 0.5, "chữ không đủ tương phản với nền");
    }

    [Fact]
    public async Task ContextMenu_IsDarkWithReadableText_WithoutHover()
    {
        using var run = Start("menu-ctx");
        run.OnUi(() => ContextMenuItems.ApplyHidden(run.Main!.ViewModel.Settings, [])); // mọi mục (kể cả submenu Zoom)
        PostContextMenuAtCenter(run);

        AssertDarkReadable(await CaptureAsync(run, "context", TextPainted));
    }

    [Fact]
    public async Task ContextMenu_HighlightsTheKeyboardSelectedItemWithTheHoverColor_KeepingTextReadable()
    {
        using var run = Start("menu-hover");
        PostContextMenuAtCenter(run);
        var hover = MenuOwnerDraw.ItemBackground(MenuOwnerDraw.OdsSelected);

        var shot = await CaptureAsync(run, "context-hover", s => s.Pixels.Count(p => p == hover) > 1500, VkDown);

        var hoverPixels = shot.Pixels.Count(p => p == hover);
        Assert.True(hoverPixels > 1500, $"không thấy mục được chọn tô màu hover ({hoverPixels} điểm)");
        Assert.True(TextPainted(shot), "chữ không đọc được");
    }

    [Fact]
    public async Task ToolsMenu_IsDarkWithReadableText_WithoutHover()
    {
        using var run = Start("menu-tools");
        var dpi = run.OnUi(() => run.Main!.Window.DpiScale);
        var (x, y) = run.OnUi(() =>
        {
            var b = run.Main!.Overlay!.Toolbar.Tools.Bounds;
            return (b.X + (b.Width / 2), b.Y + (b.Height / 2));
        });
        var p = (nint)(((int)Math.Round(y * dpi) << 16) | ((int)Math.Round(x * dpi) & 0xFFFF));
        Assert.True(User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmLButtonDown, 1, p));
        Assert.True(User32.PostMessage(run.Main.Hwnd, WindowMessages.WmLButtonUp, 0, p));

        AssertDarkReadable(await CaptureAsync(run, "tools", TextPainted));
    }

    [Fact]
    public void MainWindow_HasTheAppIcon_BigAndSmall()
    {
        using var run = Start("menu-icon");

        Assert.NotEqual(0, SendMessage(run.Main!.Hwnd, 0x007F, 1, 0)); // WM_GETICON, ICON_BIG
        Assert.NotEqual(0, SendMessage(run.Main.Hwnd, 0x007F, 0, 0)); // ICON_SMALL
    }

    private sealed class ThrowingHost : IPopupMenuHost
    {
        public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip) =>
            throw new InvalidOperationException("menu host exploded");
    }

    [Fact]
    public void ContextMenu_WhenShowingThrows_WritesShellErrorsLogAndShowsAMenuErrorStatus()
    {
        using var run = Start("menu-error");
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = new ThrowingHost());

        PostContextMenuAtCenter(run);

        string Status() => run.OnUi(() => run.Main!.ViewModel.StatusText);
        Assert.True(SpinWait.SpinUntil(() => Status().StartsWith("Menu error", StringComparison.Ordinal), Bound),
            "không có dòng trạng thái lỗi menu: " + Status());
        var log = Path.Combine(run.DataRoot, "shell-errors.log");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(log), Bound), "không có shell-errors.log");
        var text = File.ReadAllText(log);
        Assert.Contains("[ContextMenu]", text, StringComparison.Ordinal);
        Assert.Contains("menu host exploded", text, StringComparison.Ordinal);
    }
}
