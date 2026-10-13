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
/// Entry point tá»± viáº¿t cá»§a project test (xUnit khÃ´ng gá»i nÃ³). <c>--probe-lazy</c>: tiáº¿n trÃ¬nh sáº¡ch dá»±ng cá»•ng cá»­a sá»• phá»¥ nhÆ°
/// shell, má»Ÿ má»™t cá»­a sá»• phá»¥ modal rá»“i Ä‘Ã³ng, in ra danh sÃ¡ch assembly WPF Ä‘Ã£ náº¡p á»Ÿ tá»«ng bÆ°á»›c (test cha so sÃ¡nh).
/// DÃ²ng káº¿t quáº£: <c>STAGE tÃªn | wpf=a,b,c</c>; thÃªm <c>TIMING tÃªn=ms</c>.
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

    /// <summary>Thá»i gian má»Ÿ+Ä‘Ã³ng (Ä‘Ã£ gá»“m vÃ²ng láº·p lá»“ng WPF) láº§n Ä‘áº§u vÃ  láº§n sau cá»§a Diagnostics vÃ  Settings, cÃ¹ng tiáº¿n trÃ¬nh.</summary>
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
        Report("lazy-created"); // chÆ°a Ä‘á»c .Value: chÆ°a náº¡p cáº£ cáº§u
        var dialogs = new Win32DialogService(() => 0, new FolderPicker(new UnwiredShellFolderDialog(), () => 0), lazy);
        Report("dialog-service-created");
        var port = lazy.Value;
        Report("port-loaded"); // host táº¡o xong, chÆ°a má»Ÿ cá»­a sá»•: WPF váº«n chÆ°a náº¡p
        if (port is null)
        {
            Console.WriteLine("ERROR port null");
            return 1;
        }

        // ÄÃ³ng cá»­a sá»• ngay khi nÃ³ hiá»‡n: viá»‡c Ä‘Äƒng kÃ½ trÆ°á»›c cháº¡y trong vÃ²ng láº·p lá»“ng cá»§a ShowDialog.
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
    /// WP-35 GÄ1: DI THáº¬T cá»§a shell (cáº§u WPF Ä‘Äƒng kÃ½ máº·c Ä‘á»‹nh); má»Ÿ bá»‘n cá»­a sá»• Win32 gá»‘c vÃ  Ä‘Ã³ng tá»«ng cÃ¡i báº±ng WM_CLOSE. WPF vÃ  cáº£
    /// assembly cáº§u KHÃ”NG Ä‘Æ°á»£c náº¡p á»Ÿ báº¥t ká»³ bÆ°á»›c nÃ o.
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
        Console.WriteLine("RESULT batch=" + batch + " zoom=" + (picked?.ToString(CultureInfo.InvariantCulture) ?? "null"));
        (shell as IDisposable)?.Dispose();
        return batch || picked is not null ? 1 : 0;
    }

    /// <summary>Thá»i gian tá»›i khi cá»­a sá»• Ä‘Ã£ táº¡o + bá»‘ cá»¥c + hiá»‡n (callback ready), xen káº½ bá»‘n cá»­a sá»•, nhiá»u vÃ²ng.</summary>
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
        });
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
        }

        port.Dispose();
        return 0;
    }

    private static void ScheduleClose(Win32UiDispatcher dispatcher)
    {
        // TÃ¬m cá»­a sá»• WPF cá»§a thread nÃ y vÃ  gá»­i WM_CLOSE; náº¿u chÆ°a cÃ³ thÃ¬ thá»­ láº¡i á»Ÿ lÆ°á»£t sau (khÃ´ng chá» cá»‘ Ä‘á»‹nh).
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
