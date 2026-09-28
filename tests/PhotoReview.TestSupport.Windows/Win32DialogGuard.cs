using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace PhotoReview.TestSupport.Windows;

/// <summary>
/// Thread-scoped guard: install on an STA thread; auto-dismisses real Win32 dialogs opened on THAT thread.
/// </summary>
/// <remarks>
/// A real MessageBox (or common file dialog) on this thread runs a nested Win32 modal loop that nobody clicks in a
/// headless run: the body never finishes, a normal test timeout cannot unwind a nested loop, and the only exit
/// is the blame collector killing testhost after 120 s -- reported as a mysterious "crash". It also pops up on the
/// developer's desktop. This CBT hook sees the dialog before it is painted, records its text, moves it off-screen,
/// answers it with the least destructive button (Cancel, else No, else OK) and closes any WPF window still modal,
/// so the test fails at once with the dialog's text instead.
/// </remarks>
public sealed class Win32DialogGuard : IDisposable
{
    private readonly ConcurrentQueue<string> _dialogs = new();
    private readonly Dispatcher? _dispatcher;
    private readonly HookProc _hookProc; // held so the GC never collects the delegate the native hook calls
    private IntPtr _hook;
    private bool _disposed;

    private Win32DialogGuard()
    {
        _dispatcher = Application.Current is not null ? Dispatcher.CurrentDispatcher : null;
        _hookProc = OnCbt;
        _hook = SetWindowsHookEx(WhCbt, _hookProc, IntPtr.Zero, GetCurrentThreadId());
    }

    /// <summary>Installs the WH_CBT hook for the CALLING thread (must be called on the STA thread that will show dialogs).</summary>
    public static Win32DialogGuard InstallOnCurrentThread() => new();

    /// <summary>True if at least one dialog was intercepted since install / last Clear().</summary>
    public bool HasDialogs => !_dialogs.IsEmpty;

    /// <summary>Snapshot of recorded dialogs, each formatted "\"&lt;caption&gt;\": text".</summary>
    public IReadOnlyList<string> Dialogs => _dialogs.ToArray();

    /// <summary>Forgets recorded dialogs (call at the start of each test body on a long-lived thread).</summary>
    public void Clear() => _dialogs.Clear();

    /// <summary>Throws InvalidOperationException listing every recorded dialog if any; otherwise no-op.</summary>
    public void ThrowIfAny()
    {
        var exception = CreateException();
        if (exception is not null) throw exception;
    }

    /// <summary>Builds the same exception ThrowIfAny would throw, or null if none recorded (for callers that report via a TaskCompletionSource).</summary>
    public InvalidOperationException? CreateException()
    {
        if (_dialogs.IsEmpty) return null;
        return new InvalidOperationException(
            "A real Win32 dialog opened on an STA test thread (auto-dismissed so the run does not hang): "
            + string.Join(" | ", _dialogs)
            + " -- route it through the code's test seam (e.g. SettingsWindow.InvalidSettingsWarning, IDialogService) "
            + "or change the inputs so it never opens.");
    }

    /// <summary>Unhooks (UnhookWindowsHookEx). Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr OnCbt(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcbtActivate && ClassName(wParam) == "#32770")
        {
            _dialogs.Enqueue($"\"{WindowText(wParam)}\": {WindowText(GetDlgItem(wParam, MessageBoxTextId))}");
            SetWindowPos(wParam, IntPtr.Zero, -32000, -32000, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            foreach (var id in new[] { IdCancel, IdNo, IdOk })
            {
                var button = GetDlgItem(wParam, id);
                if (button == IntPtr.Zero) continue;
                PostMessage(wParam, WmCommand, (IntPtr)id, button);
                break;
            }
            // Application.Windows is only reachable from the Application's own thread; a guard on some other STA
            // thread (ad-hoc test threads) just dismisses the box.
            var app = Application.Current;
            if (app is not null && _dispatcher is not null && app.Dispatcher == _dispatcher)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    foreach (var window in app.Windows.OfType<Window>().Where(w => w.IsVisible).ToList())
                        try { window.Close(); } catch (InvalidOperationException) { }
                });
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    private static string WindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buffer = new char[1024];
        var length = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    private const int WhCbt = 5;
    private const int HcbtActivate = 5;
    private const int MessageBoxTextId = 0xFFFF;
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const int IdNo = 7;
    private const uint WmCommand = 0x0111;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
