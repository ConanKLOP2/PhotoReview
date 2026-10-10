using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>
/// Điểm vào native của mọi WndProc trong shell (<see cref="UnmanagedCallersOnly"/>, AOT-ready, không delegate marshalling).
/// Ngoại lệ không được thoát qua khung native (tiến trình sẽ chết không có stack): thunk bắt lại, giữ trên thread
/// (<see cref="RethrowPending"/>) và <see cref="MessageLoop"/> / người gọi API đồng bộ (CreateWindowEx, SendMessage) ném lại
/// ngay sau khi lời gọi native trả về - giống WPF/WinForms đẩy ngoại lệ WndProc về vòng lặp.
/// </summary>
internal static unsafe class WndProcThunk
{
    [ThreadStatic]
    private static ExceptionDispatchInfo? pendingException;

    /// <summary>WndProc của <see cref="ShellWindow"/>; ShellWindow tìm qua GCHandle trong GWLP_USERDATA (gắn ở WM_NCCREATE).</summary>
    internal static delegate* unmanaged<nint, uint, nint, nint, nint> ShellWindowProc => &ShellWindowWndProc;

    /// <summary>WndProc của cửa sổ message-only của <see cref="Win32UiDispatcher"/> (một dispatcher mỗi thread).</summary>
    internal static delegate* unmanaged<nint, uint, nint, nint, nint> DispatcherProc => &DispatcherWndProc;

    /// <summary>Ném lại (một lần) ngoại lệ đầu tiên một WndProc trên thread này đã bắt.</summary>
    internal static void RethrowPending()
    {
        var pending = pendingException;
        if (pending is null)
        {
            return;
        }

        pendingException = null;
        pending.Throw();
    }

    private static void Capture(Exception exception) => pendingException ??= ExceptionDispatchInfo.Capture(exception);

    [UnmanagedCallersOnly]
    private static nint ShellWindowWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            ShellWindow? window;
            if (message == PendingUser32.WmNcCreate)
            {
                var create = (PendingCreateStruct*)lParam;
                window = GCHandle.FromIntPtr(create->CreateParams).Target as ShellWindow;
                if (window is null)
                {
                    return 0; // huỷ tạo cửa sổ: không có chủ
                }

                PendingUser32.SetWindowLongPtr(hwnd, PendingUser32.GwlpUserData, create->CreateParams);
            }
            else
            {
                var cookie = PendingUser32.GetWindowLongPtr(hwnd, PendingUser32.GwlpUserData);
                window = cookie == 0 ? null : GCHandle.FromIntPtr(cookie).Target as ShellWindow;
            }

            if (window is null)
            {
                return PendingUser32.DefWindowProc(hwnd, message, wParam, lParam);
            }

            var result = window.ProcessMessage(hwnd, message, wParam, lParam);
            if (message == PendingUser32.WmNcDestroy)
            {
                PendingUser32.SetWindowLongPtr(hwnd, PendingUser32.GwlpUserData, 0);
                window.OnNativeDestroyed();
            }

            return result;
        }
        catch (Exception ex)
        {
            // Không xử lý mặc định sau lỗi: DefWindowProc(WM_CLOSE) sẽ huỷ cửa sổ mà Closing chưa kịp quyết; WM_NCCREATE trả 0 = huỷ tạo.
            Capture(ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint DispatcherWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == Win32UiDispatcher.WakeMessage)
            {
                Win32UiDispatcher.FromCurrentThread?.OnWakeMessage();
                return 0;
            }

            return PendingUser32.DefWindowProc(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            Capture(ex);
            return 0;
        }
    }
}
