using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// FEAT-RENAME (PR3): "Rename..." end to end in the REAL shell (ShellStartup, real HWND, WARP, hidden desktop, a temp folder of the
/// test's own): F2 and the menu item open the Win32 name box (modal, owned by the shell window), the name is typed into its edit
/// control and confirmed with its OK button through window messages (no SendInput, no SetCursorPos, no SetForegroundWindow), and the
/// real file on disk is renamed, the catalog / presenter / status bar follow, and Undo puts the old name back. An invalid name keeps
/// the box open with the OK button disabled; Cancel changes nothing.
/// </summary>
[Collection("ShellStartup")]
[Trait("Category", "UI")]
[Trait("Category", "Integration")]
public sealed class ShellRenameEndToEndTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private const int EditId = 101;
    private const int ErrorId = 103;
    private const int IdOk = 1;
    private const int IdCancel = 2;

    [Fact]
    public void F2AndTheMenuItem_RenameTheRealFile_TheViewFollows_UndoRestores_InvalidAndCancelChangeNothing()
    {
        var root = ShellRun.NewRoot("rename");
        var folder = Path.Combine(root, "photos");
        var a = TestJpeg.WriteTo(Path.Combine(folder, "a.jpg"));
        var b = TestJpeg.WriteTo(Path.Combine(folder, "b.jpg"));
        using var run = ShellRun.Start(root, [a]);
        Assert.True(run.WaitForMain(), "main window not built: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.Main!.FirstImagePresented, Bound), "first image not painted: " + run.Diagnostics);
        var main = run.Main!;
        var uiThread = run.OnUi(() => GetCurrentThreadId());
        var viewModel = main.ViewModel;
        Assert.Equal(a, run.OnUi(() => viewModel.Catalog.Current?.Path));

        // 1. F2 opens the box: name WITHOUT extension, whole name selected, the extension fixed beside it.
        Assert.True(User32.PostMessage(main.Hwnd, WindowMessages.WmKeyDown, KeyIdMapping.ToVirtualKey(KeyId.F2), 1));
        var dialog = WaitOwned(run, uiThread, "rename box (F2)");
        Assert.False(IsWindowEnabled(main.Hwnd), "the box is modal: the shell window is disabled");
        Assert.Equal("a", run.OnUi(() => TextOf(User32.GetDlgItem(dialog, EditId))));
        var selection = run.OnUi(() => (long)User32.SendMessage(User32.GetDlgItem(dialog, EditId), 0x00B0 /* EM_GETSEL */, 0, 0));
        Assert.Equal((0, 1), ((int)(selection & 0xFFFF), (int)((selection >> 16) & 0xFFFF)));

        // 2. An invalid name is refused in place: error shown, OK disabled, the file untouched, the box stays open.
        run.OnUi(() => Assert.True(User32.SetWindowText(User32.GetDlgItem(dialog, EditId), "a?b")));
        Assert.False(IsWindowEnabled(run.OnUi(() => User32.GetDlgItem(dialog, IdOk))), "OK must be disabled for an invalid name");
        Assert.Equal(Tr.DialogRenameErrorInvalidCharacter, run.OnUi(() => TextOf(User32.GetDlgItem(dialog, ErrorId))));
        Assert.True(File.Exists(a));

        // 3. A valid name + OK: the real file is renamed, in place, and the view follows.
        run.OnUi(() => Assert.True(User32.SetWindowText(User32.GetDlgItem(dialog, EditId), "holiday 01")));
        Assert.True(IsWindowEnabled(run.OnUi(() => User32.GetDlgItem(dialog, IdOk))));
        run.OnUi(() => User32.SendMessage(dialog, WindowMessages.WmCommand, IdOk, 0));
        WaitNoOwned(run, uiThread, "rename box closed after OK");
        var renamed = Path.Combine(folder, "holiday 01.jpg");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(renamed), Bound), "the file was not renamed: " + run.Diagnostics);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => viewModel.Catalog.Current?.Path) == renamed, Bound),
            "the catalog still lists the old name: " + run.Diagnostics);
        Assert.Equal(0, run.OnUi(() => viewModel.Catalog.CurrentIndex)); // in place, same index
        Assert.Equal("holiday 01.jpg", run.OnUi(() => viewModel.Presenter.CurrentPhotoInfo?.FileName));
        Assert.Contains("holiday 01.jpg", run.OnUi(() => viewModel.StatusText), StringComparison.Ordinal);
        Assert.True(IsWindowEnabled(main.Hwnd));

        // 4. Undo puts the old name back.
        run.OnUi(() => _ = viewModel.UndoAsync());
        Assert.True(SpinWait.SpinUntil(() => File.Exists(a) && !File.Exists(renamed), Bound), "Undo did not restore the name: " + run.Diagnostics);
        Assert.True(SpinWait.SpinUntil(() => run.OnUi(() => viewModel.Catalog.Current?.Path) == a, Bound));

        // 5. The menu item (controller dispatch) opens the same box; Cancel changes nothing.
        var viaMenu = run.Main!.Dispatcher.InvokeAsync(() => main.ContextMenu.ExecuteAsync(nameof(ContextMenuItemId.RenameFile)));
        var second = WaitOwned(run, uiThread, "rename box (menu)");
        Assert.Equal("a", run.OnUi(() => TextOf(User32.GetDlgItem(second, EditId))));
        run.OnUi(() => Assert.True(User32.SetWindowText(User32.GetDlgItem(second, EditId), "never")));
        run.OnUi(() => User32.SendMessage(second, WindowMessages.WmCommand, IdCancel, 0));
        Assert.True(SpinWait.SpinUntil(() => viaMenu.IsCompleted, Bound), "the menu command did not finish after Cancel");
        WaitNoOwned(run, uiThread, "rename box closed after Cancel");
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(Path.Combine(folder, "never.jpg")));
        Assert.Equal(a, run.OnUi(() => viewModel.Catalog.Current?.Path));

        run.RequestClose();
        Assert.True(run.WaitForExit(Bound), "the shell did not exit: " + run.Diagnostics);
        Assert.Null(run.Error);
        Assert.Equal(0, run.ExitCode);
    }

    private static nint WaitOwned(ShellRun run, uint uiThread, string what)
    {
        nint found = 0;
        Assert.True(SpinWait.SpinUntil(() => (found = OwnedWindows(uiThread, run.Main!.Hwnd).FirstOrDefault()) != 0, Bound),
            $"dialog '{what}' did not appear: " + run.Diagnostics);
        return found;
    }

    private static void WaitNoOwned(ShellRun run, uint uiThread, string what)
        => Assert.True(SpinWait.SpinUntil(() => OwnedWindows(uiThread, run.Main!.Hwnd).Count == 0, Bound), $"dialog still open: {what}");

    private static string TextOf(nint hwnd)
    {
        var buffer = new char[512];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, char[] text, int maxCount);
}
