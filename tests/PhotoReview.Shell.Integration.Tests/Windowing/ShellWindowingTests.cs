using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Input;
using PhotoReview.App.Windowing;
using PhotoReview.Shell.Integration.Tests.Startup;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Startup;
using PhotoReview.Shell.Win32.Windowing;

namespace PhotoReview.Shell.Integration.Tests.Windowing;

/// <summary>
/// WP-25: F11, đa màn hình, DPI, thanh tiêu đề tối, placement khi đóng trên shell THẬT (HWND, ShellStartup, WARP, dữ liệu tạm).
/// Test KHÔNG phụ thuộc cỡ/số màn hình của máy: vị trí và cỡ tham chiếu lấy từ monitor thật của cửa sổ lúc chạy; "màn hình thứ
/// hai" và "monitor bị rút" là seam giả (<see cref="IFullscreenNative"/>, <see cref="IMonitorLayout"/>). Phần chỉ kiểm được trên
/// máy hai màn hình thật (kéo giữa hai monitor khác DPI, rút cáp) ghi ở NOWPF-WP25-WINDOWING.md. Chạy tuần tự (một collection).
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed unsafe class ShellWindowingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const int GwlStyle = -16;

    // ---- giá trị giả ---------------------------------------------------------------------

    /// <summary>Bọc Win32 thật; test đặt "monitor của cửa sổ" và ghi lại các lần dời.</summary>
    private sealed class FakeFullscreenNative : IFullscreenNative
    {
        private readonly Win32FullscreenNative _real = Win32FullscreenNative.Instance;

        public ScreenRect? MonitorOverride { get; set; }

        public List<ScreenRect> Moves { get; } = [];

        public WindowShowState CurrentState(nint hwnd) => _real.CurrentState(hwnd);

        public bool TryGetMonitorRect(nint hwnd, out ScreenRect monitor)
        {
            if (MonitorOverride is { } fake)
            {
                monitor = fake;
                return true;
            }

            return _real.TryGetMonitorRect(hwnd, out monitor);
        }

        public bool TryGetWindowRect(nint hwnd, out ScreenRect window) => _real.TryGetWindowRect(hwnd, out window);

        public void MoveTo(nint hwnd, ScreenRect rect)
        {
            Moves.Add(rect);
            _real.MoveTo(hwnd, rect);
        }

        public void Show(nint hwnd, WindowShowState state) => _real.Show(hwnd, state);
    }

    private sealed class VanishedMonitorLayout(params ScreenRect[] remaining) : IMonitorLayout
    {
        public nint MonitorOf(nint hwnd) => 0x7777; // monitor lúc vào F11 không còn

        public bool TryGetWork(nint monitor, out ScreenRect work)
        {
            work = default;
            return false;
        }

        public IReadOnlyList<ScreenRect> WorkAreas() => remaining;
    }

    private sealed class RecordingDwm : IDwmWindowAttributes
    {
        public List<(uint Attribute, uint Value)> Calls { get; } = [];

        public bool Set(nint hwnd, uint attribute, uint value)
        {
            lock (Calls)
            {
                Calls.Add((attribute, value));
            }

            return true;
        }
    }

    // ---- hạ tầng ---------------------------------------------------------------------------

    private static bool WaitUntil(Func<bool> condition) => SpinWait.SpinUntil(condition, Bound);

    private static ShellRun StartRun(string label, Func<ShellStartupOptions, ShellStartupOptions>? configure = null, string? root = null)
    {
        root ??= ShellRun.NewRoot(label);
        var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))], configure: configure);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Error);
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ");
        return run;
    }

    private static void Stop(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE");
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private static ScreenRect WindowRect(nint hwnd)
    {
        Rect rect = default;
        Assert.True(User32.GetWindowRect(hwnd, &rect));
        return new ScreenRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static ScreenRect MonitorRect(nint hwnd)
    {
        Assert.True(Win32FullscreenNative.Instance.TryGetMonitorRect(hwnd, out var monitor));
        return monitor;
    }

    private static (int Left, int Top, int Right, int Bottom) NormalPosition(nint hwnd)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        Assert.True(User32.GetWindowPlacement(hwnd, &placement));
        return (placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom);
    }

    private static long Style(nint hwnd) => User32.GetWindowLongPtr(hwnd, GwlStyle);

    /// <summary>Đặt cửa sổ Normal ở vị trí/cỡ biết trước, nằm trọn trong monitor (không phụ thuộc cỡ màn hình runner).</summary>
    private static ScreenRect PlaceNormal(nint hwnd)
    {
        var monitor = MonitorRect(hwnd);
        var width = Math.Min(640, Math.Max(200, monitor.Width / 2));
        var height = Math.Min(480, Math.Max(150, monitor.Height / 2));
        User32.SetWindowPos(hwnd, 0, monitor.Left + 20, monitor.Top + 20, width, height,
            WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
        return WindowRect(hwnd);
    }

    private static string TitleOf(nint hwnd)
    {
        var text = new char[512];
        var length = GetWindowText(hwnd, text, text.Length);
        return new string(text, 0, length);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint hwnd);

    // ---- F11 ---------------------------------------------------------------------------------

    [Fact]
    public void F11_OnANormalWindow_CoversTheWholeMonitorWithoutChangingStateAndExitRestoresTheExactRect()
    {
        using var run = StartRun("f11normal");
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));
        var styleBefore = run.OnUi(() => Style(main.Hwnd));

        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        run.OnUi(() =>
        {
            Assert.Equal(MonitorRect(main.Hwnd), WindowRect(main.Hwnd)); // cả monitor, gồm thanh tác vụ (RV-A02)
            Assert.Equal(0, Style(main.Hwnd) & (WsCaption | WsThickFrame)); // không viền
            Assert.False(IsZoomed(main.Hwnd), "F11 không đổi trạng thái HĐH của cửa sổ Normal");
            Assert.True(main.Fullscreen.IsActive);
            Assert.Equal(WindowShowState.Normal, main.Fullscreen.RestoreState);
            Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), main.Fullscreen.RestoreBounds);
            var client = main.Window.ClientSizePixels;
            Assert.Equal((MonitorRect(main.Hwnd).Width, MonitorRect(main.Hwnd).Height), client); // client = cả monitor, không viền
        });

        Assert.True(run.OnUi(() => main.ApplyFullscreen(false)));
        run.OnUi(() =>
        {
            Assert.Equal(before, WindowRect(main.Hwnd));
            Assert.Equal(styleBefore, Style(main.Hwnd));
            Assert.False(main.Fullscreen.IsActive);
            Assert.Null(main.Fullscreen.RestoreState);
        });
        Stop(run);
    }

    [Fact]
    public void F11_OnAMaximizedWindow_StaysMaximizedAndExitReturnsToTheSameMaximizedRect()
    {
        using var run = StartRun("f11max");
        var main = run.Main!;
        var maximized = run.OnUi(() =>
        {
            User32.ShowWindow(main.Hwnd, WindowMessages.SwShowMaximized);
            Assert.True(IsZoomed(main.Hwnd));
            return WindowRect(main.Hwnd);
        });

        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));
        run.OnUi(() =>
        {
            Assert.Equal(MonitorRect(main.Hwnd), WindowRect(main.Hwnd));
            Assert.True(IsZoomed(main.Hwnd), "trạng thái Maximized giữ nguyên (không Normal -> Maximized: không chớp khung)");
            Assert.Equal(WindowShowState.Maximized, main.Fullscreen.RestoreState);
        });

        Assert.True(run.OnUi(() => main.ApplyFullscreen(false)));
        run.OnUi(() =>
        {
            Assert.True(IsZoomed(main.Hwnd));
            Assert.Equal(maximized, WindowRect(main.Hwnd));
            Assert.NotEqual(0, Style(main.Hwnd) & WsCaption);
        });
        Stop(run);
    }

    [Fact]
    public void F11_Twice_And_ExitWhenNotFullscreen_AreNoOps_AndKeepTheStateBeforeTheFirstF11()
    {
        using var run = StartRun("f11idem");
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));

        run.OnUi(() =>
        {
            Assert.False(main.ApplyFullscreen(false), "thoát khi không F11: không làm gì");
            Assert.True(main.ApplyFullscreen(true));
            Assert.False(main.ApplyFullscreen(true), "bật lần hai không được ghi đè rect/trạng thái trước F11");
            Assert.Equal((before.Left, before.Top, before.Right, before.Bottom), main.Fullscreen.RestoreBounds);
            Assert.True(main.ApplyFullscreen(false));
            Assert.Equal(before, WindowRect(main.Hwnd));
        });
        Stop(run);
    }

    [Fact]
    public void F11Key_ThroughRealMessages_AppliesToTheWindowAndEscapeRestoresIt()
    {
        using var run = StartRun("f11key");
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));

        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmKeyDown, KeyIdMapping.ToVirtualKey(KeyId.F11), 1);
        Assert.True(WaitUntil(() => run.OnUi(() => WindowRect(main.Hwnd) == MonitorRect(main.Hwnd))), "F11 không phủ monitor");
        Assert.True(run.OnUi(() => main.ViewModel.Viewer.IsFullscreen));

        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmKeyDown, KeyIdMapping.ToVirtualKey(KeyId.Escape), 1);
        Assert.True(WaitUntil(() => run.OnUi(() => WindowRect(main.Hwnd) == before)), "Esc không trả cửa sổ về rect cũ");
        Assert.False(run.HasExited);
        Stop(run);
    }

    [Fact]
    public void F11Transition_IsOneResizeStep_NoIntermediateClientSize()
    {
        // Thẻ WP-25 (FullscreenTransitionFrameTests): vào/ra F11 là MỘT SetWindowPos -> client đổi đúng một lần, thẳng tới cỡ cuối
        // (không qua kích thước trung gian 1200x800 như chuỗi Maximized -> Normal -> Maximized cũ).
        using var run = StartRun("f11steps");
        var main = run.Main!;
        run.OnUi(() => PlaceNormal(main.Hwnd));
        var sizes = new List<(int, int)>();
        EventHandler onSize = (_, _) => sizes.Add(main.Window.ClientSizePixels);
        run.OnUi(() => main.Window.ClientSizeChanged += onSize);

        var monitor = run.OnUi(() =>
        {
            main.ApplyFullscreen(true);
            return MonitorRect(main.Hwnd);
        });

        Assert.Equal([(monitor.Width, monitor.Height)], sizes);
        sizes.Clear();
        var normalClient = run.OnUi(() =>
        {
            main.ApplyFullscreen(false);
            return main.Window.ClientSizePixels;
        });
        Assert.Equal([normalClient], sizes);
        run.OnUi(() => main.Window.ClientSizeChanged -= onSize);
        Stop(run);
    }

    [Fact]
    public void CloseDeferredByAFileAction_WhileFullscreen_KeepsFullscreenAndSavesTheStateBeforeF11OnceItCloses()
    {
        var root = ShellRun.NewRoot("f11defer");
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigFile)!);
        File.WriteAllText(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(
            new PhotoReview.Core.Settings.AppSettings { ConfirmBeforeDelete = true },
            PhotoReview.Core.Settings.AppSettingsJsonContext.Default.AppSettings));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var run = StartRun("f11defer", o => o with
        {
            ServiceOverrides = services =>
            {
                o.ServiceOverrides?.Invoke(services);
                services.AddSingleton<PhotoReview.Core.Abstractions.IDialogService>(new BlockingConfirmDialog(entered, release));
            },
        }, root);
        var main = run.Main!;
        var normal = run.OnUi(() =>
        {
            PlaceNormal(main.Hwnd);
            return NormalPosition(main.Hwnd);
        });
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));
        main.Dispatcher.Post(() => _ = main.ViewModel.RecycleAsync());
        Assert.True(entered.Wait(Bound), "hộp xác nhận không hiện");

        run.RequestClose();
        SpinWait.SpinUntil(() => false, 400);

        Assert.False(run.HasExited);
        Assert.False(main.IsClosingOrClosed);
        Assert.Equal(MonitorRect(main.Hwnd), WindowRect(main.Hwnd)); // vẫn fullscreen trong lúc hoãn
        release.Set();
        Assert.True(run.WaitForExit(Bound), "cửa sổ không tự đóng sau khi file action xong");
        var saved = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(saved);
        Assert.Equal(1, saved.ShowCommand);
        Assert.Equal(normal, (saved.NormalLeft, saved.NormalTop, saved.NormalRight, saved.NormalBottom));
    }

    /// <summary>Hộp xác nhận chạy một vòng message lồng (như hộp thoại modal) tới khi được thả; trả "không đồng ý".</summary>
    private sealed class BlockingConfirmDialog(ManualResetEventSlim entered, ManualResetEventSlim release) : PhotoReview.Core.Abstractions.IDialogService
    {
        public bool ShowConfirmation(string title, string message)
        {
            entered.Set();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Msg message2;
            while (watch.Elapsed < Bound && !release.IsSet)
            {
                while (User32.PeekMessage(&message2, 0, 0, 0, WindowMessages.PmRemove))
                {
                    User32.TranslateMessage(&message2);
                    User32.DispatchMessage(&message2);
                }

                release.Wait(5);
            }

            return false;
        }

        public void ShowMessage(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

        public string? PickFolder(string? initialFolder = null) => null;

        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;

        public void ShowRecovery()
        {
        }

        public void ShowDiagnostics()
        {
        }

        public bool ShowSettings() => false;

        public void ShowBenchmark(string? folder = null)
        {
        }

        public void ShowSkippedFiles(IReadOnlyList<PhotoReview.Core.Abstractions.SkippedEntry> entries)
        {
        }
    }

    [Fact]
    [Trait("Category", "Manual")]
    public void Manual_F11Latency_IsReportedOnTheUiThread()
    {
        // Số đo (không thuộc CI): thời gian UI thread trong ApplyFullscreen vào/ra, 30 vòng, và tổng khung vẽ giữa chúng.
        using var run = StartRun("f11latency");
        var main = run.Main!;
        run.OnUi(() => PlaceNormal(main.Hwnd));
        var enter = new List<double>();
        var exit = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            enter.Add(run.OnUi(() =>
            {
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                main.ApplyFullscreen(true);
                main.RenderNow();
                return System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            }));
            exit.Add(run.OnUi(() =>
            {
                var t = System.Diagnostics.Stopwatch.GetTimestamp();
                main.ApplyFullscreen(false);
                main.RenderNow();
                return System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;
            }));
        }

        enter.Sort();
        exit.Sort();
        var path = Path.Combine(Path.GetTempPath(), "wp25-f11-latency.txt");
        File.WriteAllText(path, $"enter p50={enter[15]:F2} p95={enter[28]:F2} max={enter[^1]:F2} ms; exit p50={exit[15]:F2} p95={exit[28]:F2} max={exit[^1]:F2} ms");
        Stop(run);
    }

    // ---- đóng khi đang F11 (R7-10) -------------------------------------------------------------

    [Fact]
    public void CloseWhileFullscreen_SavesTheStateAndNormalRectFromBeforeF11()
    {
        var root = ShellRun.NewRoot("f11close");
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        using var run = StartRun("f11close", root: root);
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));
        var normal = run.OnUi(() => NormalPosition(main.Hwnd));
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        Stop(run);

        var saved = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(saved);
        Assert.Equal(1, saved.ShowCommand); // không phải Maximized không viền của F11
        Assert.Equal(normal, (saved.NormalLeft, saved.NormalTop, saved.NormalRight, saved.NormalBottom));
        Assert.Equal(before.Width, saved.NormalRight - saved.NormalLeft);
    }

    [Fact]
    public void CloseWhileFullscreenFromMaximized_SavesMaximized()
    {
        var root = ShellRun.NewRoot("f11closemax");
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        using var run = StartRun("f11closemax", root: root);
        var main = run.Main!;
        run.OnUi(() => User32.ShowWindow(main.Hwnd, WindowMessages.SwShowMaximized));
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        Stop(run);

        var saved = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(saved);
        Assert.Equal(3, saved.ShowCommand);
    }

    [Fact]
    public void ShellAndWpfShareThePlacementFileFormat_ShellWritesWhatTheSharedStoreReadsBack()
    {
        var root = ShellRun.NewRoot("roundtrip");
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        using var run = StartRun("roundtrip", root: root);
        var main = run.Main!;
        run.OnUi(() => PlaceNormal(main.Hwnd));
        Stop(run);

        // Bản WPF dùng cùng JsonWindowPlacementStore: tệp shell ghi phải qua store đó lần nữa mà byte không đổi.
        var store = new JsonWindowPlacementStore();
        var data = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(data);
        var other = Path.Combine(root, "other-placement.json");
        store.Save(other, data);
        Assert.Equal(File.ReadAllText(paths.WindowPlacementFile), File.ReadAllText(other));
        var text = File.ReadAllText(paths.WindowPlacementFile);
        foreach (var key in new[] { "\"Length\"", "\"Flags\"", "\"ShowCommand\"", "\"MinPosition\"", "\"MaxPosition\"", "\"NormalPosition\"" })
        {
            Assert.Contains(key, text); // lược đồ y hệt window-placement.json của bản WPF (N-3)
        }
    }

    // ---- đa màn hình ------------------------------------------------------------------------------

    [Fact]
    public void MonitorVanishesWhileFullscreen_TheWindowIsRefittedOntoTheRemainingMonitor()
    {
        var native = new FakeFullscreenNative();
        using var run = StartRun("monitorgone", o => o with { Windowing = new ShellWindowingSeams(Native: native) });
        var main = run.Main!;
        run.OnUi(() => PlaceNormal(main.Hwnd));
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));
        var realMonitor = run.OnUi(() => MonitorRect(main.Hwnd));

        // Màn hình thứ hai (rộng hơn) từng chứa cửa sổ; rút nó: Windows dời cửa sổ, monitor gần nhất giờ là màn hình còn lại.
        var remaining = new ScreenRect(realMonitor.Left, realMonitor.Top, realMonitor.Right - 100, realMonitor.Bottom - 60);
        native.MonitorOverride = remaining;
        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDisplayChange, 32, 0);

        Assert.True(WaitUntil(() => run.OnUi(() => WindowRect(main.Hwnd) == remaining)), "cửa sổ không được ép lại lên monitor còn lại");
        Assert.Contains(remaining, native.Moves);
        Stop(run);
    }

    [Fact]
    public void DisplayChangeWhileNotFullscreen_DoesNotMoveTheWindow()
    {
        var native = new FakeFullscreenNative();
        using var run = StartRun("displaynofs", o => o with { Windowing = new ShellWindowingSeams(Native: native) });
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));
        native.MonitorOverride = new ScreenRect(0, 0, 400, 300);

        PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDisplayChange, 32, 0);
        run.OnUi(() => { }); // một lượt dispatcher: việc đăng bởi handler (nếu có) đã chạy

        Assert.Empty(native.Moves);
        Assert.Equal(before, run.OnUi(() => WindowRect(main.Hwnd)));
        Stop(run);
    }

    [Fact]
    public void ExitFullscreen_AfterTheOriginalMonitorWasUnplugged_PutsTheNormalWindowOntoARemainingWorkArea()
    {
        var realWork = Win32MonitorLayout.Instance.WorkAreas();
        Assert.NotEmpty(realWork);
        var remaining = realWork[0];
        var layout = new VanishedMonitorLayout(remaining);
        using var run = StartRun("exitgone", o => o with { Windowing = new ShellWindowingSeams(MonitorLayout: layout) });
        var main = run.Main!;
        // Cửa sổ nằm hẳn ngoài mọi work area còn lại (vốn ở monitor thứ hai đã rút), cỡ biết trước.
        var offscreenLeft = remaining.Right + 5000;
        run.OnUi(() => User32.SetWindowPos(main.Hwnd, 0, offscreenLeft, remaining.Top + 10, 480, 320,
            WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate));
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        Assert.True(run.OnUi(() => main.ApplyFullscreen(false)));

        var rect = run.OnUi(() => WindowRect(main.Hwnd));
        Assert.True(rect.Left >= remaining.Left && rect.Right <= remaining.Right && rect.Top >= remaining.Top && rect.Bottom <= remaining.Bottom,
            $"cửa sổ {rect} phải nằm trong work area còn lại {remaining}");
        Assert.Equal(Math.Min(480, remaining.Width), rect.Width); // giữ cỡ (chỉ thu khi lớn hơn work area)
        Stop(run);
    }

    [Fact]
    public void DpiChangedWhileFullscreen_TheWindowCoversTheMonitorAgainAndTheViewerScaleFollows()
    {
        using var run = StartRun("dpifs");
        var main = run.Main!;
        run.OnUi(() => PlaceNormal(main.Hwnd));
        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        var (before, after, rect, monitor) = run.OnUi(() =>
        {
            var current = main.ViewModel.Viewer.DpiScale;
            var newDpi = (nint)Math.Round(current * 96 * 1.5);
            // Rect gợi ý của hệ điều hành là rect cửa sổ THƯỜNG ở monitor mới: ở F11 không được để cửa sổ ở đó.
            var suggested = new Rect { Left = 40, Top = 40, Right = 40 + 500, Bottom = 40 + 400 };
            PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDpiChanged, newDpi | (newDpi << 16), (nint)(&suggested));
            return (current, main.ViewModel.Viewer.DpiScale, WindowRect(main.Hwnd), MonitorRect(main.Hwnd));
        });

        Assert.Equal(monitor, rect);
        Assert.Equal(before * 1.5, after, 3);
        Stop(run);
    }

    // ---- DPI --------------------------------------------------------------------------------------

    [Fact]
    public void DpiChange_WithTheSamePixelSize_StillResizesTheRenderSurfaceScaleOnTheNextFrame()
    {
        using var run = StartRun("dpisurface");
        var main = run.Main!;

        var (surfaceBefore, surfaceAfter, dpi) = run.OnUi(() =>
        {
            Rect outer = default;
            Assert.True(User32.GetWindowRect(main.Hwnd, &outer));
            var newDpi = (nint)Math.Round(main.Window.DpiScale * 96 * 2);
            // Cùng số pixel (rect gợi ý = rect hiện tại) nhưng DPI khác: bề mặt phải nhận tỉ lệ mới ở khung kế (không chỉ khi cỡ đổi).
            PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDpiChanged, newDpi | (newDpi << 16), (nint)(&outer));
            var before = main.Surface!.DpiScale;
            // RenderNow đơn lẻ có thể bị FramePacer hoãn trước khi bề mặt nhận tỉ lệ mới: vẽ lại tới khi khung thật được vẽ.
            ShellFrames.RenderUntil(main, () => main.Surface!.DpiScale == main.Window.DpiScale);
            return (before, main.Surface!.DpiScale, main.Window.DpiScale);
        });

        Assert.NotEqual(dpi, surfaceBefore);
        Assert.Equal(dpi, surfaceAfter);
        Stop(run);
    }

    [Fact]
    public void DpiChange_UpdatesViewerScaleAndDecodeBoxInTheSameTurn()
    {
        using var run = StartRun("dpibox");
        var main = run.Main!;

        var (scaleBefore, scaleAfter) = run.OnUi(() =>
        {
            Rect outer = default;
            Assert.True(User32.GetWindowRect(main.Hwnd, &outer));
            var before = main.ViewModel.Viewer.DpiScale;
            var newDpi = (nint)Math.Round(before * 96 * 1.5);
            var width = outer.Right - outer.Left;
            var height = outer.Bottom - outer.Top;
            var suggested = new Rect { Left = outer.Left, Top = outer.Top, Right = outer.Left + (int)(width * 1.5), Bottom = outer.Top + (int)(height * 1.5) };
            PendingUser32.SendMessage(main.Hwnd, WindowMessages.WmDpiChanged, newDpi | (newDpi << 16), (nint)(&suggested));
            return (before, main.ViewModel.Viewer.DpiScale);
        });

        Assert.Equal(scaleBefore * 1.5, scaleAfter, 3);
        Stop(run);
    }

    // ---- thanh tiêu đề --------------------------------------------------------------------------

    [Fact]
    public void TitleBar_IsMadeDarkWithTheDarkWindowCaptionColourBeforeTheWindowIsRevealed()
    {
        var dwm = new RecordingDwm();
        using var run = StartRun("darktitle", o => o with { Windowing = new ShellWindowingSeams(Dwm: dwm) });

        List<(uint Attribute, uint Value)> calls;
        lock (dwm.Calls)
        {
            calls = [.. dwm.Calls];
        }

        Assert.Contains((20u, 1u), calls); // DWMWA_USE_IMMERSIVE_DARK_MODE
        Assert.Contains((35u, 0x00171717u), calls); // DWMWA_CAPTION_COLOR = Dark.Window #171717
        Stop(run);
    }

    [Fact]
    public void Title_FollowsTheViewModelAndCarriesTheInstanceLabel()
    {
        using var run = StartRun("title");
        var main = run.Main!;

        run.OnUi(() => main.ViewModel.InstanceLabel = "AGENT CHECK");

        var title = run.OnUi(() => TitleOf(main.Hwnd));
        Assert.Equal(run.OnUi(() => main.ViewModel.FolderTitle), title);
        Assert.StartsWith("[AGENT CHECK] ", title);

        run.OnUi(() => main.ViewModel.InstanceLabel = null);
        Assert.False(run.OnUi(() => TitleOf(main.Hwnd)).StartsWith('['));
        Stop(run);
    }

    // ---- thu nhỏ / khôi phục / forward ------------------------------------------------------------

    [Fact]
    public void MinimizeThenRestore_KeepsTheViewerStateAndTheWindowRect()
    {
        using var run = StartRun("minimize");
        var main = run.Main!;
        var before = run.OnUi(() => PlaceNormal(main.Hwnd));
        var (maxWidth, maxHeight, fit, dpi) = run.OnUi(() =>
            (main.ViewModel.Viewer.MaxImageWidth, main.ViewModel.Viewer.MaxImageHeight, main.ViewModel.Viewer.IsFit, main.ViewModel.Viewer.DpiScale));

        run.OnUi(() =>
        {
            User32.ShowWindow(main.Hwnd, WindowMessages.SwMinimize);
            Assert.True(User32.IsIconic(main.Hwnd));
            main.RenderNow(); // thu nhỏ: không vẽ, không ném
            User32.ShowWindow(main.Hwnd, WindowMessages.SwRestore);
            Assert.False(User32.IsIconic(main.Hwnd));
        });

        run.OnUi(() =>
        {
            Assert.Equal(before, WindowRect(main.Hwnd));
            var viewer = main.ViewModel.Viewer;
            Assert.Equal((maxWidth, maxHeight, fit, dpi), (viewer.MaxImageWidth, viewer.MaxImageHeight, viewer.IsFit, viewer.DpiScale));
        });
        Assert.False(run.HasExited);
        Stop(run);
    }

    [Fact]
    public void F11WhileMinimized_RestoresTheWindowFirstAndThenCoversTheMonitor()
    {
        using var run = StartRun("f11min");
        var main = run.Main!;
        run.OnUi(() =>
        {
            PlaceNormal(main.Hwnd);
            User32.ShowWindow(main.Hwnd, WindowMessages.SwMinimize);
            Assert.True(User32.IsIconic(main.Hwnd));
        });

        Assert.True(run.OnUi(() => main.ApplyFullscreen(true)));

        run.OnUi(() =>
        {
            Assert.False(User32.IsIconic(main.Hwnd));
            Assert.Equal(MonitorRect(main.Hwnd), WindowRect(main.Hwnd));
            Assert.Equal(WindowShowState.Normal, main.Fullscreen.RestoreState); // thu nhỏ tính là Normal
        });
        Stop(run);
    }
}
