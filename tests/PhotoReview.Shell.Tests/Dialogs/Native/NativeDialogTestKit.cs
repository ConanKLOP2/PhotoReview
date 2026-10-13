using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// Bộ dụng cụ cho test hộp thoại Win32 gốc (WP-35 GĐ1): chạy trên một thread STA mới có hàng đợi thông điệp riêng, tạo cửa sổ chủ
/// thật (lớp "Static" popup), mô phỏng người dùng bằng SendMessage/IsDialogMessage đồng bộ trong callback <c>ready</c> (chạy ngay
/// trước vòng lặp modal) nên không có chờ cố định. Lỗi khẳng định trong <c>ready</c> nổi lên qua <c>ShowModal</c>.
/// </summary>
internal static unsafe class NativeDialogTestKit
{
    public const uint VkReturn = 0x0D;
    public const uint VkEscape = 0x1B;
    public const uint VkTab = 0x09;
    public const uint ScClose = 0xF060;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>Chạy <paramref name="body"/> trên thread STA mới (hàng đợi riêng); ném lại ngoại lệ của nó.</summary>
    public static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "native-dialog-test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(Bound))
        {
            throw new TimeoutException("native dialog test thread did not finish in " + Bound);
        }

        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    /// <summary>Cửa sổ chủ popup hiện, 640x480 tại (100, 100); đã bật.</summary>
    public static nint CreateOwner()
    {
        const uint WsPopup = 0x80000000;
        const uint WsVisible = 0x10000000;
        var hwnd = User32.CreateWindowEx(0, "Static", "dialog test owner", WsPopup | WsVisible, 100, 100, 640, 480, 0, 0,
            Kernel32.GetModuleHandle(0), 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException("owner window not created, error " + Marshal.GetLastWin32Error());
        }

        return hwnd;
    }

    public static void DestroyOwner(nint hwnd)
    {
        if (hwnd != 0 && User32.IsWindow(hwnd))
        {
            User32.DestroyWindow(hwnd);
        }
    }

    /// <summary>Hiện hộp thoại trên một cửa sổ chủ mới; trả (kết quả, chủ còn bật sau đó).</summary>
    public static int ShowOnOwner(NativeDialog dialog, Action<NativeDialog, nint> ready)
    {
        var owner = CreateOwner();
        try
        {
            return dialog.ShowModal(owner, d => ready(d, owner));
        }
        finally
        {
            DestroyOwner(owner);
        }
    }

    public static nint Item(NativeDialog dialog, int id) => User32.GetDlgItem(dialog.Hwnd, id);

    public static string TextOf(nint hwnd)
    {
        var length = User32.GetWindowTextLength(hwnd);
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            return new string(p, 0, User32.GetWindowText(hwnd, p, buffer.Length));
        }
    }

    public static string TextOf(NativeDialog dialog, int id) => TextOf(Item(dialog, id));

    public static void SetText(NativeDialog dialog, int id, string text) => User32.SetWindowText(Item(dialog, id), text);

    public static bool IsVisible(NativeDialog dialog, int id) => User32.IsWindowVisible(Item(dialog, id));

    public static void Command(NativeDialog dialog, int id, int notification = 0)
        => User32.SendMessage(dialog.Hwnd, WindowMessages.WmCommand, (nint)((notification << 16) | (id & 0xFFFF)), 0);

    /// <summary>Gõ một phím vào điều khiển như vòng lặp thật: qua <c>IsDialogMessage</c> (Enter -> IDOK, Esc -> IDCANCEL). True = dialog manager nhận.</summary>
    public static bool Key(NativeDialog dialog, nint target, uint virtualKey)
    {
        var message = new Msg { Hwnd = target, Message = WindowMessages.WmKeyDown, WParam = (nint)virtualKey, LParam = 1 };
        return User32.IsDialogMessage(dialog.Hwnd, &message);
    }

    /// <summary>Xử lý hết thông điệp đang chờ trong hàng đợi (lệnh mà dialog manager POST thay vì SEND).</summary>
    public static void Drain(NativeDialog dialog)
    {
        Msg message;
        while (User32.PeekMessage(&message, 0, 0, 0, WindowMessages.PmRemove))
        {
            if (message.Message == WindowMessages.WmQuit)
            {
                User32.PostQuitMessage((int)message.WParam);
                return;
            }

            if (dialog.Hwnd != 0 && User32.IsDialogMessage(dialog.Hwnd, &message))
            {
                continue;
            }

            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }
    }

    public static Rect WindowRect(nint hwnd)
    {
        Rect rect = default;
        User32.GetWindowRect(hwnd, &rect);
        return rect;
    }

    public static Rect ClientRect(nint hwnd)
    {
        Rect rect = default;
        User32.GetClientRect(hwnd, &rect);
        return rect;
    }

    /// <summary>Hình chữ nhật của điều khiển con theo toạ độ client của hộp thoại.</summary>
    public static Rect ChildRect(NativeDialog dialog, int id)
    {
        var child = Item(dialog, id);
        var rect = WindowRect(child);
        var topLeft = new Point { X = rect.Left, Y = rect.Top };
        var bottomRight = new Point { X = rect.Right, Y = rect.Bottom };
        User32.ScreenToClient(dialog.Hwnd, &topLeft);
        User32.ScreenToClient(dialog.Hwnd, &bottomRight);
        return new Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
    }

    public static int Width(Rect rect) => rect.Right - rect.Left;

    public static int Height(Rect rect) => rect.Bottom - rect.Top;

    /// <summary>lfHeight của HFONT.</summary>
    public static int FontHeight(nint font)
    {
        byte* buffer = stackalloc byte[DialogConstants.LogFontSize];
        _ = Gdi32.GetObject(font, DialogConstants.LogFontSize, buffer);
        return *(int*)buffer;
    }

    /// <summary>Chiều cao chữ nhiều dòng cần cho text của điều khiển trong bề rộng hiện có (đo độc lập với khung).</summary>
    public static int NeededTextHeight(nint control)
    {
        var text = TextOf(control);
        var width = Width(WindowRect(control));
        var font = User32.SendMessage(control, DialogConstants.WmGetFont, 0, 0);
        var hdc = User32.GetDC(control);
        var old = Gdi32.SelectObject(hdc, font);
        var rect = new Rect { Right = Math.Max(1, width) };
        fixed (char* p = text)
        {
            _ = User32.DrawText(hdc, p, text.Length, &rect, DialogConstants.DtCalcRect | DialogConstants.DtWordBreak | DialogConstants.DtNoPrefix);
        }

        Gdi32.SelectObject(hdc, old);
        _ = User32.ReleaseDC(control, hdc);
        return rect.Bottom - rect.Top;
    }

    public static uint GdiObjects() => User32.GetGuiResources(Kernel32.GetCurrentProcess(), DialogConstants.GrGdiObjects);

    public static uint UserObjects() => User32.GetGuiResources(Kernel32.GetCurrentProcess(), DialogConstants.GrUserObjects);

    /// <summary>Có WM_QUIT đang chờ trong hàng đợi (không lấy ra)?</summary>
    public static bool HasQuitPending(out int exitCode)
    {
        Msg message;
        if (User32.PeekMessage(&message, 0, WindowMessages.WmQuit, WindowMessages.WmQuit, WindowMessages.PmRemove))
        {
            exitCode = (int)message.WParam;
            return true;
        }

        exitCode = 0;
        return false;
    }
}

/// <summary>Bản ghi của <see cref="INativeDialogChrome"/>: cho test kiểm "cờ dark" mà không cần nhìn bằng mắt.</summary>
internal sealed class RecordingChrome : INativeDialogChrome
{
    public List<(nint Dialog, DialogChromeControl[] Controls)> Calls { get; } = [];

    public void Apply(nint dialog, IReadOnlyList<DialogChromeControl> controls) => Calls.Add((dialog, [.. controls]));
}
