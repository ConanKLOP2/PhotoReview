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

    private static string Diag(ShellRun run) =>
        $"marks=[{string.Join(", ", run.MarkNames)}] error={run.Error} exit={run.ExitCode} firstPresent={run.Main?.FirstPresentDone}";

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

        run.OnUi(() =>
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
            main.RenderNow();
        });

        Assert.True(surface.DeviceGeneration > generation, "thiết bị phải được tạo lại");
        // Khung kế (Invalidate) vẽ lại ảnh: cache được nạp lại từ PixelBuffer sau khi bị xoá lúc mất thiết bị.
        Assert.True(WaitUntil(() => run.OnUi(() => main.ImageCache!.Count) == 1), "ảnh không được vẽ lại sau khi mất thiết bị");
        Assert.True(run.OnUi(() => main.FirstImagePresented));
        StopAndAssertClean(run);
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
        Assert.True(user1 - user0 <= 2, $"USER objects {user0} -> {user1}");
        Assert.True(gdi1 - gdi0 <= 2, $"GDI objects {gdi0} -> {gdi1}");
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

    private sealed class RecordingDialogService : IDialogService
    {
        public bool Confirm { get; init; }

        public int Confirmations { get; private set; }

        public int RecoveryShown { get; private set; }

        public bool ShowConfirmation(string title, string message)
        {
            Confirmations++;
            return Confirm;
        }

        public void ShowMessage(string title, string message)
        {
        }

        public void ShowError(string title, string message)
        {
        }

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
