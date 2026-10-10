using System.ComponentModel;
using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Hosting;

// C-15 (NO-WPF-EXEC-PLAN mục 5): cửa sổ shell. Thực thi ở WP-14.

public enum ShellCursor
{
    Arrow = 0,
    SizeAll = 1,
    Wait = 2,
}

public readonly record struct WindowMessage(nint Hwnd, uint Msg, nint WParam, nint LParam);

/// <summary>Chuỗi handler theo thứ tự đăng ký.</summary>
public interface IWindowMessageHandler
{
    bool TryHandle(in WindowMessage message, out nint result);
}

public interface IShellWindow
{
    nint Hwnd { get; }

    double DpiScale { get; }

    SizeD ClientSizeDip { get; }

    bool IsActive { get; }

    /// <summary>Sau WM_CREATE + surface sẵn sàng, trước WM_DESTROY.</summary>
    bool IsLoaded { get; }

    event EventHandler? ClientSizeChanged;

    /// <summary>WM_DPICHANGED (đã áp rect gợi ý).</summary>
    event EventHandler? DpiChanged;

    event EventHandler? Activated;

    event EventHandler? Deactivated;

    event EventHandler<CancelEventArgs>? Closing;

    event EventHandler? Closed;

    void AddMessageHandler(IWindowMessageHandler handler);

    /// <summary>Yêu cầu vẽ khung kế tiếp (gộp).</summary>
    void Invalidate();

    void SetTitle(string title);

    void SetCursor(ShellCursor cursor);

    void CapturePointer();

    void ReleasePointer();

    bool HasPointerCapture { get; }

    PointD ScreenToClientDip(PointD screenPixel);

    void Close();
}
