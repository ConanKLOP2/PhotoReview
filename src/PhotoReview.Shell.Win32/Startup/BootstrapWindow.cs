using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// WP-01: cửa sổ Win32 trống tối thiểu để chứng minh exe chạy được (message loop, WndProc UnmanagedCallersOnly, manifest).
/// Không phải cửa sổ xem ảnh: WP-14 thay bằng ShellWindow (C-15), WP-20 bằng startup thật. Mã thoát khác 0 cho biết
/// bước nào hỏng để test smoke báo rõ.
/// </summary>
internal static unsafe class BootstrapWindow
{
    internal const int ExitOk = 0;
    internal const int ExitRegisterClassFailed = 2;
    internal const int ExitCreateWindowFailed = 3;
    internal const int ExitNotShown = 4;
    internal const int ExitMessageLoopFailed = 5;

    private const string ClassName = "PhotoReview.Shell.Bootstrap";

    // Tên sản phẩm, không dịch (i18n: chuỗi giao diện thật của shell đi qua Tr.* từ WP-20).
    private const string Title = "PhotoReview";

    [ThreadStatic]
    private static bool sawCreate;

    public static int Run(bool smoke)
    {
        sawCreate = false;
        var instance = Kernel32.GetModuleHandle(0);

        fixed (char* className = ClassName)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                Cursor = User32.LoadCursor(0, User32.IdcArrow),
                Background = Gdi32.GetStockObject(Gdi32.BlackBrush),
                ClassName = className,
            };
            if (User32.RegisterClassEx(&windowClass) == 0)
            {
                return ExitRegisterClassFailed;
            }
        }

        try
        {
            var hwnd = User32.CreateWindowEx(0, ClassName, Title, User32.WsOverlappedWindow,
                User32.CwUseDefault, User32.CwUseDefault, User32.CwUseDefault, User32.CwUseDefault, 0, 0, instance, 0);
            if (hwnd == 0 || !sawCreate)
            {
                return ExitCreateWindowFailed;
            }

            // --smoke: không kích hoạt (không lấy focus của người dùng, không SetForegroundWindow).
            User32.ShowWindow(hwnd, smoke ? User32.SwShowNoActivate : User32.SwShowDefault);
            if (!User32.IsWindowVisible(hwnd))
            {
                User32.DestroyWindow(hwnd);
                return ExitNotShown;
            }

            if (smoke && !User32.PostMessage(hwnd, User32.WmClose, 0, 0))
            {
                User32.DestroyWindow(hwnd);
                return ExitMessageLoopFailed;
            }

            return RunMessageLoop();
        }
        finally
        {
            User32.UnregisterClass(ClassName, instance);
        }
    }

    private static int RunMessageLoop()
    {
        Msg message;
        int result;
        while ((result = User32.GetMessage(&message, 0, 0, 0)) != 0)
        {
            if (result == -1)
            {
                return ExitMessageLoopFailed;
            }

            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }

        // WM_QUIT từ PostQuitMessage(ExitOk) ở WM_DESTROY.
        return (int)message.WParam;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case User32.WmCreate:
                sawCreate = true;
                return 0;
            case User32.WmDestroy:
                User32.PostQuitMessage(ExitOk);
                return 0;
            default:
                return User32.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}
