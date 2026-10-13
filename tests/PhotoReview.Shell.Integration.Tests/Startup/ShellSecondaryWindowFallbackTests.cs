using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Menus;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// NOWPF-AOT-SETTINGS-FALLBACK: shell THẬT (HWND trên desktop ẩn, WARP) không có cổng cửa sổ phụ đăng ký (như bản Native
/// AOT): lệnh Settings/Diagnostics đi hết đường lệnh thật tới launcher giả và dòng trạng thái; không ném, không treo. Không
/// giả định kích thước màn hình.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellSecondaryWindowFallbackTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class RecordingLauncher : IShellLauncher
    {
        private readonly List<(string Path, string Verb)> _opens = [];

        public (string Path, string Verb)[] Opens
        {
            get { lock (_opens) { return [.. _opens]; } }
        }

        public bool TryOpenFile(string path, string verb)
        {
            lock (_opens)
            {
                _opens.Add((path, verb));
            }

            return true;
        }

        public bool TryRun(string fileName, string arguments) => false;
    }

    [Fact]
    public void SettingsFallsBack_WithNoBridge_AndRecoveryStillOpensAsAWin32Window()
    {
        var root = ShellRun.NewRoot("aotfallback");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        var launcher = new RecordingLauncher();
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(launcher);
                services.AddSingleton<SecondaryWindowPortLoader>((_, _) => null); // mô phỏng bản AOT: cầu vắng
            },
        });
        Assert.True(run.WaitForMain(), "cửa sổ chính không dựng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "ảnh đầu không được vẽ");

        // WP-35 GĐ3 (đủ 7 trang): Settings là hộp thoại Win32 gốc nên không còn rơi về cổng tạm khi cầu vắng: MỞ THẬT (modal, owner =
        // HWND shell), không chạm launcher/config.json, rồi đóng bằng WM_CLOSE.
        var uiThread = run.OnUi(() => GetCurrentThreadId());
        var settings = run.Main!.Dispatcher.InvokeAsync(() => ((IContextMenuActions)run.Main!).ShowSettings());
        nint settingsDialog = 0;
        Assert.True(SpinWait.SpinUntil(() => (settingsDialog = OwnedBy(uiThread, run.Main!.Hwnd)) != 0, Bound),
            "Settings không mở khi cầu vắng: " + run.Diagnostics);
        Assert.Equal(Tr.SettingsWindowTitle, run.OnUi(() => WindowText(settingsDialog)));
        Assert.True(PostMessage(settingsDialog, 0x0010 /* WM_CLOSE */, 0, 0));
        Assert.True(SpinWait.SpinUntil(() => settings.IsCompleted, Bound), "Settings không đóng");
        Assert.Empty(launcher.Opens);
        Assert.NotEqual(Tr.StatusSecondaryUnavailableSettings, run.OnUi(() => run.Main!.ViewModel.StatusText));

        // WP-35 GĐ1/GĐ4: Diagnostics và Recovery đã có hộp thoại Win32 gốc nên không còn rơi về cổng tạm (kể cả khi cầu vắng):
        // Recovery MỞ THẬT (modal, owner = HWND shell), không chạm launcher, rồi đóng bằng WM_CLOSE.
        var recovery = run.Main!.Dispatcher.InvokeAsync(() => run.Main!.ViewModel.ShowRecovery());
        nint dialog = 0;
        Assert.True(SpinWait.SpinUntil(() => (dialog = OwnedBy(uiThread, run.Main!.Hwnd)) != 0, Bound),
            "Recovery không mở khi cầu vắng: " + run.Diagnostics);
        Assert.Equal(Tr.RecoveryTitle, run.OnUi(() => WindowText(dialog)));
        Assert.NotEqual(Tr.StatusSecondaryUnavailableRecovery, run.OnUi(() => run.Main!.ViewModel.StatusText));
        Assert.True(PostMessage(dialog, 0x0010 /* WM_CLOSE */, 0, 0));
        Assert.True(SpinWait.SpinUntil(() => recovery.IsCompleted, Bound), "Recovery không đóng");
        Assert.Empty(launcher.Opens); // Recovery không chạm launcher

        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "shell không thoát sau WM_CLOSE: " + run.Diagnostics);
        Assert.Null(run.Error);
    }

    private static nint OwnedBy(uint uiThread, nint owner)
    {
        nint found = 0;
        EnumThreadWindows(uiThread, (hwnd, _) =>
        {
            if (hwnd != owner && IsWindowVisible(hwnd) && GetWindow(hwnd, 4 /* GW_OWNER */) == owner)
            {
                found = hwnd;
            }

            return true;
        }, 0);
        return found;
    }

    private static string WindowText(nint hwnd)
    {
        var buffer = new char[256];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, char[] text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
