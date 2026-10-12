using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Windowing;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-20 (thẻ: ShellStartupOrderTests + ShellStartupFirstShowTests + forward + INV-10): trình tự khởi động THẬT của shell chạy trong
/// tiến trình test (HWND thật, WARP, thư mục dữ liệu tạm, prefix instance duy nhất). Chạy tuần tự (một collection) vì mỗi lần
/// là một vòng thông điệp + cửa sổ top-level và dùng chung <c>AppLog</c> tĩnh.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static void Order(IReadOnlyList<string> marks, params string[] expected)
    {
        var last = -1;
        foreach (var name in expected)
        {
            var index = marks.ToList().IndexOf(name);
            Assert.True(index >= 0, $"mốc '{name}' không được phát; có: {string.Join(", ", marks)}");
            Assert.True(index > last, $"mốc '{name}' (vị trí {index}) phải đứng SAU mốc trước nó (vị trí {last}); thứ tự: {string.Join(", ", marks)}");
            last = index;
        }
    }

    private static string Diag(ShellRun run) => run.Diagnostics;

    private static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        return SpinWait.SpinUntil(condition, timeout ?? Bound);
    }

    private static void StopAndAssertClean(ShellRun run)
    {
        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE");
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Startup_WithJpeg_OpensWindowThenDrawsFirstImageThenClosesCleanly()
    {
        var root = ShellRun.NewRoot("first");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);

        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong");
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ: " + Diag(run));

        var main = run.Main!;
        var (width, height) = run.OnUi(() => (main.ViewModel.Viewer.SourcePixelWidth, main.ViewModel.Viewer.SourcePixelHeight));
        Assert.Equal((TestJpeg.Width, TestJpeg.Height), (width, height));
        Order(run.MarkNames, "appStartup", "servicesBuilt", "settingsLoaded", "instanceLock", "windowShown",
            "mainWindowConstructed", "firstPresent", "windowRevealed");
        Assert.Contains("d3dDeviceReady", run.MarkNames);
        Assert.Contains("swapChainReady", run.MarkNames);
        StopAndAssertClean(run);
    }

    [Fact]
    public void Startup_EarlyDecode_StartsOnlyAfterTheInstanceLockIsHeld()
    {
        var root = ShellRun.NewRoot("lockorder");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        Assert.True(WaitUntil(() => run.MarkNames.Contains("earlyDecodeStarted")), "decode sớm không bắt đầu");
        Order(run.MarkNames, "instanceLock", "earlyDecodeStarted");
        StopAndAssertClean(run);
    }

    [Fact]
    public void Startup_WindowIsCloakedFromItsHandleUntilTheFirstPresent()
    {
        var root = ShellRun.NewRoot("cloak");
        var events = new List<string>();
        var gate = new object();
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))], configure: options =>
        {
            return options with
            {
                Mark = name =>
                {
                    lock (gate)
                    {
                        events.Add("mark:" + name);
                    }
                },
                SetCloaked = (hwnd, cloaked) =>
                {
                    lock (gate)
                    {
                        events.Add(cloaked ? "cloak" : "uncloak");
                    }

                    return true; // DWM "chấp nhận": ép đường cloak thật dù desktop ẩn có từ chối hay không
                },
            };
        });

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        List<string> snapshot;
        lock (gate)
        {
            snapshot = [.. events];
        }

        var cloak = snapshot.IndexOf("cloak");
        var shown = snapshot.IndexOf("mark:windowShown");
        var present = snapshot.IndexOf("mark:firstPresent");
        var uncloak = snapshot.IndexOf("uncloak");
        Assert.True(cloak >= 0 && shown >= 0 && present >= 0 && uncloak >= 0, string.Join(", ", snapshot));
        Assert.True(cloak < shown, "phải cloak TRƯỚC khi hiện cửa sổ (không khung trắng): " + string.Join(", ", snapshot));
        Assert.True(uncloak > present, "chỉ bỏ cloak SAU Present đầu: " + string.Join(", ", snapshot));
        Assert.Equal(1, snapshot.Count(e => e == "cloak"));
        Assert.Equal(1, snapshot.Count(e => e == "uncloak"));
        StopAndAssertClean(run);
    }

    [Fact]
    public void Startup_CloakIsRequestedWhileTheWindowIsNotYetVisible()
    {
        var root = ShellRun.NewRoot("cloakhidden");
        var visibleAtCloak = new List<bool>();
        var visibleAtUncloak = new List<bool>();
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))], configure: options => options with
        {
            SetCloaked = (hwnd, cloaked) =>
            {
                (cloaked ? visibleAtCloak : visibleAtUncloak).Add(IsWindowVisible(hwnd));
                return true;
            },
        });

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        // Cờ cloak phải có hiệu lực TRƯỚC ShowWindow thật sự (không phải trước mốc "windowShown"): nếu không, DWM đã có thể
        // tổng hợp một khung trắng của cửa sổ rồi mới bị che.
        Assert.Equal([false], visibleAtCloak);
        Assert.Equal([true], visibleAtUncloak);
        StopAndAssertClean(run);
    }

    [Fact]
    public unsafe void Startup_SavedPlacement_IsAppliedBeforeTheWindowIsShown()
    {
        var root = ShellRun.NewRoot("placement");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowPlacementFile)!);
        var saved = new WindowPlacementData(44, 0, 1, -1, -1, -1, -1, 120, 90, 920, 590);
        new JsonWindowPlacementStore().Save(paths.WindowPlacementFile, saved);

        using var run = ShellRun.Start(root, [jpeg]);

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        Assert.True(User32.GetWindowPlacement(run.Main!.Hwnd, &placement));
        Assert.Equal((120, 90, 920, 590),
            (placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom));
        Order(run.MarkNames, "placementRestoredBeforeShow", "windowShown");
        StopAndAssertClean(run);

        // Đóng lưu lại placement (đúng khung đã khôi phục).
        var after = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(after);
        Assert.Equal((120, 90, 920, 590), (after.NormalLeft, after.NormalTop, after.NormalRight, after.NormalBottom));
        Assert.Equal(1, after.ShowCommand);
    }

    [Fact]
    public unsafe void Closing_SavesTheCurrentPlacementNotTheOneItOpenedWith()
    {
        var root = ShellRun.NewRoot("saveonclose");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        using var run = ShellRun.Start(root, [jpeg]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        Assert.False(File.Exists(paths.WindowPlacementFile), "chưa có placement trước khi đóng");

        var main = run.Main!;
        var placement = default(WindowPlacement);
        run.OnUi(() =>
        {
            User32.SetWindowPos(main.Hwnd, 0, 37, 53, 700, 500, WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
            var current = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
            Assert.True(User32.GetWindowPlacement(main.Hwnd, &current));
            placement = current;
        });

        StopAndAssertClean(run);
        var saved = JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile);
        Assert.NotNull(saved);
        Assert.Equal((placement.NormalPosition.Left, placement.NormalPosition.Top, placement.NormalPosition.Right, placement.NormalPosition.Bottom),
            (saved.NormalLeft, saved.NormalTop, saved.NormalRight, saved.NormalBottom));
        Assert.Equal(700, saved.NormalRight - saved.NormalLeft);
        Assert.Equal(1, saved.ShowCommand);
    }

    [Fact]
    public void Startup_SavedPlacement_IsRestoredWhileTheWindowIsStillHidden()
    {
        var root = ShellRun.NewRoot("placementhidden");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowPlacementFile)!);
        new JsonWindowPlacementStore().Save(paths.WindowPlacementFile, new WindowPlacementData(44, 0, 1, -1, -1, -1, -1, 120, 90, 920, 590));
        nint hwnd = 0;
        bool? visibleAtPlacement = null;
        bool? visibleAtShown = null;

        // Mốc phát trên UI thread: "placementRestoredBeforeShow" ngay sau khi áp placement và TRƯỚC ShowWindow.
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            SetCloaked = (handle, _) =>
            {
                hwnd = handle;
                return true;
            },
            Mark = name =>
            {
                if (name == "placementRestoredBeforeShow")
                {
                    visibleAtPlacement = IsWindowVisible(hwnd);
                }
                else if (name == "windowShown")
                {
                    visibleAtShown = IsWindowVisible(hwnd);
                }
            },
        });

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        Assert.NotEqual(0, hwnd);
        Assert.False(visibleAtPlacement, "placement phải được áp khi cửa sổ chưa hiện (không nhảy từ 1200x800)");
        Assert.True(visibleAtShown, "cửa sổ phải hiện sau khi áp placement");
        StopAndAssertClean(run);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [Fact]
    public unsafe void Startup_SavedMaximizedPlacement_ReopensMaximized()
    {
        var root = ShellRun.NewRoot("maximized");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowPlacementFile)!);
        new JsonWindowPlacementStore().Save(paths.WindowPlacementFile, new WindowPlacementData(44, 0, 3, -1, -1, -1, -1, 120, 90, 920, 590));

        using var run = ShellRun.Start(root, [jpeg]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        Assert.True(User32.GetWindowPlacement(run.Main!.Hwnd, &placement));
        Assert.Equal(WindowMessages.SwShowMaximized, (int)placement.ShowCmd);
        StopAndAssertClean(run);
        Assert.Equal(3, JsonWindowPlacementStore.ReadRaw(paths.WindowPlacementFile)!.ShowCommand);
    }

    [Fact]
    public void Startup_JournalReconcile_RunsAfterTheLockAndShowAndBeforeAnyQueuedInput()
    {
        var root = ShellRun.NewRoot("journal");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var order = new List<string>();
        var gate = new object();
        ShellRun? runRef = null;
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            JournalRecovery = _ =>
            {
                lock (gate)
                {
                    order.Add("reconcile:" + string.Join(",", runRef!.MarkNames));
                }

                return Task.FromResult<IReadOnlyList<JournalEntry>>([]);
            },
            OnMainWindowReady = main =>
            {
                // "Input đầu tiên" thay thế: việc Normal xếp lên UI thread NGAY khi cửa sổ sẵn sàng, tức trước khi vòng thông điệp
                // nhận bất cứ gì. Reconcile phải đã bắt đầu trước nó.
                main.Dispatcher.Post(() =>
                {
                    lock (gate)
                    {
                        order.Add("input");
                    }
                });
            },
        });
        runRef = run;

        Assert.True(WaitUntil(() =>
        {
            lock (gate)
            {
                return order.Count >= 2;
            }
        }));
        lock (gate)
        {
            Assert.StartsWith("reconcile:", order[0]);
            Assert.Equal("input", order[1]);
            var marksAtReconcile = order[0]["reconcile:".Length..].Split(',');
            Assert.Contains("instanceLock", marksAtReconcile);
            Assert.Contains("windowShown", marksAtReconcile);
            Assert.True(Array.IndexOf(marksAtReconcile, "instanceLock") < Array.IndexOf(marksAtReconcile, "windowShown"));
        }

        StopAndAssertClean(run);
    }

    [Fact]
    public void Startup_FailedJournalEntries_AskOnceToOpenRecovery()
    {
        var root = ShellRun.NewRoot("recovery");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var dialogs = new RecordingDialogService { Confirm = true };
        var failed = new JournalEntry("op-1", FileOperationType.Move, JournalState.Failed, @"C:\a.jpg", @"C:\b.jpg", 10,
            DateTime.UtcNow, DateTime.UtcNow, Error: "boom");
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IDialogService>(dialogs);
            },
            JournalRecovery = _ => Task.FromResult<IReadOnlyList<JournalEntry>>([failed]),
        });

        Assert.True(WaitUntil(() => dialogs.Confirmations == 1 && dialogs.RecoveryShown == 1),
            $"confirmations={dialogs.Confirmations} recovery={dialogs.RecoveryShown}");
        StopAndAssertClean(run);
        Assert.Equal(1, dialogs.Confirmations);
    }

    [Fact]
    public void SecondInstance_ForwardsItsPathToTheRunningOneAndExitsZero()
    {
        var rootA = ShellRun.NewRoot("fwdA");
        var rootB = ShellRun.NewRoot("fwdB");
        var first = TestJpeg.WriteTo(Path.Combine(rootA, "photos", "a.jpg"));
        var second = TestJpeg.WriteTo(Path.Combine(rootA, "photos", "b.jpg"));
        using var a = ShellRun.Start(rootA, [first]);
        Assert.True(a.WaitForMain());
        Assert.True(WaitUntil(() => a.Main!.FirstImagePresented));

        using var b = ShellRun.Start(rootB, [second], prefix: a.Prefix);
        Assert.True(b.WaitForExit(Bound), "bản thứ hai không thoát");
        Assert.Null(b.Error);
        Assert.Equal(0, b.ExitCode);
        Assert.Null(b.Main); // không bao giờ dựng cửa sổ
        Assert.DoesNotContain("windowShown", b.MarkNames);
        Assert.DoesNotContain("earlyDecodeStarted", b.MarkNames);

        var main = a.Main!;
        Assert.True(WaitUntil(() => a.OnUi(() => string.Equals(
                Path.GetFileName(main.ViewModel.Presenter.CurrentPresentedPath), "b.jpg", StringComparison.OrdinalIgnoreCase))),
            "bản đầu không mở đường dẫn được forward");
        StopAndAssertClean(a);
    }

    [Fact]
    public unsafe void Startup_WithoutSavedPlacement_OpensAtTheWpfOuterSize()
    {
        var root = ShellRun.NewRoot("outersize");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstPresentDone));

        var main = run.Main!;
        Rect outer = default;
        Assert.True(User32.GetWindowRect(main.Hwnd, &outer));
        var dpi = run.OnUi(() => main.Window.DpiScale);
        // Width/Height 1200x800 của MainWindow.xaml là cỡ NGOÀI (gồm khung) trong WPF: bản Win32 giữ đúng cỡ đó.
        // Trên máy có màn hình nhỏ hơn cỡ mặc định (runner CI 1024x768) cửa sổ bị kẹp vào khung màn hình: chỉ khi màn hình
        // đủ lớn mới khẳng định đúng cỡ mặc định; còn lại cửa sổ không được vượt khung màn hình quá viền vô hình.
        var monitor = User32.MonitorFromWindow(main.Hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        Assert.True(User32.GetMonitorInfo(monitor, &info));
        int wantW = (int)Math.Round(ShellMainWindow.DefaultWidthDip * dpi);
        int wantH = (int)Math.Round(ShellMainWindow.DefaultHeightDip * dpi);
        int workW = info.Monitor.Right - info.Monitor.Left, workH = info.Monitor.Bottom - info.Monitor.Top;
        int gotW = outer.Right - outer.Left, gotH = outer.Bottom - outer.Top;
        // GetWindowRect gồm cả viền đổi cỡ vô hình (~8 px mỗi bên): dung sai một viền ở mỗi cạnh khi bị kẹp.
        const int InvisibleFrameSlack = 24;
        if (workW >= wantW) Assert.Equal(wantW, gotW); else Assert.InRange(gotW, 1, workW + InvisibleFrameSlack);
        if (workH >= wantH) Assert.Equal(wantH, gotH); else Assert.InRange(gotH, 1, workH + InvisibleFrameSlack);
        StopAndAssertClean(run);
    }

    [Fact]
    public unsafe void DpiChange_WithTheSameDipSize_StillUpdatesTheViewerScaleAndDecodeBoxInTheSameTurn()
    {
        var root = ShellRun.NewRoot("dpi");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        var main = run.Main!;

        var (before, after, sizeEvents) = run.OnUi(() =>
        {
            var current = main.ViewModel.Viewer.DpiScale;
            var newDpi = (nint)Math.Round(current * 96 * 2);
            // Chuyển sang màn hình DPI gấp đôi: rect gợi ý gấp đôi số pixel nên cỡ DIP của client KHÔNG đổi (không có
            // ClientSizeChanged kéo UpdateFitSize theo), chỉ DpiChanged. Hộp decode và tỉ lệ của ViewerState phải theo DPI mới
            // ngay khi handler trả về (không chờ việc layout đăng sau).
            Rect outer = default;
            Rect client = default;
            Assert.True(User32.GetWindowRect(main.Hwnd, &outer));
            Assert.True(User32.GetClientRect(main.Hwnd, &client));
            // Cỡ nhỏ đã biết (400x300 client) để bản gấp đôi luôn nằm trong màn hình (khung không bị MaxTrackSize cắt).
            User32.SetWindowPos(main.Hwnd, 0, 0, 0, outer.Right - outer.Left - (client.Right - client.Left) + 400,
                outer.Bottom - outer.Top - (client.Bottom - client.Top) + 300,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
            Assert.True(User32.GetWindowRect(main.Hwnd, &outer));
            Assert.True(User32.GetClientRect(main.Hwnd, &client));
            var (clientWidth, clientHeight) = (client.Right - client.Left, client.Bottom - client.Top);
            var (frameWidth, frameHeight) = (outer.Right - outer.Left - clientWidth, outer.Bottom - outer.Top - clientHeight);
            var suggested = new Rect
            {
                Left = outer.Left,
                Top = outer.Top,
                Right = outer.Left + frameWidth + (clientWidth * 2),
                Bottom = outer.Top + frameHeight + (clientHeight * 2),
            };
            var sizeEvents = 0;
            EventHandler onSize = (_, _) => sizeEvents++;
            main.Window.ClientSizeChanged += onSize;
            PhotoReview.Shell.Win32.Hosting.PendingInterop.PendingUser32.SendMessage(
                main.Hwnd, WindowMessages.WmDpiChanged, newDpi | (newDpi << 16), (nint)(&suggested));
            main.Window.ClientSizeChanged -= onSize;
            return (current, main.ViewModel.Viewer.DpiScale, sizeEvents);
        });

        Assert.Equal(0, sizeEvents); // kịch bản đúng: chỉ DpiChanged, cỡ DIP giữ nguyên
        Assert.Equal(before * 2, after, 3);
        StopAndAssertClean(run);
    }

    /// <summary>Bơm message lồng (như một vòng modal của hệ thống) trên thread hiện tại tới khi <paramref name="done"/> hoặc hết hạn.</summary>
    private static unsafe void PumpNested(Func<bool> done, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        Msg message;
        while (watch.Elapsed < limit && !done())
        {
            while (User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmRemove))
            {
                User32.TranslateMessage(&message);
                User32.DispatchMessage(&message);
            }

            SpinWait.SpinUntil(() => false, 5);
        }
    }

    [Fact]
    public void SecondInstance_ForwardingWhileTheFirstIsStillStarting_IsAppliedOnceTheWindowIsReady()
    {
        var rootA = ShellRun.NewRoot("fwdEarlyA");
        var rootB = ShellRun.NewRoot("fwdEarlyB");
        var first = TestJpeg.WriteTo(Path.Combine(rootA, "photos", "a.jpg"));
        var second = TestJpeg.WriteTo(Path.Combine(rootA, "photos", "b.jpg"));
        using var inCloak = new ManualResetEventSlim();
        using var secondDone = new ManualResetEventSlim();
        var pumped = false;

        // Seam cloak chạy trên UI thread khi cửa sổ đã có HWND nhưng ShellMainWindow/handler forward CHƯA dựng: bơm message lồng ở
        // đây để yêu cầu forward của bản thứ hai (đã được coalescer đăng lên dispatcher) chạy khi chưa có cửa sổ để nhận.
        using var a = ShellRun.Start(rootA, [first], configure: options => options with
        {
            SetCloaked = (_, cloaked) =>
            {
                if (cloaked && !pumped)
                {
                    pumped = true;
                    inCloak.Set();
                    PumpNested(() => secondDone.IsSet, Bound);
                    // Cửa sổ gộp (750 ms) + đăng lên dispatcher: bơm thêm để chắc chắn yêu cầu đã được xả vào lúc này.
                    PumpNested(() => false, TimeSpan.FromSeconds(2));
                }

                return true;
            },
        });
        Assert.True(inCloak.Wait(Bound), "bản đầu không tới bước cloak: " + Diag(a));

        using var b = ShellRun.Start(rootB, [second], prefix: a.Prefix);
        Assert.True(b.WaitForExit(Bound), "bản thứ hai không thoát");
        Assert.Equal(0, b.ExitCode);
        secondDone.Set();

        Assert.True(a.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(a));
        var main = a.Main!;
        Assert.True(WaitUntil(() => a.OnUi(() => string.Equals(
                Path.GetFileName(main.ViewModel.Presenter.CurrentPresentedPath), "b.jpg", StringComparison.OrdinalIgnoreCase))),
            "đường dẫn forward lúc đang khởi động bị bỏ rơi: " + Diag(a));
        StopAndAssertClean(a);
    }

    [Fact]
    public void Startup_WithLoggingOff_WritesNoLogFile()
    {
        var root = ShellRun.NewRoot("log");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        string dataRoot;
        using (var run = ShellRun.Start(root, [jpeg]))
        {
            dataRoot = run.DataRoot;
            Assert.True(run.WaitForMain());
            Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
            StopAndAssertClean(run);
            Assert.False(File.Exists(run.Paths.LogFile), "log tắt (INV-10): không được tạo " + run.Paths.LogFile);
            Assert.False(File.Exists(Path.Combine(root, "test-log", "app.log")));
        }

        Assert.False(Directory.Exists(Path.Combine(dataRoot, "logs")) && Directory.EnumerateFiles(Path.Combine(dataRoot, "logs")).Any());
    }

    [Fact]
    public void Smoke_WithAnImage_TakesNoLockNoPlacementNoReconcileAndExitsZero()
    {
        var root = ShellRun.NewRoot("smoke");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var reconciled = false;
        using var run = ShellRun.Start(root, ["--smoke", jpeg], configure: options => options with
        {
            JournalRecovery = _ =>
            {
                reconciled = true;
                return Task.FromResult<IReadOnlyList<JournalEntry>>([]);
            },
        });

        Assert.True(run.WaitForExit(Bound));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Assert.False(reconciled);
        Assert.DoesNotContain("instanceLock", run.MarkNames);
        Assert.DoesNotContain("placementRestoredBeforeShow", run.MarkNames);
        Assert.False(File.Exists(run.Paths.WindowPlacementFile));
    }

    [Fact]
    public void Startup_FailureAfterTheWindowExists_ReportsOnceAndExitsWithTheFailureCode()
    {
        var root = ShellRun.NewRoot("failure");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var dialogs = new RecordingDialogService();
        // Lỗi ném SAU khi cửa sổ chính đã dựng: dọn nó phát Window.Closed -> Quit(0) TRƯỚC Quit(1); mã lỗi vẫn phải thắng.
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IDialogService>(dialogs);
            },
            OnMainWindowReady = _ => throw new InvalidOperationException("injected startup failure"),
        });

        Assert.True(run.WaitForExit(Bound), "tiến trình không thoát sau khi khởi động hỏng: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(ShellStartup.ExitStartupFailed, run.ExitCode);
        Assert.Equal(1, dialogs.Errors);
    }

    [Fact]
    public void Smoke_WhenNoImageFrameEverArrives_ExitsWithTheSmokeTimeoutCode()
    {
        var root = ShellRun.NewRoot("smoketimeout");
        var broken = Path.Combine(root, "photos", "broken.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "this is not a jpeg"); // tồn tại (được mở như ảnh đầu) nhưng không bao giờ cho khung có ảnh
        using var run = ShellRun.Start(root, ["--smoke", broken], configure: options => options with
        {
            SmokeTimeout = TimeSpan.FromSeconds(2),
        });

        Assert.True(run.WaitForExit(Bound), "smoke không tự đóng khi quá hạn: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(ShellStartup.ExitSmokeTimeout, run.ExitCode);
    }

    [Fact]
    public void Smoke_WithASavedPlacement_NeitherAppliesItNorRewritesIt()
    {
        var root = ShellRun.NewRoot("smokeplacement");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowPlacementFile)!);
        new JsonWindowPlacementStore().Save(paths.WindowPlacementFile, new WindowPlacementData(44, 0, 1, -1, -1, -1, -1, 120, 90, 920, 590));
        var before = File.ReadAllBytes(paths.WindowPlacementFile);

        using var run = ShellRun.Start(root, ["--smoke", jpeg]);

        Assert.True(run.WaitForExit(Bound));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("windowShown", run.MarkNames); // smoke vẫn hiện cửa sổ và vẽ ảnh thật, chỉ không đụng placement
        Assert.DoesNotContain("placementRestoredBeforeShow", run.MarkNames);
        Assert.Equal(before, File.ReadAllBytes(paths.WindowPlacementFile));
    }

    [Fact]
    public void DeviceLost_DuringPresent_RecreatesTheDeviceAndRedrawsTheImage()
    {
        var root = ShellRun.NewRoot("lost");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        var main = run.Main!;
        var surface = main.Surface!;
        var generation = surface.DeviceGeneration;
        Assert.Equal(1, run.OnUi(() => main.ImageCache!.Count));

        var recreated = run.OnUi(() =>
        {
            var armed = true;
            surface.Resources.PresentFault = hr =>
            {
                if (!armed)
                {
                    return hr;
                }

                armed = false;
                return PhotoReview.Shell.Interop.Graphics.DxgiConstants.ErrorDeviceRemoved;
            };
            // Khung có thể bị hoãn (swap chain chưa sẵn sàng): vẽ lại tới khi lỗi đã được tiêm và thiết bị được tạo lại.
            return ShellFrames.RenderUntil(main, () => surface.DeviceGeneration > generation);
        });

        Assert.True(recreated, "thiết bị phải được tạo lại");
        // Khung kế (Invalidate) vẽ lại ảnh: cache được nạp lại từ PixelBuffer sau khi bị xoá lúc mất thiết bị.
        Assert.True(WaitUntil(() => run.OnUi(() => main.ImageCache!.Count) == 1), "ảnh không được vẽ lại sau khi mất thiết bị");
        Assert.True(run.OnUi(() => main.FirstImagePresented));
        StopAndAssertClean(run);
    }

    [Fact]
    public void Shutdown_ReleasesTheMainWindowBeforeTheServiceContainerIsDisposed()
    {
        var root = ShellRun.NewRoot("disposeorder");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        ShellRun? runRef = null;
        // Window.IsDestroyed không đủ (WM_CLOSE đã huỷ cửa sổ trước Cleanup); bề mặt chỉ được trả khi ShellMainWindow.Dispose.
        var probe = new DisposeOrderProbe(() => runRef!.Main!.Surface is null);
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IDialogService>(_ => probe); // qua factory: container sở hữu và dispose nó
            },
        });
        runRef = run;
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));

        StopAndAssertClean(run);

        Assert.True(probe.MainWindowDestroyedAtDispose is not null, "container không dispose dịch vụ thăm dò (không được resolve?)");
        Assert.True(probe.MainWindowDestroyedAtDispose, "cửa sổ + bề mặt phải được giải phóng TRƯỚC container dịch vụ (thứ tự ngược khởi tạo)");
    }

    [Fact]
    public void DeviceLost_RequestsItsOwnRedraw_EvenWhenTheOsSendsNoPaint()
    {
        var root = ShellRun.NewRoot("lostredraw");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        var main = run.Main!;
        var surface = main.Surface!;
        var generation = surface.DeviceGeneration;

        // WM_PAINT (hệ điều hành đánh dấu vùng bẩn sau khi tạo lại swap chain) cũng gọi Invalidate: nuốt nó để chỉ còn đúng
        // một nguồn vẽ lại, là yêu cầu khung của chính đường mất thiết bị.
        run.OnUi(() => main.Window.AddMessageHandler(new SwallowPaint()));
        // Không còn khung nào đang chờ nhịp (khung treo sẽ tự vẽ lại và che lỗi yêu cầu khung của đường mất thiết bị).
        Assert.True(WaitUntil(() => run.OnUi(() => !main.Window.IsRenderRequested && main.ImageCache!.Count == 1)));

        var recreated = run.OnUi(() =>
        {
            var armed = true;
            surface.Resources.PresentFault = hr =>
            {
                if (!armed)
                {
                    return hr;
                }

                armed = false;
                return PhotoReview.Shell.Interop.Graphics.DxgiConstants.ErrorDeviceRemoved;
            };
            // Khung có thể bị hoãn (swap chain chưa sẵn sàng): vẽ lại tới khi lỗi đã được tiêm và thiết bị được tạo lại.
            return ShellFrames.RenderUntil(main, () => surface.DeviceGeneration > generation);
        });

        Assert.True(recreated, "thiết bị phải được tạo lại");
        Assert.True(WaitUntil(() => run.OnUi(() => main.ImageCache!.Count) == 1, TimeSpan.FromSeconds(5)),
            "ảnh không được vẽ lại sau khi mất thiết bị (không có yêu cầu khung kế)");
        StopAndAssertClean(run);
    }

    [Fact]
    public void FirstImage_IsDrawnInTheSameTurnItIsAssigned_NotAtTheNextFrameTick()
    {
        var root = ShellRun.NewRoot("firstsync");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var gate = new object();
        var drawnWhenTheTurnEnded = new List<bool>();
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            OnMainWindowReady = main => main.ViewModel.Viewer.PropertyChanged += (_, e) =>
            {
                // SourcePixelWidth đổi khi presenter gán ảnh (trong callback của sink, TRƯỚC khi hub báo cửa sổ): việc Send đăng ở
                // đây chạy ngay sau lượt gán, trước mọi nhịp khung. Ảnh đầu phải đã được vẽ (không chờ khung kế).
                if (e.PropertyName == "SourcePixelWidth" && main.ViewModel.Viewer.SourcePixelWidth > 0)
                {
                    main.Dispatcher.Post(() =>
                    {
                        lock (gate)
                        {
                            drawnWhenTheTurnEnded.Add(main.FirstImagePresented);
                        }
                    }, UiPriority.Send);
                }
            },
        });

        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() =>
        {
            lock (gate)
            {
                return drawnWhenTheTurnEnded.Count > 0;
            }
        }), "không thấy ảnh đầu được gán");
        lock (gate)
        {
            Assert.True(drawnWhenTheTurnEnded[0], "ảnh đầu phải được vẽ ngay trong lượt gán (mốc 'ảnh đầu đã vẽ'), không đợi nhịp khung kế");
        }

        StopAndAssertClean(run);
    }

    [Fact]
    public void Close_WhileAFileActionHoldsTheGate_KeepsTheWindowUntilTheActionIsDone()
    {
        var root = ShellRun.NewRoot("closegate");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var paths = new PhotoReview.Core.AppPaths(root, Path.Combine(root, "data"), isolateConfig: true);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigFile)!);
        File.WriteAllText(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(
            new PhotoReview.Core.Settings.AppSettings { ConfirmBeforeDelete = true },
            PhotoReview.Core.Settings.AppSettingsJsonContext.Default.AppSettings));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var dialogs = new BlockingConfirmDialogService(entered, release);
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IDialogService>(dialogs);
            },
        });
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        var main = run.Main!;

        // Recycle với "xác nhận trước khi xoá": file action GIỮ cổng trong lúc hộp xác nhận (vòng lồng) đang mở.
        main.Dispatcher.Post(() => _ = main.ViewModel.RecycleAsync());
        Assert.True(entered.Wait(Bound), "hộp xác nhận không hiện: " + Diag(run));
        Assert.True(main.ViewModel.IsFileActionInProgress);

        run.RequestClose(); // WM_CLOSE tới vòng lồng
        SpinWait.SpinUntil(() => false, 400);
        Assert.False(run.HasExited);
        Assert.False(main.IsClosingOrClosed, "đóng giữa lúc file action giữ cổng phải bị hoãn (R7-7)");
        Assert.False(main.Window.IsDestroyed);

        release.Set(); // người dùng trả lời "không": action kết thúc, cổng thả, cửa sổ tự đóng
        Assert.True(run.WaitForExit(Bound), "cửa sổ không tự đóng sau khi file action xong: " + Diag(run));
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void Closing_ReleasesTheRenderSurface_WhileTheWindowIsStillAlive()
    {
        // Gốc của dây chuyền treo trên CI (runner WARP + DWM): swap chain flip-model còn Present xếp hàng bị giải phóng SAU khi HWND
        // đã huỷ thì ClearState/Flush kẹt vĩnh viễn trên UI thread (và mọi D3D11CreateDevice sau đó). Thiết bị phải được trả khi
        // cửa sổ còn sống: ngay trong Closing (handler của test đăng ký sau handler của cửa sổ chính nên chạy sau nó).
        var root = ShellRun.NewRoot("closing-gpu");
        using var run = ShellRun.Start(root, [TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"))]);
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + Diag(run));
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented), "ảnh đầu không được vẽ: " + Diag(run));
        var main = run.Main!;
        using var observed = new ManualResetEventSlim();
        var surfaceReleased = false;
        var windowAlive = false;
        run.OnUi(() => main.Window.Closing += (_, _) =>
        {
            surfaceReleased = main.Surface is null && main.ImageCache is null;
            windowAlive = !main.Window.IsDestroyed;
            observed.Set();
        });

        StopAndAssertClean(run);

        Assert.True(observed.IsSet, "Closing không được phát");
        Assert.True(windowAlive, "HWND đã huỷ trước Closing");
        Assert.True(surfaceReleased, "bề mặt D3D/D2D và cache GPU phải được trả khi bắt đầu đóng, trước khi HWND bị huỷ");
    }

    [Fact]
    public void Startup_ShutdownIsClean_NoWindowDispatcherOrGuiObjectLeak()
    {
        var root = ShellRun.NewRoot("leak");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        RunOnce(jpeg, root, "warm"); // lần đầu nạp lớp cửa sổ, DLL, cache tĩnh
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var (gdi0, user0) = (GuiResources(0), GuiResources(1)); // GR_GDIOBJECTS = 0, GR_USEROBJECTS = 1
        var handles0 = Process.GetCurrentProcess().HandleCount;

        for (var i = 0; i < 4; i++)
        {
            RunOnce(jpeg, root, "r" + i);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var (gdi1, user1) = (GuiResources(0), GuiResources(1));
        var handles1 = Process.GetCurrentProcess().HandleCount;
        Assert.True((long)user1 - user0 <= 2, $"USER objects {user0} -> {user1}");
        Assert.True((long)gdi1 - gdi0 <= 2, $"GDI objects {gdi0} -> {gdi1}");
        Assert.True(handles1 - handles0 <= 40, $"process handles {handles0} -> {handles1}");
    }

    private static void RunOnce(string jpeg, string root, string label)
    {
        using var run = ShellRun.Start(Path.Combine(root, label), [jpeg]);
        Assert.True(run.WaitForMain());
        Assert.True(WaitUntil(() => run.Main!.FirstImagePresented));
        StopAndAssertClean(run);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint process, uint flags);

    private static uint GuiResources(uint flags) => GetGuiResources(Process.GetCurrentProcess().Handle, flags);

    /// <summary>Hộp xác nhận xoá chạy một vòng message lồng (như hộp thoại modal) tới khi được thả; trả "không đồng ý".</summary>
    private sealed class BlockingConfirmDialogService(ManualResetEventSlim entered, ManualResetEventSlim release) : IDialogService
    {
        public bool ShowConfirmation(string title, string message)
        {
            entered.Set();
            PumpNested(() => release.IsSet, Bound);
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

        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
        {
        }
    }

    /// <summary>Xử lý WM_PAINT tại chỗ (xác nhận vùng bẩn) để <c>ShellWindow</c> không gọi Invalidate thay cho renderer.</summary>
    private sealed unsafe class SwallowPaint : PhotoReview.Shell.Win32.Hosting.IWindowMessageHandler
    {
        public bool TryHandle(in PhotoReview.Shell.Win32.Hosting.WindowMessage message, out nint result)
        {
            result = 0;
            if (message.Msg != 0x000F)
            {
                return false;
            }

            PhotoReview.Shell.Win32.Hosting.PendingInterop.PendingUser32.ValidateRect(message.Hwnd, null);
            return true;
        }
    }

    /// <summary>Dịch vụ (do container tạo -> container dispose) ghi lại cửa sổ chính đã được giải phóng chưa khi container bị dispose.</summary>
    private sealed class DisposeOrderProbe(Func<bool> mainWindowReleased) : IDialogService, IDisposable
    {
        public bool? MainWindowDestroyedAtDispose { get; private set; }

        public void Dispose() => MainWindowDestroyedAtDispose = mainWindowReleased();

        public bool ShowConfirmation(string title, string message) => false;

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

        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
        {
        }
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public bool Confirm { get; init; }

        public int Confirmations { get; private set; }

        public int RecoveryShown { get; private set; }

        public int Errors { get; private set; }

        public bool ShowConfirmation(string title, string message)
        {
            Confirmations++;
            return Confirm;
        }

        public void ShowMessage(string title, string message)
        {
        }

        public void ShowError(string title, string message) => Errors++;

        public string? PickFolder(string? initialFolder = null) => null;

        public bool ShowBatchReview(IReadOnlyList<string> paths) => false;

        public void ShowRecovery() => RecoveryShown++;

        public void ShowDiagnostics()
        {
        }

        public bool ShowSettings() => false;

        public void ShowBenchmark(string? folder = null)
        {
        }

        public void ShowSkippedFiles(IReadOnlyList<SkippedEntry> entries)
        {
        }
    }
}
