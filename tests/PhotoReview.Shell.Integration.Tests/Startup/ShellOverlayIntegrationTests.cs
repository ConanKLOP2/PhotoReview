using System.Reflection;
using PhotoReview.App.Input;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Overlay;
using PhotoReview.TestSupport;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-23: overlay HUD trong shell THẬT (HWND trên desktop ẩn, WARP, thư mục dữ liệu tạm): toolbar có mặt ở khung đầu, chuột đi
/// qua thông điệp Win32 thật tới nút, đổi cài đặt/ngôn ngữ cập nhật, dọn sạch khi đóng. Chạy tuần tự cùng collection startup.
/// Kích thước cửa sổ không được giả định (runner CI 1024x768; GetWindowRect gồm viền vô hình): chỉ dùng toạ độ DIP của chính
/// overlay quy ra pixel client.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellOverlayIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static bool WaitUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, Bound);

    private static ShellRun StartWithImage(string label, out string root)
    {
        root = ShellRun.NewRoot(label);
        ShellRun run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong");
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ");
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Overlay!.Model.HasImages)), "model overlay không có ảnh");
        return run;
    }

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE");
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private static nint Point(double xDip, double yDip, double dpi) =>
        (nint)(((int)Math.Round(yDip * dpi) << 16) | ((int)Math.Round(xDip * dpi) & 0xFFFF));

    private static void Send(ShellRun run, uint message, double xDip, double yDip)
    {
        double dpi = run.OnUi(() => run.Main!.Window.DpiScale);
        OverlayNative.SendMessage(run.Main!.Hwnd, message, 0, Point(xDip, yDip, dpi));
    }

    private static (double X, double Y) CenterOf(ShellRun run, Func<ShellOverlay, OverlayElement> pick) =>
        run.OnUi(() =>
        {
            RectD bounds = pick(run.Main!.Overlay!).Bounds;
            return (bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        });

    [Fact]
    public void ToolbarIsDrawnFromTheFirstFrame_AtTheTopLeft_AndGoneAfterClose()
    {
        using ShellRun run = StartWithImage("ovl-first", out _);
        ShellOverlay overlay = run.OnUi(() => run.Main!.Overlay!);
        RectD box = run.OnUi(() => overlay.Toolbar.Bounds);
        Assert.Equal(8, box.X, 3);
        Assert.Equal(8, box.Y, 3);
        Assert.InRange(box.Height, 38, 42); // 32 (⋮) + Padding 4+4 (golden: 40)
        Assert.True(box.Width > 100);
        Assert.Equal(1f, run.OnUi(() => overlay.Toolbar.Opacity));

        Stop(run);
        Assert.Null(run.Main!.Overlay);
    }

    [Fact]
    public void RealMouseMessages_ClickTheFitButton_AndTheOverlaySwallowsThePress()
    {
        using ShellRun run = StartWithImage("ovl-click", out _);
        int fits = 0;
        run.OnUi(() => run.Main!.FitHandler = () =>
        {
            fits++;
            return Task.CompletedTask;
        });
        (double x, double y) = CenterOf(run, o => o.Toolbar.Fit);

        Send(run, WindowMessages.WmMouseMove, x, y);
        // Hover không kiểm ở đây: chuột thật không nằm trên cửa sổ nên WM_MOUSELEAVE tự phát ngay sau TrackMouseEvent
        // (hover được kiểm ở unit test của ShellOverlay).
        Send(run, WindowMessages.WmLButtonDown, x, y);
        Assert.True(run.OnUi(() => run.Main!.Overlay!.IsPointerDown));
        Assert.True(run.OnUi(() => run.Main!.Overlay!.Toolbar.Fit.IsPressed));
        Send(run, WindowMessages.WmLButtonUp, x, y);

        Assert.Equal(1, run.OnUi(() => fits));
        Assert.False(run.OnUi(() => run.Main!.Overlay!.IsPointerDown));

        // Nhấn ở giữa ảnh (không có phần tử overlay): overlay không nhận.
        Send(run, WindowMessages.WmLButtonDown, 600, 400);
        Assert.False(run.OnUi(() => run.Main!.Overlay!.IsPointerDown));
        Send(run, WindowMessages.WmLButtonUp, 600, 400);
        Assert.Equal(1, run.OnUi(() => fits));
        Stop(run);
    }

    [Fact]
    public void SettingsSave_ReevaluatesAutoHide_AndTheToolbarFollowsTheMouse()
    {
        using ShellRun run = StartWithImage("ovl-hide", out _);
        SettingsStore store = run.OnUi(() => run.Main!.OverlaySettingsStore!);
        run.OnUi(() =>
        {
            AppSettings next = store.Current;
            next.ToolbarAutoHide = true;
            next.ToolbarAutoHideDelayMs = 50;
            store.Save(next);
        });
        Assert.True(run.OnUi(() => run.Main!.ViewModel.Settings.ToolbarAutoHide), "view model không thấy cài đặt mới");

        Send(run, WindowMessages.WmMouseMove, 700, 450); // xa toolbar
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Overlay!.Toolbar.Opacity) == 0f), "toolbar không tự ẩn");
        Assert.False(run.OnUi(() => run.Main!.Overlay!.Toolbar.IsHitTestVisible));

        Send(run, WindowMessages.WmMouseMove, 20, 20); // vào hot zone
        Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Overlay!.Toolbar.Opacity) == 1f), "toolbar không hiện lại");
        Assert.True(run.OnUi(() => run.Main!.Overlay!.Toolbar.IsHitTestVisible));
        Stop(run);
    }

    [Fact]
    public void LanguageSwitch_UpdatesToolbarTextOnTheUiThread()
    {
        using ShellRun run = StartWithImage("ovl-lang", out _);
        Localizer previous = Localizer.Current;
        try
        {
            Localizer.SetCurrent(TestLocalization.Vietnamese);
            string vietnamese = TestLocalization.Vietnamese.Get("main.toolbar.fit");
            Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Overlay!.Toolbar.Fit.Content) == vietnamese),
                "toolbar không đổi sang tiếng Việt");

            Localizer.SetCurrent(TestLocalization.English);
            string english = TestLocalization.English.Get("main.toolbar.fit");
            Assert.True(WaitUntil(() => run.OnUi(() => run.Main!.Overlay!.Toolbar.Fit.Content) == english),
                "toolbar không đổi lại sang tiếng Anh");
            Assert.NotEqual(vietnamese, english);
        }
        finally
        {
            Localizer.SetCurrent(previous);
        }

        Stop(run);
    }

    [Fact]
    public void Close_UnsubscribesTheOverlayFromTheStaticLocalizerEvent()
    {
        int Handlers()
        {
            FieldInfo field = typeof(Localizer).GetField("CurrentChanged", BindingFlags.NonPublic | BindingFlags.Static)!;
            return ((Delegate?)field.GetValue(null))?.GetInvocationList().Length ?? 0;
        }

        int before = Handlers();
        using ShellRun run = StartWithImage("ovl-leak", out _);
        Assert.True(Handlers() > before, "overlay không nghe Localizer.CurrentChanged");
        Stop(run);
        Assert.Equal(before, Handlers());
    }
}
