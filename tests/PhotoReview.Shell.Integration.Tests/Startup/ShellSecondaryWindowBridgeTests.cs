using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Startup;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// NOWPF-SHELL-WIRE-WPF-BRIDGE: shell THẬT (ShellStartup + DI thật, HWND trên desktop ẩn, WARP) MỞ được cửa sổ phụ WPF qua
/// <c>IDialogService</c>/<c>IZoomPromptService</c> đăng ký mặc định (không override cổng): Settings (lưu), Recovery (không đổi dữ liệu),
/// Diagnostics, SkippedFiles, Zoom tuỳ chọn, Benchmark modeless; rồi đóng shell khi Benchmark VÀ một hộp modal còn mở (không treo,
/// thoát mã 0, không cửa sổ mồ côi). Project này không UseWPF nên chạm WPF bằng reflection (Application là một-cho-tiến-trình và gắn
/// thread tạo: cả kịch bản chạy trong MỘT lần chạy shell).
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellSecondaryWindowBridgeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void SecondaryWindows_OpenThroughTheRealShell_AndTheShellClosesCleanlyWithThemOpen()
    {
        var root = ShellRun.NewRoot("wpfbridge");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        // Launcher giả: nếu cầu bị đột biến/vắng, cổng tạm không được mở notepad/trình soạn thảo thật của máy.
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(new NoLauncher());
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());
        var viewModel = main.ViewModel;

        // 1. Settings: modal, owner = HWND shell, Lưu -> trả true và ghi config.json (SettingsStore.Changed nổ).
        var configBefore = File.Exists(run.Paths.ConfigFile) ? File.GetLastWriteTimeUtc(run.Paths.ConfigFile) : (DateTime?)null;
        var settings = Open(run, () => viewModel.ShowSettings());
        var settingsHwnd = WaitOwned(run, uiThread, "settings");
        Assert.False(IsWindowEnabled(main.Hwnd), "modal phải vô hiệu hoá cửa sổ chủ");
        run.OnUi(() => Click(OnlyVisibleWindow(), "Save_Click"));
        Finished(settings, "Settings không đóng sau Lưu: " + run.Diagnostics);
        WaitNoOwned(run, uiThread, "settings closed");
        Assert.True(File.Exists(run.Paths.ConfigFile), "Lưu phải ghi config.json");
        Assert.True(configBefore is null || File.GetLastWriteTimeUtc(run.Paths.ConfigFile) >= configBefore);
        Assert.NotEqual(0, settingsHwnd);
        Assert.True(IsWindowEnabled(main.Hwnd));

        // 2. Recovery: mở rồi đóng, dữ liệu không đổi.
        var dataBefore = Snapshot(run.DataRoot);
        var recovery = Open(run, () => viewModel.ShowRecovery());
        PostClose(WaitOwned(run, uiThread, "recovery"));
        Finished(recovery, "Recovery không đóng");
        WaitNoOwned(run, uiThread, "recovery closed");
        Assert.Equal(dataBefore, Snapshot(run.DataRoot));

        // 3. Diagnostics.
        var diagnostics = Open(run, () => viewModel.ShowDiagnostics());
        PostClose(WaitOwned(run, uiThread, "diagnostics"));
        Finished(diagnostics, "Diagnostics không đóng");
        WaitNoOwned(run, uiThread, "diagnostics closed");

        // 4. Zoom tuỳ chọn: Huỷ rồi OK 150 (qua IZoomPromptService đăng ký mặc định, không còn "null vì cầu vắng").
        var zoomCancelled = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        PostClose(WaitOwned(run, uiThread, "zoom prompt"));
        Finished(zoomCancelled, "hộp zoom không đóng");
        WaitNoOwned(run, uiThread, "zoom prompt closed");
        var zoomOk = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        WaitOwned(run, uiThread, "zoom prompt again");
        run.OnUi(() =>
        {
            var window = OnlyVisibleWindow();
            var box = window.GetType().GetMethod("FindName")!.Invoke(window, ["PercentText"])!;
            box.GetType().GetProperty("Text")!.SetValue(box, "150");
            Click(window, "Ok_Click");
        });
        Finished(zoomOk, "hộp zoom không đóng sau OK");
        WaitNoOwned(run, uiThread, "zoom prompt closed after OK");

        // 5. Benchmark modeless (trả về ngay, chủ vẫn bật) + một hộp modal Diagnostics mở nữa, rồi đóng SHELL.
        run.OnUi(() => viewModel.ShowBenchmark());
        WaitOwned(run, uiThread, "benchmark");
        Assert.True(IsWindowEnabled(main.Hwnd), "Benchmark là modeless: cửa sổ chủ vẫn bật");
        var modal = Open(run, () => viewModel.ShowDiagnostics());
        Assert.True(SpinWait.SpinUntil(() => OwnedWindows(uiThread, main.Hwnd).Count == 2, Bound), "benchmark + diagnostics không cùng mở");

        run.RequestClose();

        Assert.True(run.WaitForExit(Bound), "shell không thoát khi cửa sổ phụ còn mở: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Finished(modal, "hộp modal phải trả về khi shell thoát (không treo vòng lồng)");
    }

    private sealed class NoLauncher : IShellLauncher
    {
        public bool TryOpenFile(string path, string verb) => true;

        public bool TryRun(string fileName, string arguments) => true;
    }

    private static void Finished(Task task, string what) => Assert.True(task.Wait(Bound), what);

    private static Task Open(ShellRun run, Action action) => run.Main!.Dispatcher.InvokeAsync(action); // chặn tới khi cửa sổ đóng (modal)

    private static nint WaitOwned(ShellRun run, uint uiThread, string what)
    {
        nint found = 0;
        Assert.True(SpinWait.SpinUntil(() => (found = OwnedWindows(uiThread, run.Main!.Hwnd).FirstOrDefault()) != 0, Bound),
            $"cửa sổ phụ '{what}' không hiện: " + run.Diagnostics);
        return found;
    }

    private static void WaitNoOwned(ShellRun run, uint uiThread, string what)
        => Assert.True(SpinWait.SpinUntil(() => OwnedWindows(uiThread, run.Main!.Hwnd).Count == 0, Bound), $"cửa sổ phụ còn mở: {what}");

    private static void PostClose(nint hwnd) => Assert.True(User32.PostMessage(hwnd, User32.WmClose, 0, 0));

    private static string Snapshot(string dir)
        => !Directory.Exists(dir)
            ? "(none)"
            : string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => f + ":" + new FileInfo(f).Length + ":" + File.GetLastWriteTimeUtc(f).Ticks));

    // WPF qua reflection: project test này không UseWPF (luật L-WPF); assembly nạp khi cầu mở cửa sổ phụ đầu tiên.
    private static object OnlyVisibleWindow()
    {
        var application = Type.GetType("System.Windows.Application, PresentationFramework")!;
        var current = application.GetProperty("Current")!.GetValue(null)!;
        var windows = (IEnumerable)application.GetProperty("Windows")!.GetValue(current)!;
        var visible = windows.Cast<object>()
            .Where(w => (bool)w.GetType().GetProperty("IsVisible")!.GetValue(w)!)
            .ToList();
        return Assert.Single(visible);
    }

    private static void Click(object window, string handler)
    {
        var routed = Activator.CreateInstance(Type.GetType("System.Windows.RoutedEventArgs, PresentationCore")!)!;
        window.GetType().GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, routed]);
    }

    private static List<nint> OwnedWindows(uint uiThread, nint owner)
    {
        var found = new List<nint>();
        EnumThreadWindows(uiThread, (hwnd, _) =>
        {
            if (hwnd != owner && IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == owner)
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
}

/// <summary>Quyết định đóng cửa sổ phụ ở <c>Closing</c> (không cần desktop): chỉ khi việc đóng shell không bị huỷ.</summary>
public sealed class ShellClosingSecondaryWindowsTests
{
    [Fact]
    public void Closing_ClosesTheSecondaryWindows_OnlyWhenTheCloseIsNotCancelled()
    {
        var closed = 0;

        ShellStartup.CloseSecondaryWindowsUnlessCancelled(new System.ComponentModel.CancelEventArgs(cancel: true), () => closed++);
        Assert.Equal(0, closed); // đang có file action: shell ở lại, cửa sổ phụ giữ nguyên

        ShellStartup.CloseSecondaryWindowsUnlessCancelled(new System.ComponentModel.CancelEventArgs(cancel: false), () => closed++);
        Assert.Equal(1, closed);
    }
}
