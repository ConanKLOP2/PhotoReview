using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Core.Abstractions;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-35 GÄ1: shell THáº¬T (DI tháº­t, desktop áº©n) má»Ÿ Diagnostics, SkippedFiles, BatchReview vÃ  há»™p zoom báº±ng há»™p thoáº¡i Win32 Gá»C
/// (lá»›p <c>#32770</c>, owner = HWND shell, modal) mÃ  KHÃ”NG cáº§n cáº§u WPF (cá»•ng cáº§u mÃ´ phá»ng váº¯ng nhÆ° báº£n AOT), vÃ  Ä‘Ã³ng shell khi má»™t
/// há»™p thoáº¡i cÃ²n má»Ÿ: thoÃ¡t mÃ£ 0, há»™p modal tráº£ vá», khÃ´ng treo.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellNativeDialogsIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class NoLauncher : IShellLauncher
    {
        public bool TryOpenFile(string path, string verb) => true;

        public bool TryRun(string fileName, string arguments) => false;
    }

    [Fact]
    public void NativeDialogs_OpenOwnedAndModal_WithoutTheBridge_AndTheShellClosesCleanlyWithOneOpen()
    {
        var root = ShellRun.NewRoot("nativedlg");
        var jpeg = TestJpeg.WriteTo(Path.Combine(root, "photos", "a.jpg"));
        using var run = ShellRun.Start(root, [jpeg], configure: options => options with
        {
            ServiceOverrides = services =>
            {
                options.ServiceOverrides?.Invoke(services);
                services.AddSingleton<IShellLauncher>(new NoLauncher());
                services.AddSingleton<SecondaryWindowPortLoader>((_, _) => null); // cáº§u WPF váº¯ng (báº£n AOT)
            },
        });
        Assert.True(run.WaitForMain(), "cá»­a sá»• chÃ­nh khÃ´ng dá»±ng xong: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "áº£nh Ä‘áº§u khÃ´ng Ä‘Æ°á»£c váº½: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());
        var viewModel = main.ViewModel;

        // 1. Diagnostics: há»™p thoáº¡i Win32 gá»‘c, modal; Ä‘Ã³ng báº±ng nÃºt ÄÃ³ng (IDCANCEL).
        var diagnostics = Open(run, () => viewModel.ShowDiagnostics());
        var dialog = WaitOwned(run, uiThread, "diagnostics");
        Assert.Equal("#32770", ClassName(dialog));
        Assert.False(IsWindowEnabled(main.Hwnd), "modal pháº£i vÃ´ hiá»‡u hoÃ¡ cá»­a sá»• chá»§");
        Assert.Contains(Children(dialog), IsPushButton);
        run.OnUi(() => User32.SendMessage(dialog, WindowMessages.WmCommand, WindowMessages.IdCancel, 0));
        Finished(diagnostics, "Diagnostics khÃ´ng Ä‘Ã³ng");
        WaitNoOwned(run, uiThread, "diagnostics closed");
        Assert.True(IsWindowEnabled(main.Hwnd));

        // 2. Há»™p zoom: Huá»· -> null, rá»“i OK 150.
        var zoomCancelled = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        PostClose(WaitOwned(run, uiThread, "zoom prompt"));
        Finished(zoomCancelled, "há»™p zoom khÃ´ng Ä‘Ã³ng");
        WaitNoOwned(run, uiThread, "zoom prompt closed");

        var zoomOk = Open(run, () => _ = main.OpenClickZoomCustomDialogAsync());
        var zoom = WaitOwned(run, uiThread, "zoom prompt again");
        run.OnUi(() =>
        {
            Assert.True(User32.SetWindowText(User32.GetDlgItem(zoom, 101), "150"));
            Assert.True(User32.PostMessage(zoom, WindowMessages.WmCommand, WindowMessages.IdOk, 0));
        });
        Finished(zoomOk, "há»™p zoom khÃ´ng Ä‘Ã³ng sau OK");
        WaitNoOwned(run, uiThread, "zoom prompt closed after OK");

        // 3. ÄÃ³ng SHELL khi má»™t há»™p thoáº¡i Win32 cÃ²n má»Ÿ: thoÃ¡t mÃ£ 0, há»™p modal tráº£ vá» (khÃ´ng treo vÃ²ng lá»“ng).
        var modal = Open(run, () => viewModel.ShowDiagnostics());
        WaitOwned(run, uiThread, "diagnostics before shutdown");

        run.RequestClose();

        Assert.True(run.WaitForExit(Bound), "shell khÃ´ng thoÃ¡t khi há»™p thoáº¡i Win32 cÃ²n má»Ÿ: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
        Finished(modal, "há»™p modal pháº£i tráº£ vá» khi shell thoÃ¡t");
    }

    private static void Finished(Task task, string what) => Assert.True(task.Wait(Bound), what);

    private static Task Open(ShellRun run, Action action) => run.Main!.Dispatcher.InvokeAsync(action);

    private static nint WaitOwned(ShellRun run, uint uiThread, string what)
    {
        nint found = 0;
        Assert.True(SpinWait.SpinUntil(() => (found = OwnedWindows(uiThread, run.Main!.Hwnd).FirstOrDefault()) != 0, Bound),
            $"há»™p thoáº¡i '{what}' khÃ´ng hiá»‡n: " + run.Diagnostics);
        return found;
    }

    private static void WaitNoOwned(ShellRun run, uint uiThread, string what)
        => Assert.True(SpinWait.SpinUntil(() => OwnedWindows(uiThread, run.Main!.Hwnd).Count == 0, Bound), $"há»™p thoáº¡i cÃ²n má»Ÿ: {what}");

    private static void PostClose(nint hwnd) => Assert.True(User32.PostMessage(hwnd, WindowMessages.WmClose, 0, 0));

    private static string ClassName(nint hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static List<nint> Children(nint parent)
    {
        var found = new List<nint>();
        EnumChildWindows(parent, (hwnd, _) =>
        {
            found.Add(hwnd);
            return true;
        }, 0);
        return found;
    }

    private static bool IsPushButton(nint hwnd) => ClassName(hwnd) == "Button";

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
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, char[] name, int maxCount);
}
