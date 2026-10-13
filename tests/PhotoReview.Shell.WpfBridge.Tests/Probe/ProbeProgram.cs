using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Hosting;
using Xunit;

namespace PhotoReview.Shell.WpfBridge.Tests.Probe;

/// <summary>
/// Entry point tự viết của project test (xUnit không gọi nó). <c>--probe-lazy</c>: tiến trình sạch dựng cổng cửa sổ phụ như
/// shell, mở một cửa sổ phụ modal rồi đóng, in ra danh sách assembly WPF đã nạp ở từng bước (test cha so sánh).
/// Dòng kết quả: <c>STAGE tên | wpf=a,b,c</c>; thêm <c>TIMING tên=ms</c>.
/// </summary>
internal static class ProbeProgram
{
    private static readonly string[] WpfAssemblyPrefixes = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "PhotoReview.App.WpfWindows"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--probe-lazy" or "--probe-timing" or "--probe-native" or "--probe-native-timing"))
        {
            Console.Error.WriteLine("usage: --probe-lazy | --probe-timing | --probe-native | --probe-native-timing");
            return 2;
        }

        var timing = args[0] == "--probe-timing";
        var mode = args[0];

        Report("start");
        using var dispatcher = new Win32UiDispatcher(null);
        using var context = Win32UiSynchronizationContext.Install(dispatcher);
        var loop = new MessageLoop(dispatcher);
        var root = Path.Combine(Path.GetTempPath(), "PhotoReview-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddSingleton(new ReviewMetrics());
        services.AddSingleton(new SettingsStore(new AppPaths(root), new PhysicalFileSystem(), NullLog.Instance));
        var provider = services.BuildServiceProvider();

        var exit = -1;
        dispatcher.Post(() =>
        {
            try
            {
                exit = mode switch
                {
                    "--probe-native" => RunNativeScenario(provider, loop),
                    "--probe-native-timing" => RunNativeTiming(provider, loop),
                    _ => timing ? RunTiming(provider, loop) : RunScenario(provider, loop),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + ex);
                exit = 1;
            }
            finally
            {
                MessageLoop.Quit(exit);
            }
        }, UiPriority.Normal);
        loop.Run();
        try { Directory.Delete(root, true); } catch (IOException) { }
        return exit;
    }

    /// <summary>Thời gian mở+đóng (đã gồm vòng lặp lồng WPF) lần đầu và lần sau của Diagnostics và Settings, cùng tiến trình.</summary>
    private static System.Diagnostics.Stopwatch? Clock;
    private static TimeSpan? VisibleAt;

    private static int RunTiming(IServiceProvider provider, MessageLoop loop)
    {
        var port = WpfBridgePort.CreateLazy(provider, loop).Value ?? throw new InvalidOperationException("no bridge");
        Measure(loop, "diagnostics-1st", () => port.ShowDiagnostics(0));
        Measure(loop, "diagnostics-2nd", () => port.ShowDiagnostics(0));
        Measure(loop, "settings-1st", () => port.ShowSettings(0, SettingsTarget.Default));
        Measure(loop, "settings-2nd", () => port.ShowSettings(0, SettingsTarget.Default));
        (port as IDisposable)?.Dispose();
        return 0;
    }

    private static void Measure(MessageLoop loop, string name, Action open)
    {
        loop.Dispatcher.Post(() => ScheduleClose(loop.Dispatcher), UiPriority.Normal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        VisibleAt = null;
        Clock = sw;
        open();
        sw.Stop();
        Console.WriteLine("TIMING " + name + "-visible-ms=" + (VisibleAt?.TotalMilliseconds ?? -1).ToString("F0", CultureInfo.InvariantCulture)
            + " total-ms=" + sw.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
    }

    private static int RunScenario(IServiceProvider provider, MessageLoop loop)
    {
        var lazy = WpfBridgePort.CreateLazy(provider, loop);
        Report("lazy-created"); // chưa đọc .Value: chưa nạp cả cầu
        var dialogs = new Win32DialogService(() => 0, new FolderPicker(new UnwiredShellFolderDialog(), () => 0), lazy);
        Report("dialog-service-created");
        var port = lazy.Value;
        Report("port-loaded"); // host tạo xong, chưa mở cửa sổ: WPF vẫn chưa nạp
        if (port is null)
        {
            Console.WriteLine("ERROR port null");
            return 1;
        }

        // Đóng cửa sổ ngay khi nó hiện: việc đăng ký trước chạy trong vòng lặp lồng của ShowDialog.
        loop.Dispatcher.Post(() => ScheduleClose(loop.Dispatcher), UiPriority.Normal);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        dialogs.ShowDiagnostics();
        sw.Stop();
        Console.WriteLine("TIMING first-open-and-close-ms=" + sw.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
        Report("after-first-window");
        (port as IDisposable)?.Dispose();
        Report("after-dispose");
        return 0;
    }

    /// <summary>
    /// WP-35 GĐ1/GĐ5: DI THẬT của shell (không còn cầu WPF); mở bốn cửa sổ Win32 gốc và đóng từng cái bằng WM_CLOSE. WPF và cả
    /// assembly cầu KHÔNG được nạp ở bất kỳ bước nào.
    /// </summary>
    private static int RunNativeScenario(IServiceProvider unused, MessageLoop loop)
    {
        _ = unused;
        var shell = PhotoReview.Shell.Win32.Startup.ShellCompositionRoot.Build(
            new PhotoReview.Shell.Win32.Startup.ShellHostServices(loop.Dispatcher, () => 0, loop),
            new PhotoReview.Shell.Win32.Startup.ShellPresentationHub(),
            services =>
            {
                services.AddSingleton<IAppPaths>(new AppPaths(Path.Combine(Path.GetTempPath(), "PhotoReview-probe-native")));
            });
        Report("shell-built");
        var dialogs = shell.GetRequiredService<IDialogService>();
        var zoom = shell.GetRequiredService<PhotoReview.App.Services.IZoomPromptService>();
        Report("services-resolved");

        void CloseSoon() => loop.Dispatcher.Post(() => ScheduleClose(loop.Dispatcher), UiPriority.Normal);
        CloseSoon();
        dialogs.ShowDiagnostics();
        Report("after-diagnostics");
        CloseSoon();
        dialogs.ShowSkippedFiles([new SkippedEntry(@"C:\photos\a.jpg", "locked")]);
        CloseSoon();
        var batch = dialogs.ShowBatchReview([new BatchReviewItem(@"C:\photos\a.jpg", 10)]);
        CloseSoon();
        var picked = zoom.PromptCustomZoom(100);
        Report("after-four-native-windows");

        // WP-35 GĐ2: Benchmark (modeless: trả về ngay, đóng bằng WM_CLOSE) và Action profiles (modal, qua cổng).
        var before = VisibleWindows().Count;
        dialogs.ShowBenchmark(null);
        var benchmarkOpened = VisibleWindows().Count == before + 1;
        dialogs.ShowBenchmark(null); // lần hai: không tạo cửa sổ thứ hai
        var benchmarkSingle = VisibleWindows().Count == before + 1;
        foreach (var hwnd in VisibleWindows())
        {
            SendMessage(hwnd, WindowMessages.WmClose, 0, 0);
        }

        Report("after-benchmark");
        CloseSoon();
        var port = shell.GetRequiredService<PhotoReview.Shell.Win32.Dialogs.SecondaryWindowPortHost>().Port.Value!;
        var edited = port.ShowActionProfiles(0, [new PhotoReview.Core.Settings.ReviewAction { Name = "Keep", Shortcut = "F6" }]);
        Report("after-action-profiles");

        // WP-35 GĐ4: Recovery (modal, journal rỗng của thư mục tạm riêng) cũng là hộp thoại Win32 gốc: không nạp WPF/cầu.
        // Không có journal thì cửa sổ không mở và ScheduleClose sẽ quay mãi: dừng sớm thay vì chờ.
        if (shell.GetService<PhotoReview.Core.FileActions.OperationJournal>() is null)
        {
            Console.WriteLine("ERROR no OperationJournal in the shell DI");
            return 1;
        }

        var recoveryBefore = VisibleWindows().Count;
        CloseSoon();
        dialogs.ShowRecovery();
        var recoveryClosed = VisibleWindows().Count == recoveryBefore;
        Report("after-recovery");
        // WP-35 GĐ3a: Settings (modal, every page present) closed with WM_CLOSE = Cancel -> false; still no WPF.
        CloseSoon();
        var settingsSaved = dialogs.ShowSettings();
        Report("after-settings");
        Console.WriteLine("RESULT batch=" + batch + " zoom=" + (picked?.ToString(CultureInfo.InvariantCulture) ?? "null")
            + " benchmark=" + benchmarkOpened + "/" + benchmarkSingle + " actionProfiles=" + (edited is null ? "null" : "list")
            + " recovery=" + recoveryClosed + " settings=" + settingsSaved);
        (shell as IDisposable)?.Dispose();
        return batch || picked is not null ? 1 : 0;
    }

    /// <summary>Thời gian tới khi cửa sổ đã tạo + bố cục + hiện (callback ready), xen kẽ bốn cửa sổ, nhiều vòng.</summary>
    private static int RunNativeTiming(IServiceProvider provider, MessageLoop loop)
    {
        _ = loop;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double started = 0;
        var seen = new List<double>();
        var port = new PhotoReview.Shell.Win32.Dialogs.Native.Win32SecondaryWindowPort(provider, null, d =>
        {
            seen.Add(clock.Elapsed.TotalMilliseconds - started);
            d.Close();
        }, settingsEnabled: () => true);
        var skipped = new[] { new SkippedEntry(@"C:\photos\a.jpg", "locked"), new SkippedEntry(@"C:\photos\b.jpg", "denied") };
        var batch = new[] { new BatchReviewItem(@"C:\photos\a.jpg", 10), new BatchReviewItem(@"C:\photos\b.jpg", 20) };
        string Last() => seen[^1].ToString("F1", CultureInfo.InvariantCulture);
        for (var round = 1; round <= 8; round++)
        {
            started = clock.Elapsed.TotalMilliseconds;
            port.PromptCustomZoom(0, 100);
            Console.WriteLine($"TIMING native-zoom-r{round}-ms={Last()}");
            started = clock.Elapsed.TotalMilliseconds;
            port.ShowDiagnostics(0);
            Console.WriteLine($"TIMING native-diagnostics-r{round}-ms={Last()}");
            started = clock.Elapsed.TotalMilliseconds;
            port.ShowSkippedFiles(0, skipped);
            Console.WriteLine($"TIMING native-skipped-r{round}-ms={Last()}");
            started = clock.Elapsed.TotalMilliseconds;
            port.ShowBatchReview(0, batch);
            Console.WriteLine($"TIMING native-batch-r{round}-ms={Last()}");
            started = clock.Elapsed.TotalMilliseconds;
            port.ShowSettings(0, SettingsTarget.Default);
            Console.WriteLine($"TIMING native-settings-r{round}-ms={Last()}");
        }

        port.Dispose();
        return 0;
    }

    private static List<nint> VisibleWindows()
    {
        var windows = new List<nint>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        }, 0);
        return windows;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    private static void ScheduleClose(Win32UiDispatcher dispatcher)
    {
        // Tìm cửa sổ WPF của thread này và gửi WM_CLOSE; nếu chưa có thì thử lại ở lượt sau (không chờ cố định).
        var windows = new List<nint>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        }, 0);
        if (windows.Count == 0)
        {
            dispatcher.Post(() => ScheduleClose(dispatcher), UiPriority.Background);
            return;
        }

        VisibleAt ??= Clock?.Elapsed;
        foreach (var hwnd in windows)
        {
            User32.PostMessage(hwnd, WindowMessages.WmClose, 0, 0);
        }
    }

    private static void Report(string stage)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(n => WpfAssemblyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)) || n == "PhotoReview.Shell.WpfBridge")
            .OrderBy(n => n, StringComparer.Ordinal);
        Console.WriteLine("STAGE " + stage + " | wpf=" + string.Join(",", loaded));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);
}
