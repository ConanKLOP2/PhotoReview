using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.App.Services;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Integration.Tests.Input;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Integration.Tests.Menus;

/// <summary>
/// WP-24: menu chuột phải của <c>ShellMainWindow</c> THẬT (ShellStartup, HWND thật, WARP). Thông điệp WM_CONTEXTMENU /
/// WM_RBUTTONUP gửi vào cửa sổ qua PostMessage (menu là modal nên không SendMessage); phím cho menu native được post TRƯỚC khi menu mở
/// (vòng lặp modal của TrackPopupMenuEx đọc chúng) - không SendInput, không phụ thuộc kích thước màn hình hay viền vô hình của
/// GetWindowRect (toạ độ lấy từ ClientToScreen). Một nhóm test dùng host kịch bản (chọn id có sẵn) để chạy mọi lệnh qua cửa sổ thật.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellContextMenuEndToEndTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private const nint VkEscape = 0x1B;
    private const nint VkReturn = 0x0D;
    private const nint VkDown = 0x28;
    private const nint VkRight = 0x27;

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref ScreenPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    /// <summary>Host menu quan sát: ghi mô hình/điểm neo/kết quả; có id kịch bản thì trả id đó (không hiện menu), không thì dùng host thật.</summary>
    private sealed class SpyMenuHost(IPopupMenuHost inner, string? scripted) : IPopupMenuHost
    {
        public ConcurrentQueue<IReadOnlyList<MenuItemModel>> Shown { get; } = new();

        public ConcurrentQueue<PointD> Points { get; } = new();

        public ConcurrentQueue<string?> Results { get; } = new();

        public string? Scripted { get; set; } = scripted;

        public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip)
        {
            Shown.Enqueue(items);
            Points.Enqueue(screenPointDip);
            var result = Scripted ?? inner.Show(items, screenPointDip);
            Results.Enqueue(result);
            return result;
        }
    }

    private sealed class FakeZoomPrompt(int? answer) : IZoomPromptService
    {
        public List<int> Asked { get; } = [];

        public int? PromptCustomZoom(int currentPercent)
        {
            Asked.Add(currentPercent);
            return answer;
        }
    }

    private static bool WaitUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, Bound);

    private static string Diag(ShellRun run) => run.Diagnostics;

    private static ShellRun Start(string label, string[] folders, int width, int height, IZoomPromptService? prompt = null)
    {
        var root = ShellRun.NewRoot(label);
        string? first = null;
        foreach (var folder in folders)
        {
            var path = Path.Combine(root, "lib", folder, "image.png");
            TestPng.WriteTo(path, width, height);
            first ??= path;
        }

        var run = ShellRun.Start(root, [first!], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                if (prompt is not null)
                {
                    services.AddSingleton(prompt);
                }
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(run));
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ: " + Diag(run));
        return run;
    }

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(TimeSpan.FromSeconds(90)), "shell không thoát sau WM_CLOSE: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private static SpyMenuHost Spy(ShellRun run, string? scripted = null)
    {
        var main = run.Main!;
        return run.OnUi(() =>
        {
            var spy = new SpyMenuHost(main.ContextMenu.MenuHost, scripted);
            main.ContextMenu.MenuHost = spy;
            return spy;
        });
    }

    private static nint Pack(int x, int y) => (nint)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);

    private static (int X, int Y) ScreenOf(ShellRun run, int clientX, int clientY)
    {
        var point = new ScreenPoint { X = clientX, Y = clientY };
        Assert.True(ClientToScreen(run.Main!.Hwnd, ref point));
        return (point.X, point.Y);
    }

    private static void PostContextMenu(ShellRun run, int clientX, int clientY)
    {
        var (sx, sy) = ScreenOf(run, clientX, clientY);
        Assert.True(User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmContextMenu, run.Main.Hwnd, Pack(sx, sy)));
    }

    private static void PostKeys(ShellRun run, params nint[] keys)
    {
        foreach (var key in keys)
        {
            Assert.True(User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmKeyDown, key, 1));
        }
    }

    private static void EnableEveryMenuItem(ShellRun run) =>
        run.OnUi(() => ContextMenuItems.ApplyHidden(run.Main!.ViewModel.Settings, []));

    private static (int X, int Y) ClientCenterPixels(ShellRun run)
    {
        var (size, dpi) = run.OnUi(() => (run.Main!.Window.ClientSizePixels, run.Main.Window.DpiScale));
        _ = dpi;
        return (size.Width / 2, size.Height / 2);
    }

    // ---- hiện menu ----

    [Fact]
    public void WmContextMenu_OpensTheMenuBuiltFromTheLiveSettings_AtTheClickPointInDip()
    {
        using var run = Start("open", ["a-photos"], 64, 48);
        var scripted = new SpyMenuHost(new NullHost(), null);
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = scripted);
        var (cx, cy) = ClientCenterPixels(run);
        var (sx, sy) = ScreenOf(run, cx, cy);
        var dpi = run.OnUi(() => run.Main!.Window.DpiScale);

        PostContextMenu(run, cx, cy);

        Assert.True(WaitUntil(() => scripted.Shown.Count == 1), "menu không mở");
        Assert.True(scripted.Points.TryPeek(out var point));
        Assert.Equal(sx / dpi, point.X, 3);
        Assert.Equal(sy / dpi, point.Y, 3);
        scripted.Shown.TryPeek(out var items);
        Assert.Equal(["Undo", "Separator.2", "Refresh", "Separator.3", "OpenFolder", "NextFolder", "PreviousFolder", "Separator.4",
            "ExternalEditor", "Separator.5", "CopyFileName", "CopyFullPath", "Separator.6", "Settings"], items!.Select(i => i.Id).ToList());
        Stop(run);
    }

    /// <summary>Host cuối cùng không bao giờ hiện gì (trả null): dùng khi chỉ cần quan sát mô hình.</summary>
    private sealed class NullHost : IPopupMenuHost
    {
        public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip) => null;
    }

    [Fact]
    public void WmRButtonUp_IsTurnedIntoTheMenuByDefWindowProc()
    {
        using var run = Start("rbuttonup", ["a-photos"], 64, 48);
        var host = new SpyMenuHost(new NullHost(), null);
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = host);
        var (cx, cy) = ClientCenterPixels(run);

        User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmRButtonDown, 0x0002, Pack(cx, cy));
        User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmRButtonUp, 0, Pack(cx, cy));

        Assert.True(WaitUntil(() => host.Shown.Count == 1), "nhả nút phải không mở menu");
        Stop(run);
    }

    [Fact]
    public void KeyboardInvocation_LParamMinusOne_OpensAtTheClientCentre()
    {
        using var run = Start("keyboard", ["a-photos"], 64, 48);
        var host = new SpyMenuHost(new NullHost(), null);
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = host);
        var (cx, cy) = ClientCenterPixels(run);
        var (sx, sy) = ScreenOf(run, cx, cy);
        var dpi = run.OnUi(() => run.Main!.Window.DpiScale);

        User32.PostMessage(run.Main!.Hwnd, WindowMessages.WmContextMenu, run.Main.Hwnd, Pack(-1, -1));

        Assert.True(WaitUntil(() => host.Shown.Count == 1));
        host.Points.TryPeek(out var point);
        // giữa client theo pixel nguyên: sai số tối đa 1 pixel do làm tròn chia đôi
        Assert.InRange(point.X * dpi, sx - 1.5, sx + 1.5);
        Assert.InRange(point.Y * dpi, sy - 1.5, sy + 1.5);
        Stop(run);
    }

    [Fact]
    public void RightClickOutsideTheClientArea_DoesNotOpenTheAppMenu()
    {
        using var run = Start("nonclient", ["a-photos"], 64, 48);
        var host = new SpyMenuHost(new NullHost(), null);
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = host);
        var (ox, oy) = ScreenOf(run, 0, 0);

        // Gửi trực tiếp tới handler của cửa sổ: điểm 40 px phía trên gốc client (thanh tiêu đề) không phải client.
        var handled = run.OnUi(() =>
        {
            var handlerWindow = run.Main!.Window;
            var message = new PhotoReview.Shell.Win32.Hosting.WindowMessage(handlerWindow.Hwnd, WindowMessages.WmContextMenu,
                handlerWindow.Hwnd, Pack(ox + 50, oy - 40));
            var outside = new ContextMenuMessageHandler(handlerWindow, run.Main.ContextMenu);
            return outside.TryHandle(message, out _);
        });

        Assert.False(handled);
        Assert.Empty(host.Shown);
        Stop(run);
    }

    // ---- menu native thật ----

    [Fact]
    public void NativeMenu_EscapeClosesTheMenu_RunsNothing_AndKeepsTheWindowAlive()
    {
        using var run = Start("escape", ["a-photos"], 64, 48);
        var host = Spy(run);
        var (cx, cy) = ClientCenterPixels(run);

        PostContextMenu(run, cx, cy);
        PostKeys(run, VkEscape);

        Assert.True(WaitUntil(() => host.Results.Count == 1), "menu không đóng");
        host.Results.TryPeek(out var result);
        Assert.Null(result);
        Assert.False(run.OnUi(() => run.Main!.ContextMenu.IsOpen));
        Assert.False(run.HasExited, "Esc của menu không được đóng cửa sổ");
        Stop(run);
    }

    [Fact]
    public void NativeMenu_ArrowsAndEnter_RunTheChosenCommand_NextFolder()
    {
        using var run = Start("nextfolder", ["a-photos", "b-photos"], 64, 48);
        var host = Spy(run);
        var (cx, cy) = ClientCenterPixels(run);
        Assert.Contains("a-photos", run.OnUi(() => run.Main!.ViewModel.FolderText));

        PostContextMenu(run, cx, cy);
        // Mặc định: Undo, Refresh, Open folder, Next folder -> 4 lần Xuống.
        PostKeys(run, VkDown, VkDown, VkDown, VkDown, VkReturn);

        Assert.True(WaitUntil(() => host.Results.Count == 1));
        host.Results.TryPeek(out var result);
        Assert.Equal("NextFolder", result);
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.FolderText).Contains("b-photos", StringComparison.Ordinal)),
            "Next folder không chuyển sang thư mục kế");
        Stop(run);
    }

    [Fact]
    public void NativeMenu_ZoomSubmenu_PresetFromTheSubmenu_ZoomsAndSavesTheClickLevel()
    {
        using var run = Start("zoompreset", ["a-photos"], 3000, 2000);
        EnableEveryMenuItem(run);
        var host = Spy(run);
        var (cx, cy) = ClientCenterPixels(run);

        PostContextMenu(run, cx, cy);
        // Undo, Recycle, Fit, Zoom to N %, Zoom > : 5 lần Xuống tới Zoom, Phải mở submenu; trong submenu
        // Fit width, Fit width 2, Fit height, 50, 70, 100, 150, 200 -> preset 200 là mục thứ 8 (đã ở mục đầu sau Phải).
        PostKeys(run, VkDown, VkDown, VkDown, VkDown, VkDown, VkRight, VkDown, VkDown, VkDown, VkDown, VkDown, VkDown, VkDown, VkReturn);

        Assert.True(WaitUntil(() => host.Results.Count == 1));
        host.Results.TryPeek(out var result);
        Assert.Equal("Zoom.Preset.200", result);
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Viewer.DisplayZoomPercent == 200)), "preset 200 không zoom");
        Assert.Equal(200, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));
        Stop(run);
    }

    // ---- lệnh theo id, qua cửa sổ thật ----

    private static void Choose(ShellRun run, SpyMenuHost host, string id)
    {
        var before = host.Results.Count;
        host.Scripted = id;
        var (cx, cy) = ClientCenterPixels(run);
        PostContextMenu(run, cx, cy);
        Assert.True(WaitUntil(() => host.Results.Count == before + 1), $"{id}: menu không mở");
    }

    [Fact]
    public void ZoomToLevel_ZoomsToTheConfiguredLevel_WithoutChangingIt()
    {
        using var run = Start("zoomtolevel", ["a-photos"], 3000, 2000);
        EnableEveryMenuItem(run);
        run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent = 150);
        var host = Spy(run, "ZoomToLevel");

        Choose(run, host, "ZoomToLevel");

        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Viewer.DisplayZoomPercent == 150)));
        Assert.Equal(150, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));
        Stop(run);
    }

    [Theory]
    [InlineData(true, 300)]
    [InlineData(false, 100)]
    public void ZoomPreset_SavesTheClickLevelOnlyWhenAlsoSetIsOn(bool alsoSet, int expectedClickLevel)
    {
        using var run = Start("alsoset" + alsoSet, ["a-photos"], 3000, 2000);
        EnableEveryMenuItem(run);
        run.OnUi(() => run.Main!.ViewModel.Settings.SetZoomAlsoSetsClickLevel = alsoSet);
        var host = Spy(run, "Zoom.Preset.300");

        Choose(run, host, "Zoom.Preset.300");

        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Viewer.DisplayZoomPercent == 300)));
        Assert.Equal(expectedClickLevel, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));
        Stop(run);
    }

    [Fact]
    public void CustomZoom_AsksThePromptWithTheCurrentLevel_AndAppliesTheAnswer()
    {
        var prompt = new FakeZoomPrompt(250);
        using var run = Start("custom", ["a-photos"], 3000, 2000, prompt);
        EnableEveryMenuItem(run);
        var host = Spy(run, "Zoom.Custom");

        Choose(run, host, "Zoom.Custom");

        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Viewer.DisplayZoomPercent == 250)), "mức tuỳ chọn không được áp");
        Assert.Equal([100], prompt.Asked);
        Assert.Equal(250, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));
        Stop(run);
    }

    [Fact]
    public void CustomZoom_CancelledPrompt_ChangesNothing()
    {
        var prompt = new FakeZoomPrompt(null);
        using var run = Start("customcancel", ["a-photos"], 3000, 2000, prompt);
        EnableEveryMenuItem(run);
        var host = Spy(run, "Zoom.Custom");

        Choose(run, host, "Zoom.Custom");

        Assert.True(WaitUntil(() => prompt.Asked.Count == 1));
        Assert.True(run.OnUi(() => run.Main!.ViewModel.Viewer.IsFit));
        Assert.Equal(100, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));
        Stop(run);
    }

    [Fact]
    public void SetCurrentAsClickLevel_SavesTheCurrentZoom_AndIsDisabledInFit()
    {
        using var run = Start("setcurrent", ["a-photos"], 3000, 2000);
        EnableEveryMenuItem(run);
        var host = Spy(run, "Zoom.SetCurrentAsClickLevel");
        var (cx, cy) = ClientCenterPixels(run);

        // Đang Fit: mục tắt trong mô hình, chọn nó (kịch bản) cũng không đổi gì.
        PostContextMenu(run, cx, cy);
        Assert.True(WaitUntil(() => host.Results.Count == 1));
        host.Shown.TryPeek(out var fitModel);
        Assert.False(fitModel!.Single(i => i.Id == "ZoomSubmenu").Children.Single(c => c.Id == "Zoom.SetCurrentAsClickLevel").IsEnabled);
        Assert.Equal(100, run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent));

        run.OnUi(() => run.Main!.ViewModel.Viewer.SetZoom(1.75));
        Choose(run, host, "Zoom.SetCurrentAsClickLevel");

        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Settings.ClickZoomPercent) == 175));
        Assert.Equal(1.75, run.OnUi(() => run.Main!.ViewModel.Viewer.Zoom), 6); // không zoom lại
        Stop(run);
    }

    [Fact]
    public void AlsoSetClickLevel_FlipsAndPersistsTheSetting()
    {
        using var run = Start("alsoflip", ["a-photos"], 64, 48);
        EnableEveryMenuItem(run);
        var host = Spy(run, "Zoom.AlsoSetClickLevel");
        Assert.True(run.OnUi(() => run.Main!.ViewModel.Settings.SetZoomAlsoSetsClickLevel));

        Choose(run, host, "Zoom.AlsoSetClickLevel");

        Assert.True(WaitUntil(() => !run.OnUi(() => run.Main!.ViewModel.Settings.SetZoomAlsoSetsClickLevel)));
        Choose(run, host, "Zoom.AlsoSetClickLevel");
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Settings.SetZoomAlsoSetsClickLevel)));
        Stop(run);
    }

    [Fact]
    public void Fit_ReturnsToFit_FromAZoomedView()
    {
        using var run = Start("fit", ["a-photos"], 3000, 2000);
        EnableEveryMenuItem(run);
        run.OnUi(() => run.Main!.ViewModel.Viewer.SetZoom(2.0));
        Assert.False(run.OnUi(() => run.Main!.ViewModel.Viewer.IsFit));
        var host = Spy(run, "Fit");

        Choose(run, host, "Fit");

        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.ViewModel.Viewer.IsFit)), "Fit không về chế độ vừa khung");
        Stop(run);
    }

    [Fact]
    public void NoFolderOpen_TheMenuStillOpens_WithoutTheCopyItems()
    {
        var root = ShellRun.NewRoot("nophoto");
        using var run = ShellRun.Start(root, []);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(run));
        var host = new SpyMenuHost(new NullHost(), null);
        run.OnUi(() => run.Main!.ContextMenu.MenuHost = host);
        var (cx, cy) = ClientCenterPixels(run);

        PostContextMenu(run, cx, cy);

        Assert.True(WaitUntil(() => host.Shown.Count == 1), "menu không mở khi chưa có thư mục");
        host.Shown.TryPeek(out var items);
        Assert.Equal(["Undo", "Separator.2", "Refresh", "Separator.3", "OpenFolder", "NextFolder", "PreviousFolder", "Separator.4",
            "ExternalEditor", "Separator.6", "Settings"], items!.Select(i => i.Id).ToList());
        Stop(run);
    }

    [Fact]
    public void MenuReflectsLiveSettingsChange_BetweenTwoOpenings()
    {
        using var run = Start("live", ["a-photos"], 64, 48);
        var host = Spy(run, "Refresh");
        Choose(run, host, "Refresh");
        host.Shown.TryDequeue(out var first);
        Assert.DoesNotContain(first!, i => i.Id == "MoveToRecycleBin");

        EnableEveryMenuItem(run);
        Choose(run, host, "Refresh");

        host.Shown.TryDequeue(out var second);
        Assert.Contains(second!, i => i.Id == "MoveToRecycleBin");
        Stop(run);
    }
}
