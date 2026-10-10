using System.ComponentModel;
using System.Runtime.InteropServices;
using PhotoReview.App.Input;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Hosting;

/// <summary>Tuỳ chọn tạo <see cref="ShellWindow"/>; kích thước theo DIP (nhân DPI của màn hình lúc tạo).</summary>
internal sealed record ShellWindowOptions(
    string Title = "PhotoReview",
    double WidthDip = 1024,
    double HeightDip = 720,
    double MinWidthDip = 320,
    double MinHeightDip = 240);

/// <summary>
/// C-15 trên Win32 (NO-WPF-EXEC-PLAN mục 5, thẻ WP-14): cửa sổ top-level PerMonitorV2, WndProc
/// <see cref="UnmanagedCallersOnly"/> qua <see cref="WndProcThunk"/>. Chỉ dùng trên UI thread tạo nó.
/// <list type="bullet">
/// <item>DPI: đọc ở WM_NCCREATE; WM_DPICHANGED áp rect gợi ý (SetWindowPos) RỒI mới phát <see cref="DpiChanged"/>.</item>
/// <item>WM_GETMINMAXINFO: kích thước kéo tối thiểu = Min*Dip x DPI.</item>
/// <item>WM_CLOSE (nút X, Alt+F4, <see cref="Close"/>): phát <see cref="Closing"/>; Cancel = giữ cửa sổ. WM_DESTROY phát
/// <see cref="Closed"/>; GCHandle giải phóng ở WM_NCDESTROY.</item>
/// <item>Handler (<see cref="AddMessageHandler"/>) xem message theo thứ tự đăng ký, SAU khi cửa sổ cập nhật trạng thái của
/// WM_SIZE/WM_DPICHANGED/WM_ACTIVATE và TRƯỚC xử lý mặc định; vòng đời (NCCREATE/CREATE/DESTROY/NCDESTROY) không qua handler.</item>
/// <item><see cref="Invalidate"/> gộp: đăng ký một lần vào <see cref="IFrameClock.Frame"/> và phát <see cref="Render"/> ở khung kế.</item>
/// </list>
/// </summary>
internal sealed unsafe class ShellWindow : IShellWindow, IDisposable
{
    private const string ClassName = "PhotoReview.Shell.Window";
    private const uint DefaultDpi = 96;

    private static readonly object ClassGate = new();
    private static bool classRegistered;

    private readonly int _threadId;
    private readonly IFrameClock _frameClock;
    private readonly ShellWindowOptions _options;
    private readonly EventHandler<FrameTickEventArgs> _onFrame;
    private IWindowMessageHandler[] _handlers = [];
    private GCHandle _self;
    private nint _hwnd;
    private uint _dpi = DefaultDpi;
    private int _clientWidthPx;
    private int _clientHeightPx;
    private SizeD _lastRaisedClientSize;
    private ShellCursor _cursor = ShellCursor.Arrow;
    private bool _created;
    private bool _destroyed;
    private bool _surfaceReady = true;
    private bool _renderRequested;

    public ShellWindow(ShellWindowOptions options, IFrameClock frameClock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _frameClock = frameClock ?? throw new ArgumentNullException(nameof(frameClock));
        _threadId = Environment.CurrentManagedThreadId;
        _onFrame = OnFrame;
        EnsureClassRegistered();

        _self = GCHandle.Alloc(this);
        var hwnd = PendingUser32.CreateWindowEx(0, ClassName, options.Title, PendingUser32.WsOverlappedWindow,
            PendingUser32.CwUseDefault, PendingUser32.CwUseDefault, PendingUser32.CwUseDefault, PendingUser32.CwUseDefault,
            0, 0, PendingKernel32.GetModuleHandle(0), GCHandle.ToIntPtr(_self));
        if (hwnd == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            FreeSelf();
            WndProcThunk.RethrowPending();
            throw new Win32Exception(error, "CreateWindowEx (shell window) failed");
        }

        WndProcThunk.RethrowPending();
        ApplyInitialSize();
        _lastRaisedClientSize = ClientSizeDip;
    }

    public nint Hwnd => _hwnd;

    public double DpiScale => _dpi / (double)DefaultDpi;

    public SizeD ClientSizeDip => new(_clientWidthPx / DpiScale, _clientHeightPx / DpiScale);

    /// <summary>Kích thước vùng client (pixel thiết bị) - cho renderer.</summary>
    internal (int Width, int Height) ClientSizePixels => (_clientWidthPx, _clientHeightPx);

    public bool IsActive { get; private set; }

    public bool IsLoaded => _created && !_destroyed && _surfaceReady;

    public bool HasPointerCapture => _hwnd != 0 && PendingUser32.GetCapture() == _hwnd;

    internal bool IsDestroyed => _destroyed;

    internal ShellCursor Cursor => _cursor;

    /// <summary>Số lần đã phát <see cref="Render"/> (chẩn đoán/test).</summary>
    internal long RenderCount { get; private set; }

    public event EventHandler? ClientSizeChanged;

    public event EventHandler? DpiChanged;

    public event EventHandler? Activated;

    public event EventHandler? Deactivated;

    public event EventHandler<CancelEventArgs>? Closing;

    public event EventHandler? Closed;

    /// <summary>Khung đã yêu cầu bằng <see cref="Invalidate"/> tới: vẽ ở đây (WP-15/WP-21 nối renderer).</summary>
    internal event EventHandler<FrameTickEventArgs>? Render;

    public void AddMessageHandler(IWindowMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        VerifyAccess();
        _handlers = [.. _handlers, handler];
    }

    public void Invalidate()
    {
        VerifyAccess();
        if (_renderRequested || _destroyed)
        {
            return;
        }

        _renderRequested = true;
        _frameClock.Frame += _onFrame;
    }

    public void SetTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        VerifyAccess();
        if (!_destroyed && !PendingUser32.SetWindowText(_hwnd, title))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowText failed");
        }
    }

    public void SetCursor(ShellCursor cursor)
    {
        VerifyAccess();
        if (!Enum.IsDefined(cursor))
        {
            throw new ArgumentOutOfRangeException(nameof(cursor), cursor, "Unknown ShellCursor.");
        }

        _cursor = cursor;
        if (!_destroyed && (HasPointerCapture || IsPointerOverClient()))
        {
            PendingUser32.SetCursor(CursorHandle(cursor));
        }
    }

    public void CapturePointer()
    {
        VerifyAccess();
        if (!_destroyed)
        {
            PendingUser32.SetCapture(_hwnd);
        }
    }

    public void ReleasePointer()
    {
        VerifyAccess();
        if (HasPointerCapture)
        {
            PendingUser32.ReleaseCapture();
        }
    }

    public PointD ScreenToClientDip(PointD screenPixel)
    {
        VerifyAccess();
        var point = new PendingPoint { X = (int)Math.Round(screenPixel.X), Y = (int)Math.Round(screenPixel.Y) };
        if (!_destroyed)
        {
            PendingUser32.ScreenToClient(_hwnd, &point);
        }

        return new PointD(point.X / DpiScale, point.Y / DpiScale);
    }

    /// <summary>Như nút X: WM_CLOSE đồng bộ, <see cref="Closing"/> có thể huỷ.</summary>
    public void Close()
    {
        VerifyAccess();
        if (_destroyed)
        {
            return;
        }

        PendingUser32.SendMessage(_hwnd, PendingUser32.WmClose, 0, 0);
        WndProcThunk.RethrowPending();
    }

    /// <summary>Hiện cửa sổ; <paramref name="activate"/> = false không lấy focus (test, smoke).</summary>
    internal void Show(bool activate)
    {
        VerifyAccess();
        PendingUser32.ShowWindow(_hwnd, activate ? PendingUser32.SwShowNormal : PendingUser32.SwShowNoActivate);
        WndProcThunk.RethrowPending();
    }

    /// <summary>WP-15/WP-20: <see cref="IsLoaded"/> chỉ true khi surface vẽ đã sẵn sàng.</summary>
    internal void SetSurfaceReady(bool ready)
    {
        VerifyAccess();
        _surfaceReady = ready;
    }

    /// <summary>Huỷ cửa sổ không qua <see cref="Closing"/> (chủ sở hữu dọn). Gọi trên UI thread.</summary>
    public void Dispose()
    {
        if (_hwnd != 0 && !_destroyed)
        {
            VerifyAccess();
            PendingUser32.DestroyWindow(_hwnd);
            WndProcThunk.RethrowPending();
        }

        FreeSelf();
    }

    internal nint ProcessMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case PendingUser32.WmNcCreate:
                _hwnd = hwnd;
                var dpi = PendingUser32.GetDpiForWindow(hwnd);
                _dpi = dpi == 0 ? DefaultDpi : dpi;
                return PendingUser32.DefWindowProc(hwnd, message, wParam, lParam);
            case PendingUser32.WmCreate:
                _created = true;
                return 0;
            case PendingUser32.WmDestroy:
                OnDestroy();
                return 0;
            case PendingUser32.WmNcDestroy:
                return PendingUser32.DefWindowProc(hwnd, message, wParam, lParam);
            case PendingUser32.WmSize:
                UpdateClientSize(LowWord(lParam), HighWord(lParam));
                break;
            case PendingUser32.WmDpiChanged:
                ApplyDpiChange(LowWord(wParam), (PendingRect*)lParam);
                break;
            case PendingUser32.WmActivate:
                UpdateActive(LowWord(wParam) != PendingUser32.WaInactive);
                break;
        }

        foreach (var handler in _handlers)
        {
            if (handler.TryHandle(new WindowMessage(hwnd, message, wParam, lParam), out var handled))
            {
                return handled;
            }
        }

        switch (message)
        {
            case PendingUser32.WmSize:
                Invalidate();
                return 0;
            case PendingUser32.WmDpiChanged:
                return 0;
            case PendingUser32.WmGetMinMaxInfo:
                ApplyMinTrackSize((PendingMinMaxInfo*)lParam);
                return 0;
            case PendingUser32.WmClose:
                RaiseClosing();
                return 0;
            case PendingUser32.WmSetCursor when LowWord(lParam) == PendingUser32.HtClient:
                PendingUser32.SetCursor(CursorHandle(_cursor));
                return 1;
            case PendingUser32.WmEraseBkgnd:
                return 1; // renderer phủ toàn bộ client: không xoá nền (không nháy trắng)
            case PendingUser32.WmPaint:
                PendingUser32.ValidateRect(hwnd, null);
                Invalidate();
                return 0;
            default:
                return PendingUser32.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    /// <summary>WM_NCDESTROY đã xong: cửa sổ native không còn, giải phóng GCHandle.</summary>
    internal void OnNativeDestroyed() => FreeSelf();

    private void ApplyInitialSize()
    {
        var rect = new PendingRect(0, 0, ToPixels(_options.WidthDip), ToPixels(_options.HeightDip));
        if (PendingUser32.AdjustWindowRectExForDpi(&rect, PendingUser32.WsOverlappedWindow, false, 0, _dpi))
        {
            PendingUser32.SetWindowPos(_hwnd, 0, 0, 0, rect.Width, rect.Height,
                PendingUser32.SwpNoMove | PendingUser32.SwpNoZOrder | PendingUser32.SwpNoActivate);
            WndProcThunk.RethrowPending();
        }

        var client = default(PendingRect);
        if (PendingUser32.GetClientRect(_hwnd, &client))
        {
            _clientWidthPx = client.Width;
            _clientHeightPx = client.Height;
        }
    }

    private void UpdateClientSize(int widthPx, int heightPx)
    {
        _clientWidthPx = widthPx;
        _clientHeightPx = heightPx;
        RaiseClientSizeChangedIfNeeded();
    }

    private void RaiseClientSizeChangedIfNeeded()
    {
        var size = ClientSizeDip;
        if (size == _lastRaisedClientSize)
        {
            return;
        }

        _lastRaisedClientSize = size;
        ClientSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDpiChange(int newDpi, PendingRect* suggested)
    {
        if (newDpi > 0)
        {
            _dpi = (uint)newDpi;
        }

        if (suggested is not null)
        {
            var rect = *suggested;
            PendingUser32.SetWindowPos(_hwnd, 0, rect.Left, rect.Top, rect.Width, rect.Height,
                PendingUser32.SwpNoZOrder | PendingUser32.SwpNoActivate);
        }

        // WM_SIZE đã phát ClientSizeChanged nếu số pixel đổi; cùng số pixel nhưng DPI khác thì DIP vẫn đổi.
        RaiseClientSizeChangedIfNeeded();
        DpiChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void UpdateActive(bool active)
    {
        if (active == IsActive)
        {
            return;
        }

        IsActive = active;
        (active ? Activated : Deactivated)?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyMinTrackSize(PendingMinMaxInfo* info)
    {
        if (info is null)
        {
            return;
        }

        var frame = new PendingRect(0, 0, ToPixels(_options.MinWidthDip), ToPixels(_options.MinHeightDip));
        if (PendingUser32.AdjustWindowRectExForDpi(&frame, PendingUser32.WsOverlappedWindow, false, 0, _dpi))
        {
            info->MinTrackSize.X = frame.Width;
            info->MinTrackSize.Y = frame.Height;
        }
    }

    private void RaiseClosing()
    {
        var args = new CancelEventArgs();
        Closing?.Invoke(this, args);
        if (!args.Cancel)
        {
            PendingUser32.DestroyWindow(_hwnd);
        }
    }

    private void OnDestroy()
    {
        _destroyed = true;
        IsActive = false;
        if (PendingUser32.GetCapture() == _hwnd)
        {
            PendingUser32.ReleaseCapture();
        }

        if (_renderRequested)
        {
            _renderRequested = false;
            _frameClock.Frame -= _onFrame;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFrame(object? sender, FrameTickEventArgs e)
    {
        _frameClock.Frame -= _onFrame;
        _renderRequested = false;
        if (_destroyed)
        {
            return;
        }

        RenderCount++;
        Render?.Invoke(this, e);
    }

    private bool IsPointerOverClient()
    {
        PendingPoint point;
        return PendingUser32.GetCursorPos(&point) && PendingUser32.WindowFromPoint(point) == _hwnd;
    }

    private int ToPixels(double dip) => (int)Math.Round(dip * DpiScale);

    private void FreeSelf()
    {
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("ShellWindow must be used on the UI thread that created it.");
        }
    }

    private static int LowWord(nint value) => (int)((ulong)value & 0xFFFF);

    private static int HighWord(nint value) => (int)(((ulong)value >> 16) & 0xFFFF);

    private static nint CursorHandle(ShellCursor cursor) => cursor switch
    {
        ShellCursor.SizeAll => SystemCursors.SizeAll,
        ShellCursor.Wait => SystemCursors.Wait,
        _ => SystemCursors.Arrow,
    };

    private static void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (classRegistered)
            {
                return;
            }

            fixed (char* className = ClassName)
            {
                var windowClass = new PendingWndClassEx
                {
                    Size = (uint)sizeof(PendingWndClassEx),
                    WndProc = WndProcThunk.ShellWindowProc,
                    Instance = PendingKernel32.GetModuleHandle(0),
                    ClassName = className,
                };
                if (PendingUser32.RegisterClassEx(&windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx (shell window) failed");
                }
            }

            classRegistered = true;
        }
    }

    /// <summary>Con trỏ hệ thống dùng chung (LoadCursor không cấp handle mới, không cần huỷ).</summary>
    private static class SystemCursors
    {
        public static readonly nint Arrow = PendingUser32.LoadCursor(0, PendingUser32.IdcArrow);
        public static readonly nint SizeAll = PendingUser32.LoadCursor(0, PendingUser32.IdcSizeAll);
        public static readonly nint Wait = PendingUser32.LoadCursor(0, PendingUser32.IdcWait);
    }
}
