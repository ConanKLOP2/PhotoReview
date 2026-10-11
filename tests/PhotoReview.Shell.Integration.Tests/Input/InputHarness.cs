using System.ComponentModel;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Tests.Input;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;
using PhotoReview.Shell.Win32.Input;
using PhotoReview.Shell.Win32.Viewing;

namespace PhotoReview.Shell.Integration.Tests.Input;

/// <summary>
/// <see cref="IShellWindow"/> mà kích thước client và DPI do test quyết định (không phụ thuộc màn hình runner CI 1024x768 và
/// khung vô hình của GetWindowRect), còn HWND, thông điệp, capture, con trỏ là của <see cref="ShellWindow"/> THẬT.
/// Toạ độ "màn hình" của wrapper trùng toạ độ client (một đơn vị = một DIP = một pixel), nên lParam của wheel mang đúng
/// toạ độ client mà test muốn.
/// </summary>
internal sealed class SizedShellWindow(ShellWindow real, double width, double height) : IShellWindow
{
    public nint Hwnd => real.Hwnd;

    public double DpiScale => 1.0;

    public SizeD ClientSizeDip { get; private set; } = new(width, height);

    public bool IsActive => real.IsActive;

    public bool IsLoaded => real.IsLoaded;

    public bool HasPointerCapture => real.HasPointerCapture;

    public ShellCursor Cursor => real.Cursor;

    public event EventHandler? ClientSizeChanged;

    public event EventHandler? DpiChanged
    {
        add => real.DpiChanged += value;
        remove => real.DpiChanged -= value;
    }

    public event EventHandler? Activated
    {
        add => real.Activated += value;
        remove => real.Activated -= value;
    }

    public event EventHandler? Deactivated
    {
        add => real.Deactivated += value;
        remove => real.Deactivated -= value;
    }

    public event EventHandler<CancelEventArgs>? Closing
    {
        add => real.Closing += value;
        remove => real.Closing -= value;
    }

    public event EventHandler? Closed
    {
        add => real.Closed += value;
        remove => real.Closed -= value;
    }

    public void Resize(double newWidth, double newHeight)
    {
        ClientSizeDip = new SizeD(newWidth, newHeight);
        ClientSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddMessageHandler(IWindowMessageHandler handler) => real.AddMessageHandler(handler);

    public void Invalidate() => real.Invalidate();

    public void SetTitle(string title) => real.SetTitle(title);

    public void SetCursor(ShellCursor cursor) => real.SetCursor(cursor);

    public void CapturePointer() => real.CapturePointer();

    public void ReleasePointer() => real.ReleasePointer();

    public PointD ScreenToClientDip(PointD screenPixel) => screenPixel;

    public void Close() => real.Close();
}

/// <summary>
/// WP-22: dựng tối thiểu "cửa sổ + viewport + nhập liệu" như <c>ShellMainWindow</c> nối (ViewerState, ViewportController,
/// <see cref="ShellInput"/>, handler thông điệp THẬT trên HWND thật) nhưng không cần MainViewModel/D2D, để replay golden bằng
/// thông điệp Win32 thật. Dấu thời gian, phím chỉnh, thiết bị wheel, con trỏ do test đặt (SendMessage trên desktop ẩn không
/// phản ánh được chúng). Chỉ dùng trên UI thread của <see cref="UiThread"/>.
/// </summary>
internal sealed class InputHarness : IDisposable
{
    private readonly UiThread _ui;
    private readonly ShellWindow _real;
    private (double Width, double Height) _bitmap;
    private bool _disposed;

    public InputHarness(UiThread ui, double clientWidth, double clientHeight, int imageWidth, int imageHeight, double dpiScale = 1.0,
        AppSettings? settings = null)
    {
        _ui = ui;
        Settings = settings ?? new AppSettings();
        Clock = new ManualFrameClock();
        _real = new ShellWindow(new ShellWindowOptions(), Clock);
        Window = new SizedShellWindow(_real, clientWidth, clientHeight);
        Host = new RecordingInputHost();
        Viewer = new ViewerState { DpiScale = dpiScale, ZoomStep = Settings.KeyboardZoomStepPercent / 100.0 };
        Viewer.SetSourceSize(imageWidth, imageHeight, newImage: true);
        _bitmap = (imageWidth, imageHeight);

        Viewport = new ViewportController(Window, ui.Dispatcher, Clock, ContentFromViewer, UpdateFitSize,
            new ViewportControllerOptions
            {
                DragMetricsPixels = () => (4, 4),
                CursorScreenPixel = () => ScriptedMouse,
                Timestamp = () => Timestamp,
            });
        Viewer.PropertyChanged += (_, _) => Viewport.InvalidateLayout(); // thay binding XAML, như ShellMainWindow.OnViewerChanged
        Environment = new ShellInputEnvironment
        {
            Modifiers = () => Modifiers,
            MessageTime = () => MessageTime,
            TickCount = () => MessageTime,
            WheelDevice = () => WheelDevice,
        };
        Host.ZoomActualSizeAction = Viewer.ZoomToActualSize;
        Input = new ShellInput(Window, Viewport, Viewer, () => Settings, Host, Environment);
        Viewport.UpdateLayout();
    }

    public AppSettings Settings { get; }

    public ManualFrameClock Clock { get; }

    public ShellWindow Real => _real;

    public SizedShellWindow Window { get; }

    public RecordingInputHost Host { get; }

    public ViewerState Viewer { get; }

    public ViewportController Viewport { get; }

    public ShellInput Input { get; }

    public ShellInputEnvironment Environment { get; }

    public KeyModifiers Modifiers { get; set; }

    public int MessageTime { get; set; }

    public WheelDeviceHint WheelDevice { get; set; }

    public long Timestamp { get; set; }

    public PointD? ScriptedMouse { get; set; }

    public nint Hwnd => _real.Hwnd;

    public void SetBitmap(double width, double height) => _bitmap = (width, height);

    private ViewportContent ContentFromViewer() => new(Viewer.Stretch, Viewer.ImageWidth, Viewer.ImageHeight, Viewer.MaxImageWidth,
        Viewer.MaxImageHeight, _bitmap.Width, _bitmap.Height, Viewer.IsFit);

    private void UpdateFitSize()
    {
        var (width, height) = Viewport.ViewportSize;
        Viewer.UpdateViewport(width, height);
    }

    // ---- gửi thông điệp THẬT vào HWND ----

    public static nint PackPoint(int x, int y) => (nint)(((uint)(ushort)y << 16) | (ushort)x);

    public nint Send(uint message, nint wParam, nint lParam) => PendingUser32.SendMessage(_real.Hwnd, message, wParam, lParam);

    public void SendMouse(uint message, int x, int y, int keyFlags = 0) => Send(message, keyFlags, PackPoint(x, y));

    public void SendWheel(uint message, int delta, int x, int y, bool ctrl = false)
    {
        var wParam = (nint)(((uint)(ushort)(short)delta << 16) | (uint)(ctrl ? InputNative.MkControl : 0));
        Send(message, wParam, PackPoint(x, y));
    }

    public nint SendKeyDown(int virtualKey, bool repeat = false, bool extended = false, bool system = false, int scanCode = 0)
    {
        long lParam = 1 | ((long)scanCode << 16);
        if (repeat)
        {
            lParam |= InputNative.KeyRepeatBit;
        }

        if (extended)
        {
            lParam |= InputNative.KeyExtendedBit;
        }

        return Send(system ? WindowMessages.WmSysKeyDown : WindowMessages.WmKeyDown, virtualKey, (nint)lParam);
    }

    // ---- chờ yên ----

    /// <summary>
    /// Chờ mọi việc đã xếp ở mức cao hơn Background (layout ở Render, continuation của thao tác async) chạy xong. Mỗi bước nhường
    /// một lượt Background: nó chỉ chạy khi các hàng đợi cao hơn rỗng nên là rào chắn tất định, không có độ trễ cố định.
    /// </summary>
    public async Task SettleAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await _ui.Dispatcher.InvokeAsync(static () => { }, UiPriority.Background);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Input.Dispose();
        Viewport.Dispose();
        _real.Dispose();
    }
}
