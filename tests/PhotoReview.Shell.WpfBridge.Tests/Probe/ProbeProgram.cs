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
        if (args.Length == 0 || args[0] is not ("--probe-lazy" or "--probe-timing"))
        {
            Console.Error.WriteLine("usage: --probe-lazy | --probe-timing");
            return 2;
        }

        var timing = args[0] == "--probe-timing";

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
                exit = timing ? RunTiming(provider, loop) : RunScenario(provider, loop);
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
        var port = SecondaryWindowPorts.CreateLazy(provider, loop).Value ?? throw new InvalidOperationException("no bridge");
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
        var lazy = SecondaryWindowPorts.CreateLazy(provider, loop);
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
