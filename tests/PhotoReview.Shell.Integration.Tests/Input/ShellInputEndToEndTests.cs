using System.Diagnostics;
using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Input;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Integration.Tests.Input;

/// <summary>
/// WP-22: nhập liệu của <c>ShellMainWindow</c> THẬT (ShellStartup, HWND thật, WARP): thông điệp WM_KEYDOWN/WM_MOUSEWHEEL/
/// WM_MBUTTONDOWN gửi vào cửa sổ, đi qua ba handler, controller con trỏ/Fit/phím tắt thật tới MainViewModel. Port các test cửa
/// sổ WPF <c>KeyboardZoomFrameTests</c> và <c>KeepZoomAcrossImagesIntegrationTests</c>. Chạy tuần tự cùng collection với startup.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed partial class ShellInputEndToEndTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint hwnd, ref ScreenPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null) => SpinWait.SpinUntil(condition, timeout ?? Bound);

    private static string Diag(ShellRun run) => run.Diagnostics;

    private static ShellRun StartWithImages(string label, params (string Name, int Width, int Height)[] images) =>
        StartWithImages(label, configure: null, images);

    private static ShellRun StartWithImages(string label, Func<ShellStartupOptions, ShellStartupOptions>? configure,
        params (string Name, int Width, int Height)[] images)
    {
        var root = ShellRun.NewRoot(label);
        var folder = Path.Combine(root, "photos");
        string? first = null;
        foreach (var (name, width, height) in images)
        {
            var path = Path.Combine(folder, name);
            if (name.EndsWith(".jpg", StringComparison.Ordinal))
            {
                TestJpeg.WriteTo(path);
            }
            else
            {
                TestPng.WriteTo(path, width, height);
            }

            first ??= path;
        }

        var run = ShellRun.Start(root, [first!], configure: configure);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(run));
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ: " + Diag(run));
        return run;
    }

    private static void StopAndAssertClean(ShellRun run)
    {
        run.RequestClose();
        // Có thể đang giải mã ảnh lớn ở nền (runner CI 2 nhân + WARP): chờ lâu hơn bound mặc định, vẫn có giới hạn.
        Assert.True(run.WaitForExit(TimeSpan.FromSeconds(90)), "shell không thoát sau WM_CLOSE: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private static nint Pack(int x, int y) => (nint)(((uint)(ushort)y << 16) | (ushort)x);

    private static void Key(ShellRun run, KeyId key, bool repeat = false)
    {
        long lParam = 1 | (repeat ? 0x40000000L : 0);
        PendingUser32.SendMessage(run.Main!.Hwnd, WindowMessages.WmKeyDown, KeyIdMapping.ToVirtualKey(key), (nint)lParam);
    }

    private static (int X, int Y) ClientToScreenPixels(nint hwnd, int clientX, int clientY)
    {
        var point = new ScreenPoint { X = clientX, Y = clientY };
        Assert.True(ClientToScreen(hwnd, ref point));
        return (point.X, point.Y);
    }

    private static void Wheel(ShellRun run, int delta, int clientX, int clientY, bool ctrl = false)
    {
        var hwnd = run.Main!.Hwnd;
        var (sx, sy) = ClientToScreenPixels(hwnd, clientX, clientY);
        var wParam = (nint)(((uint)(ushort)(short)delta << 16) | (uint)(ctrl ? 0x0008 : 0));
        PendingUser32.SendMessage(hwnd, WindowMessages.WmMouseWheel, wParam, Pack(sx, sy));
    }

    // ---- điều hướng bằng phím ----

    [Fact]
    public void ArrowKeysHomeEnd_NavigateTheFolder_ThroughRealMessages()
    {
        using var run = StartWithImages("keys-nav", ("a.jpg", 0, 0), ("b.jpg", 0, 0), ("c.jpg", 0, 0));
        var main = run.Main!;
        int Index() => run.OnUi(() => main.ViewModel.CurrentIndex);

        Key(run, KeyId.Right);
        Assert.True(WaitUntil(() => Index() == 1), "Right không sang ảnh kế");
        Key(run, KeyId.Right);
        Assert.True(WaitUntil(() => Index() == 2));
        Key(run, KeyId.Left);
        Assert.True(WaitUntil(() => Index() == 1));
        Key(run, KeyId.End);
        Assert.True(WaitUntil(() => Index() == 2));
        Key(run, KeyId.Home);
        Assert.True(WaitUntil(() => Index() == 0));
        StopAndAssertClean(run);
    }

    [Fact]
    public void Escape_ClosesTheWindow_AndF11TogglesTheViewerFullscreenState()
    {
        using var run = StartWithImages("keys-esc", ("a.jpg", 0, 0));
        var main = run.Main!;
        bool Fullscreen() => run.OnUi(() => main.ViewModel.Viewer.IsFullscreen);

        Key(run, KeyId.F11);
        Assert.True(WaitUntil(Fullscreen), "F11 không bật fullscreen");
        Key(run, KeyId.Escape); // Esc khi fullscreen: thoát fullscreen, KHÔNG đóng cửa sổ
        Assert.True(WaitUntil(() => !Fullscreen()));
        Assert.False(run.HasExited);

        Key(run, KeyId.Escape); // Esc thường: đóng cửa sổ
        Assert.True(run.WaitForExit(Bound), "Esc không đóng cửa sổ");
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    // ---- chuột ----

    [Fact]
    public void Wheel_ZoomsAtTheCursor_ThroughRealMessages_KeepingThePointUnderIt()
    {
        using var run = StartWithImages("wheel", ("big.png", 3000, 2000)); // sau một bước zoom ảnh vẫn rộng hơn viewport: neo giữ được
        var main = run.Main!;
        var (clientPixels, dpi) = run.OnUi(() => (main.Window.ClientSizePixels, main.Window.DpiScale));
        var (px, py) = (clientPixels.Width / 3, clientPixels.Height / 3); // pixel client (đơn vị của lParam)
        var (x, y) = (px / dpi, py / dpi);                                  // cùng điểm theo DIP (đơn vị của viewport)
        var (fractionBefore, zoomBefore) = run.OnUi(() =>
        {
            var p = main.Viewport.ToImageElement(new PointD(x, y));
            return ((p.X / main.Viewport.ImageActualWidth, p.Y / main.Viewport.ImageActualHeight), main.ViewModel.Viewer.Zoom);
        });

        Wheel(run, 120, px, py);

        Assert.True(WaitUntil(() => run.OnUi(() => !main.ViewModel.Viewer.IsFit && main.ViewModel.Viewer.Zoom != zoomBefore)), "wheel không zoom");
        Assert.True(WaitUntil(() => run.OnUi(() => !main.Viewport.IsLayoutPosted)));
        var fractionAfter = run.OnUi(() =>
        {
            var p = main.Viewport.ToImageElement(new PointD(x, y));
            return (p.X / main.Viewport.ImageActualWidth, p.Y / main.Viewport.ImageActualHeight);
        });
        Assert.Equal(fractionBefore.Item1, fractionAfter.Item1, 2);
        Assert.Equal(fractionBefore.Item2, fractionAfter.Item2, 2);
        StopAndAssertClean(run);
    }

    [Fact]
    public void MiddleClick_DefaultAction_ZoomsToActualSize()
    {
        using var run = StartWithImages("middle", ("big.png", 2000, 3000));
        var main = run.Main!;
        var client = run.OnUi(() => main.Window.ClientSizeDip);

        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmMButtonDown, 0, Pack((int)client.Width / 2, (int)client.Height / 2));

        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Viewer.DisplayZoomPercent == 100)), "nút giữa không zoom 100 %");
        Assert.False(run.OnUi(() => main.ViewModel.Viewer.IsFit));
        StopAndAssertClean(run);
    }

    [Fact]
    public void ClickToZoom_LeftPressRelease_ZoomsInAndOutAgain_ThroughRealCapture()
    {
        using var run = StartWithImages("click", ("big.png", 2000, 3000));
        var main = run.Main!;
        run.OnUi(() => main.ViewModel.Settings.ClickToZoomEnabled = true);
        var (cx, cy) = run.OnUi(() =>
        {
            var o = main.Viewport.ImageOrigin;
            var dpi = main.Window.DpiScale;
            return ((int)((o.X + (main.Viewport.ImageActualWidth / 2)) * dpi), (int)((o.Y + (main.Viewport.ImageActualHeight / 2)) * dpi));
        });
        var hwnd = main.Hwnd;

        PendingUser32.SendMessage(hwnd, WindowMessages.WmLButtonDown, 0x0001, Pack(cx, cy));
        Assert.True(run.OnUi(() => main.Window.HasPointerCapture), "nhấn trái trên ảnh phải bắt chuột");
        PendingUser32.SendMessage(hwnd, WindowMessages.WmLButtonUp, 0, Pack(cx, cy));
        Assert.False(run.OnUi(() => main.Window.HasPointerCapture));

        Assert.True(WaitUntil(() => run.OnUi(() => !main.ViewModel.Viewer.IsFit)), "click không zoom");
        StopAndAssertClean(run);
    }

    // ---- port: KeyboardZoomFrameTests ----

    private sealed record Frame(double ImageWidth, double HorizontalOffset, double VerticalOffset);

    [Fact]
    public void KeyboardZoom_NeverPresentsAFrameAtTheNewSizeWithTheOldScrollOffsets()
    {
        // Báo cáo "ghosting 1-2/10 lần zoom bàn phím": zoom áp trong một lượt dispatcher, cuộn neo sau lượt render nên một khung
        // được trình bày ở kích thước mới với offset cũ. Ghi trạng thái layout của MỌI khung shell trình bày (sau Present).
        using var run = StartWithImages("zoomframes", ("portrait.png", 2000, 3000));
        var main = run.Main!;
        var frames = new List<Frame>();
        var gate = new object();
        EventHandler<PhotoReview.App.Input.FrameTickEventArgs> onRender = (_, _) =>
        {
            var frame = new Frame(main.Viewport.ImageActualWidth, main.Viewport.HorizontalOffset, main.Viewport.VerticalOffset);
            lock (gate)
            {
                frames.Add(frame);
            }
        };
        run.OnUi(() => main.Window.Render += onRender);
        try
        {
            foreach (var key in new[] { KeyId.D1, KeyId.Add, KeyId.Add, KeyId.Subtract })
            {
                var before = run.OnUi(() => main.Viewport.ImageActualWidth);
                lock (gate)
                {
                    frames.Clear();
                }

                Key(run, key);
                Assert.True(WaitUntil(() => run.OnUi(() => Math.Abs(main.Viewport.ImageActualWidth - before) > 0.5 && !main.Viewport.IsLayoutPosted)),
                    $"{key} không zoom");
                // nhường thêm vài khung để mọi khung của lượt này đã được trình bày
                var seen = 0;
                lock (gate)
                {
                    seen = frames.Count;
                }

                run.OnUi(main.Window.Invalidate);
                Assert.True(WaitUntil(() =>
                {
                    lock (gate)
                    {
                        return frames.Count > seen;
                    }
                }));

                var final = run.OnUi(() => new Frame(main.Viewport.ImageActualWidth, main.Viewport.HorizontalOffset, main.Viewport.VerticalOffset));
                List<Frame> atNewSize;
                lock (gate)
                {
                    atNewSize = [.. frames.Where(f => Math.Abs(f.ImageWidth - final.ImageWidth) < 0.5)];
                }

                Assert.NotEmpty(atNewSize);
                foreach (var f in atNewSize)
                {
                    Assert.True(Math.Abs(f.HorizontalOffset - final.HorizontalOffset) < 1 && Math.Abs(f.VerticalOffset - final.VerticalOffset) < 1,
                        $"{key}: khung ở kích thước mới {f.ImageWidth:F0} có offset ({f.HorizontalOffset:F0},{f.VerticalOffset:F0}); cuối ({final.HorizontalOffset:F0},{final.VerticalOffset:F0})");
                }
            }
        }
        finally
        {
            run.OnUi(() => main.Window.Render -= onRender);
        }

        StopAndAssertClean(run);
    }

    // ---- port: KeepZoomAcrossImagesIntegrationTests ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeepZoomAcrossImages_DecidesWhetherNavigationKeepsTheZoom(bool keep)
    {
        using var run = StartWithImages("keepzoom", ("a.png", 16, 16), ("b.png", 16, 16));
        var main = run.Main!;
        run.OnUi(() =>
        {
            main.ViewModel.Settings.KeepZoomAcrossImages = keep;
            main.ViewModel.Viewer.SetZoom(2.0);
        });
        Assert.False(run.OnUi(() => main.ViewModel.Viewer.IsFit));

        run.OnUi(() => _ = main.ViewModel.NextAsync());
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.CurrentIndex == 1)), "ảnh thứ hai không hiện");

        if (keep)
        {
            Assert.True(WaitUntil(() => run.OnUi(() => !main.ViewModel.Viewer.IsFit && Math.Abs(main.ViewModel.Viewer.Zoom - 2.0) < 1e-6)),
                "KeepZoomAcrossImages=true không được đặt lại view khi điều hướng");
        }
        else
        {
            Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Viewer.IsFit)),
                "KeepZoomAcrossImages=false phải về Fit khi điều hướng");
        }

        StopAndAssertClean(run);
    }

    // ---- kéo-thả ----

    [Fact]
    public void DropTarget_IsRegisteredWithOle_AndADropOpensTheFirstFile()
    {
        using var run = StartWithImages("drop", ("a.jpg", 0, 0));
        var main = run.Main!;
        var other = TestJpeg.WriteTo(Path.Combine(run.Root, "other", "z.jpg"));
        Assert.NotNull(run.OnUi(() => main.DropTarget));
        // Đã đăng ký: đích thứ hai trên cùng HWND bị OLE từ chối (DRAGDROP_E_ALREADYREGISTERED).
        var second = new ShellDropTarget(_ => Task.CompletedTask, _ => { });
        Assert.False(run.OnUi(() => second.Register(main.Hwnd)));
        run.OnUi(second.Dispose);

        var data = new FakeDropData([other, Path.Combine(run.Root, "ignored.jpg")]);
        var effect = PhotoReview.Shell.Interop.Shell.DropEffect.None;
        run.OnUi(() =>
        {
            var local = PhotoReview.Shell.Interop.Shell.DropEffect.None;
            main.DropTarget!.Drop(data, 0, default, ref local);
            effect = local;
        });

        Assert.Equal(PhotoReview.Shell.Interop.Shell.DropEffect.Copy, effect);
        Assert.True(WaitUntil(() => run.OnUi(() => string.Equals(main.ViewModel.Catalog.Current?.Path, other, StringComparison.OrdinalIgnoreCase))),
            "thả file không mở file đầu tiên");
        StopAndAssertClean(run);
    }

    // ---- cài đặt đổi: bảng phím tắt dựng lại ----

    [Fact]
    public void SettingsChanged_RebuildsTheShortcutTable()
    {
        using var run = StartWithImages("rebind", ("a.jpg", 0, 0));
        var main = run.Main!;
        var store = run.OnUi(() => main.SettingsStore);
        Assert.NotNull(store);
        bool Overlay() => run.OnUi(() => main.ViewModel.Settings.ShowInfoOverlay);
        var initial = Overlay();

        run.OnUi(() =>
        {
            var settings = store!.Current;
            settings.Shortcuts.ToggleInfoOverlay = "J";
            store.Save(settings);
        });
        Key(run, KeyId.J);
        Assert.True(WaitUntil(() => Overlay() != initial), "phím J (mới gán) không bật/tắt overlay");
        Key(run, KeyId.I); // phím cũ không còn gán: không đổi gì
        Key(run, KeyId.J);
        Assert.True(WaitUntil(() => Overlay() == initial), "I vẫn còn gán, hoặc J không còn đổi overlay");
        StopAndAssertClean(run);
    }

    // ---- nối interop vào ViewportController ----

    [Fact]
    public void ViewportController_UsesTheSystemDragThreshold_AtTheWindowDpi()
    {
        using var run = StartWithImages("drag", ("a.jpg", 0, 0));
        var main = run.Main!;
        var (threshold, dpi) = run.OnUi(() => (main.Viewport.DragThreshold, main.Window.DpiScale));

        var system = InputNative.DragMetricsPixels(InputNative.DpiOf(dpi));

        Assert.Equal(system.Horizontal / dpi, threshold.Horizontal, 9);
        Assert.Equal(system.Vertical / dpi, threshold.Vertical, 9);
        StopAndAssertClean(run);
    }

    // ---- điều hướng dừng đà; Skip/ToggleInfoOverlay mặc định ----

    [Fact]
    public void Navigation_StopsAKineticGlide()
    {
        using var run = StartWithImages("glidenav", ("a.png", 2400, 1600), ("b.png", 2400, 1600));
        var main = run.Main!;
        run.OnUi(() => main.ViewModel.Settings.KineticPanEnabled = true);
        Key(run, KeyId.D1);
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Viewer.DisplayZoomPercent == 100 && !main.Viewport.IsLayoutPosted)));
        for (var i = 0; i < 12; i++)
        {
            Key(run, KeyId.Right, repeat: i > 0); // xung lượng cộng dồn: đà dài
        }

        run.OnUi(() => _ = main.ViewModel.NextAsync());
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.CurrentIndex == 1)));

        Assert.False(run.OnUi(() => main.Input.Pointer.StopKinetic()), "điều hướng phải dừng đà đang chạy");
        StopAndAssertClean(run);
    }

    [Fact]
    public void SpaceSkips_AndIToggleTheInfoOverlay_WithTheDefaultShortcuts()
    {
        using var run = StartWithImages("defaults", ("a.jpg", 0, 0), ("b.jpg", 0, 0));
        var main = run.Main!;
        var overlay = run.OnUi(() => main.ViewModel.Settings.ShowInfoOverlay);

        Key(run, KeyId.I);
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Settings.ShowInfoOverlay) != overlay), "phím I không đổi overlay");
        Key(run, KeyId.Space);
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.CurrentIndex) == 1), "Space (Skip) không sang ảnh kế");
        StopAndAssertClean(run);
    }

    [Fact]
    public void KeyK_TogglesKeepZoomAcrossImages_AndSavesItThroughTheStore()
    {
        using var run = StartWithImages("keepkey", ("a.jpg", 0, 0));
        var main = run.Main!;
        var initial = run.OnUi(() => main.ViewModel.Settings.KeepZoomAcrossImages);

        Key(run, KeyId.K);

        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Settings.KeepZoomAcrossImages) != initial), "phím K không đổi KeepZoomAcrossImages");
        var saved = File.ReadAllText(run.Paths.ConfigFile);
        Assert.Contains($"\"KeepZoomAcrossImages\": {(!initial).ToString().ToLowerInvariant()}", saved, StringComparison.OrdinalIgnoreCase);
        StopAndAssertClean(run);
    }

    // ---- độ trễ input -> khung ----

    [Fact]
    public void InputLatency_ArrowPan_IsMeasuredAtTheHandler_TheLayout_AndTheFrame()
    {
        // PHOTOREVIEW_WP22_GPU=1: GPU thật thay WARP + cửa sổ topmost (chạy trên desktop tương tác của máy đo; fragment WP-22).
        var gpu = Environment.GetEnvironmentVariable("PHOTOREVIEW_WP22_GPU") == "1";
        using var run = StartWithImages("latency", options => options with { PreferWarp = !gpu, Activate = gpu }, ("big.png", 2000, 3000));
        var main = run.Main!;
        if (gpu)
        {
            // Đo trên màn hình thật: cửa sổ phải thấy được (không bị che) thì Present(1) mới theo vblank thật.
            run.OnUi(() => User32.SetWindowPos(main.Hwnd, WindowMessages.HwndTopMost, 0, 0, 0, 0,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoSize | WindowMessages.SwpNoActivate));
        }

        Key(run, KeyId.D1);
        Assert.True(WaitUntil(() => run.OnUi(() => main.ViewModel.Viewer.DisplayZoomPercent == 100 && !main.Viewport.IsLayoutPosted)));

        var layoutDone = new ManualResetEventSlim();
        var presented = new ManualResetEventSlim();
        long layoutAt = 0;
        long frameAt = 0;
        EventHandler onLayout = (_, _) =>
        {
            if (!layoutDone.IsSet)
            {
                layoutAt = Stopwatch.GetTimestamp();
                layoutDone.Set();
            }
        };
        EventHandler<PhotoReview.App.Input.FrameTickEventArgs> onRender = (_, _) =>
        {
            if (layoutDone.IsSet && !presented.IsSet)
            {
                frameAt = Stopwatch.GetTimestamp();
                presented.Set();
            }
        };
        run.OnUi(() =>
        {
            main.Viewport.LayoutUpdated += onLayout;
            main.Window.Render += onRender;
        });

        var send = new List<double>();
        var layout = new List<double>();
        var frame = new List<double>();
        try
        {
            var directions = new[] { KeyId.Right, KeyId.Down, KeyId.Left, KeyId.Up };
            for (var i = 0; i < 60; i++)
            {
                layoutDone.Reset();
                presented.Reset();
                var started = Stopwatch.GetTimestamp();
                Key(run, directions[i % directions.Length]); // SendMessage: trả về khi handler đã chạy xong
                send.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                Assert.True(layoutDone.Wait(Bound), "layout không chạy sau phím");
                layout.Add(Stopwatch.GetElapsedTime(started, layoutAt).TotalMilliseconds);
                Assert.True(presented.Wait(Bound), "không có khung nào được trình bày sau phím");
                frame.Add(Stopwatch.GetElapsedTime(started, frameAt).TotalMilliseconds);
            }
        }
        finally
        {
            run.OnUi(() =>
            {
                main.Viewport.LayoutUpdated -= onLayout;
                main.Window.Render -= onRender;
            });
        }

        static (double P50, double P95, double Max) Stats(List<double> samples)
        {
            var sorted = samples.OrderBy(s => s).ToList();
            return (sorted[sorted.Count / 2], sorted[(int)(sorted.Count * 0.95)], sorted[^1]);
        }

        var s = Stats(send);
        var l = Stats(layout);
        var f = Stats(frame);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "pr-wp22-latency.txt"),
            $"{DateTime.UtcNow:O} {(gpu ? "gpu" : "warp")} n={frame.Count} " +
            $"handler(SendMessage) p50={s.P50:F2} p95={s.P95:F2} max={s.Max:F2} | key->layout p50={l.P50:F2} p95={l.P95:F2} max={l.Max:F2} | " +
            $"key->frame p50={f.P50:F2} p95={f.P95:F2} max={f.Max:F2}{Environment.NewLine}");
        Assert.True(s.P95 < 50, $"handler quá chậm: p50={s.P50:F2} ms, p95={s.P95:F2} ms");
        Assert.True(l.P95 < 100, $"key->layout quá chậm: p50={l.P50:F2} ms, p95={l.P95:F2} ms");
        // key->frame chỉ ghi lại: Present(1) trên desktop ẩn/bị che chờ theo bội 250 ms (xem fragment WP-22), không thuộc nhập liệu.
        StopAndAssertClean(run);
    }
}