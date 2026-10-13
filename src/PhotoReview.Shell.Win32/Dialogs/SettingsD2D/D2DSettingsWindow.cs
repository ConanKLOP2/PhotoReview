using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>Tuỳ chọn tạo <see cref="D2DSettingsWindow"/>.</summary>
internal sealed record D2DSettingsWindowOptions(
    bool Dark = true,
    D2DEditMode EditMode = D2DEditMode.SelfDrawn,
    int ClientWidthDip = 640,
    int ClientHeightDip = 560,
    uint? DpiOverride = null,
    bool PreferWarp = false,
    bool Visible = true,
    bool QuitOnClose = false);

/// <summary>
/// SPIKE: cửa sổ top-level chứa MỘT bề mặt Direct2D (<see cref="D2DRenderSurface"/>) và <see cref="D2DSettingsView"/>. Chuyển thông điệp
/// (chuột, phím, ký tự, cuộn, DPI, focus, timer caret) vào view; ở chế độ lai (<see cref="D2DEditMode.HostedWin32"/>) tạo thêm
/// control EDIT/COMBOBOX Win32 thật đặt chồng lên bề mặt tại đúng ô, cắt vùng (SetWindowRgn) theo viewport khi cuộn.
/// Một thread (UI) sở hữu; WndProc <see cref="UnmanagedCallersOnly"/> như <c>ShellWindow</c>.
/// </summary>
internal sealed unsafe class D2DSettingsWindow : IDisposable
{
    private const string ClassName = "PhotoReview.SettingsD2D.Surface";
    private const uint DefaultDpi = 96;
    private const nuint BlinkTimerId = 0x44;
    private const int WmMouseLeave = 0x02A3;
    private const int WmImeStartComposition = 0x010D;
    private const int WmGetObject = 0x003D;

    private static readonly object ClassGate = new();
    private static bool classRegistered;

    private readonly D2DSettingsWindowOptions _options;
    private readonly DWriteTextRenderer _text;
    private readonly Win32Clipboard _clipboard;
    private readonly List<HostedControl> _hosted = [];
    private readonly Dictionary<nint, HostedControl> _hostedByHandle = [];
    private GCHandle _self;
    private nint _hwnd;
    private uint _dpi;
    private int _clientWidth;
    private int _clientHeight;
    private D2DRenderSurface? _surface;
    private nint _font;
    private nint _inputBrush;
    private bool _destroyed;
    private bool _inSizeChange;
    private ExceptionDispatchInfo? _pending;

    public D2DSettingsWindow(D2DSettingsWindowOptions options, Func<string> title, Func<D2DSettingsView, object> buildPage)
    {
        _options = options;
        _text = new DWriteTextRenderer();
        _clipboard = new Win32Clipboard(this);
        View = new D2DSettingsView(_text, _clipboard, options.Dark ? D2DSettingsPalette.Dark : D2DSettingsPalette.Classic, options.EditMode, title);
        Page = buildPage(View);
        EnsureClassRegistered();

        _dpi = options.DpiOverride ?? DefaultDpi;
        _self = GCHandle.Alloc(this);
        var style = WindowMessages.WsOverlappedWindow;
        var rect = new Rect { Left = 0, Top = 0, Right = (int)Math.Round(options.ClientWidthDip * _dpi / 96.0), Bottom = (int)Math.Round(options.ClientHeightDip * _dpi / 96.0) };
        User32.AdjustWindowRectExForDpi(&rect, style, false, 0, _dpi);
        _hwnd = User32.CreateWindowEx(0, ClassName, "SettingsD2D", style, 100, 100, rect.Right - rect.Left, rect.Bottom - rect.Top, 0, 0,
            Kernel32.GetModuleHandle(0), GCHandle.ToIntPtr(_self));
        if (_hwnd == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _self.Free();
            throw new Win32Exception(error, "CreateWindowEx (SettingsD2D) failed");
        }

        RethrowPending();
        if (options.DpiOverride is null)
        {
            var actual = User32.GetDpiForWindow(_hwnd);
            _dpi = actual == 0 ? DefaultDpi : actual;
        }

        var client = default(Rect);
        User32.GetClientRect(_hwnd, &client);
        _clientWidth = client.Right - client.Left;
        _clientHeight = client.Bottom - client.Top;
        _surface = D2DRenderSurface.CreateWindowSurface(_hwnd, new RenderSurfaceOptions(PreferWarp: options.PreferWarp, MaxFrameLatency: 1));
        _surface.ImmediatePresent = true;
        _surface.Resize(Math.Max(1, _clientWidth), Math.Max(1, _clientHeight), 1.0);
        RecreateFont();
        View.Layout(_clientWidth, _clientHeight, _dpi / 96.0);
        if (options.EditMode == D2DEditMode.HostedWin32)
        {
            CreateHostedControls();
            SyncHosted();
        }

        View.FocusMoved += OnViewFocusMoved;
        View.Changed += _ => InvalidateView();
        if (options.Visible && !options.QuitOnClose)
        {
            User32.ShowWindow(_hwnd, WindowMessages.SwShowNoActivate);
        }

        D2DNative.SetTimer(_hwnd, BlinkTimerId, Math.Max(200, D2DNative.GetCaretBlinkTime()), 0);
    }

    public nint Hwnd => _hwnd;

    public D2DSettingsView View { get; }

    /// <summary>Đối tượng trang đã dựng (<c>D2DDisplayPage</c>).</summary>
    public object Page { get; }

    public D2DRenderSurface Surface => _surface ?? throw new ObjectDisposedException(nameof(D2DSettingsWindow));

    public uint Dpi => _dpi;

    public double Scale => _dpi / 96.0;

    public bool IsDestroyed => _destroyed;

    public int HostedCount => _hosted.Count;

    /// <summary>Trình cung cấp UIA (cửa sổ trả lời WM_GETOBJECT); null = không công bố cây trợ năng.</summary>
    public Func<nint, nint, nint, nint?>? GetObjectHandler { get; set; }

    /// <summary>Ghi thời gian từng khung (ms) khi true.</summary>
    public bool RecordTimings { get; set; }

    /// <summary>Chờ GPU xong sau Present để đo "vẽ xong thật" (chậm hơn, chỉ để đo).</summary>
    public bool MeasureGpuCompletion { get; set; }

    public List<double> CpuSubmitMs { get; } = [];

    public List<double> GpuDoneMs { get; } = [];

    public List<double> FrameTotalMs { get; } = [];

    public int Presented { get; private set; }

    public PresentResult LastPresentResult { get; private set; }

    public IReadOnlyList<(string Id, nint Hwnd)> HostedHandles => _hosted.Select(h => (h.Widget.Id, h.Hwnd)).ToList();

    // ---- vẽ -------------------------------------------------------------------------------------------------------------

    /// <summary>Hiện cửa sổ (kích hoạt) - lối vào thử.</summary>
    public void Show() => User32.ShowWindow(_hwnd, WindowMessages.SwShow);

    public void InvalidateView()
    {
        if (_hwnd != 0 && !_destroyed)
        {
            User32.InvalidateRect(_hwnd, null, false);
        }
    }

    /// <summary>Vẽ ngay một khung (bỏ qua WM_PAINT). Trả PresentResult.</summary>
    public PresentResult RenderNow()
    {
        var surface = Surface;
        var t0 = Stopwatch.GetTimestamp();
        var ctx = surface.BeginDrawContext();
        View.Render(ctx);
        var result = surface.EndDrawAndPresent();
        var t1 = Stopwatch.GetTimestamp();
        if (result == PresentResult.DeviceRecreated)
        {
            ctx = surface.BeginDrawContext();
            View.Render(ctx);
            result = surface.EndDrawAndPresent();
        }

        double t2 = t1;
        if (MeasureGpuCompletion)
        {
            surface.WaitForGpu();
            t2 = Stopwatch.GetTimestamp();
        }

        if (RecordTimings)
        {
            CpuSubmitMs.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            GpuDoneMs.Add(Stopwatch.GetElapsedTime(t0, (long)t2).TotalMilliseconds);
        }

        Presented++;
        LastPresentResult = result;
        return result;
    }

    /// <summary>Xử lý yêu cầu vẽ đang chờ ngay (như vòng lặp thông điệp), kể cả control con ở chế độ lai.</summary>
    public void UpdateNow()
    {
        D2DNative.RedrawWindow(_hwnd, null, 0, D2DNative.RdwUpdateNow | D2DNative.RdwAllChildren);
    }

    // ---- DPI + kích thước -----------------------------------------------------------------------------------------------

    /// <summary>Đổi DPI (như WM_DPICHANGED, kể cả khi tiến trình không PerMonitor): cỡ cửa sổ giữ số DIP của client.</summary>
    public void SetDpi(uint dpi)
    {
        if (dpi == _dpi || _destroyed)
        {
            return;
        }

        var oldScale = Scale;
        _dpi = dpi;
        var widthDip = _clientWidth / oldScale;
        var heightDip = _clientHeight / oldScale;
        var rect = new Rect { Left = 0, Top = 0, Right = (int)Math.Round(widthDip * Scale), Bottom = (int)Math.Round(heightDip * Scale) };
        User32.AdjustWindowRectExForDpi(&rect, WindowMessages.WsOverlappedWindow, false, 0, _dpi);
        User32.SetWindowPos(_hwnd, 0, 0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top, WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
        RethrowPending();
        ApplySize();
        RecreateFont();
        ApplyFontToHosted();
        View.Layout(_clientWidth, _clientHeight, Scale);
        SyncHosted();
        InvalidateView();
    }

    private void ApplySize()
    {
        var client = default(Rect);
        User32.GetClientRect(_hwnd, &client);
        _clientWidth = Math.Max(1, client.Right - client.Left);
        _clientHeight = Math.Max(1, client.Bottom - client.Top);
        _surface?.Resize(_clientWidth, _clientHeight, 1.0);
    }

    // ---- chế độ lai: control Win32 thật ---------------------------------------------------------------------------------

    private sealed class HostedControl(D2DWidget widget, nint hwnd)
    {
        public D2DWidget Widget { get; } = widget;

        public nint Hwnd { get; } = hwnd;
    }

    private void RecreateFont()
    {
        var old = _font;
        _font = D2DNative.CreateFont(-(int)Math.Round(12 * Scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (old != 0)
        {
            Gdi32.DeleteObject(old);
        }
    }

    private void CreateHostedControls()
    {
        const uint child = WindowMessages.WsChild | WindowMessages.WsVisible;
        var controls = new List<DialogChromeControl>();
        foreach (var (widget, _) in View.HostedAreas())
        {
            nint hwnd;
            if (widget is D2DFieldWidget field)
            {
                var style = child | DialogConstants.WsTabStop | DialogConstants.EsAutoHScroll | (field.Box.NumericOnly && !field.Box.AllowDecimal ? ControlConstants.EsNumber : 0);
                hwnd = User32.CreateWindowEx(DialogConstants.WsExClientEdge, "EDIT", string.Empty, style, 0, 0, 10, 10, _hwnd, 0, Kernel32.GetModuleHandle(0), 0);
                User32.SendMessage(hwnd, DialogConstants.EmSetLimitText, field.Box.MaxLength, 0);
            }
            else
            {
                var combo = (D2DComboWidget)widget;
                var style = child | DialogConstants.WsTabStop | ControlConstants.CbsDropDownList | DialogConstants.WsVScroll;
                hwnd = User32.CreateWindowEx(0, "COMBOBOX", string.Empty, style, 0, 0, 10, 10, _hwnd, 0, Kernel32.GetModuleHandle(0), 0);
                foreach (var item in combo.Items)
                {
                    fixed (char* text = item())
                    {
                        User32.SendMessage(hwnd, ControlConstants.CbAddString, 0, (nint)text);
                    }
                }
            }

            var hosted = new HostedControl(widget, hwnd);
            _hosted.Add(hosted);
            _hostedByHandle[hwnd] = hosted;
            controls.Add(new DialogChromeControl(hwnd, widget is D2DFieldWidget ? DialogControlClass.Edit : DialogControlClass.ComboBox));
            CommonControls.SetWindowSubclass(hwnd, &HostedSubclassProc, 0x44324453, (nuint)GCHandle.ToIntPtr(_self));
        }

        ApplyFontToHosted();
        if (_options.Dark)
        {
            DarkDialogChrome.Instance.ApplyControls(_hwnd, controls);
        }

        PushValuesToHosted();
    }

    private void ApplyFontToHosted()
    {
        foreach (var h in _hosted)
        {
            User32.SendMessage(h.Hwnd, DialogConstants.WmSetFont, _font, 1);
        }
    }

    /// <summary>Đẩy giá trị của view (sau Load) vào control thật.</summary>
    public void PushValuesToHosted()
    {
        foreach (var h in _hosted)
        {
            switch (h.Widget)
            {
                case D2DFieldWidget f:
                    User32.SetWindowText(h.Hwnd, f.Box.Text);
                    break;
                case D2DComboWidget c:
                    User32.SendMessage(h.Hwnd, ControlConstants.CbSetCurSel, c.Selected, 0);
                    break;
            }

            D2DNative.EnableWindow(h.Hwnd, h.Widget.Enabled);
        }
    }

    /// <summary>Đặt vị trí + cắt vùng control thật theo viewport (gọi sau mỗi lần cuộn/đổi bố cục).</summary>
    public void SyncHosted()
    {
        if (_hosted.Count == 0)
        {
            return;
        }

        var viewport = View.Viewport;
        var dropExtra = (int)Math.Round(120 * Scale);
        foreach (var (widget, client) in View.HostedAreas())
        {
            var h = _hosted.First(x => ReferenceEquals(x.Widget, widget));
            var visible = client.Intersect(viewport);
            if (visible.Width == 0 || visible.Height == 0 || client.Width == 0)
            {
                User32.ShowWindow(h.Hwnd, WindowMessages.SwHide);
                continue;
            }

            var height = widget is D2DComboWidget ? client.Height + dropExtra : client.Height;
            User32.SetWindowPos(h.Hwnd, 0, client.X, client.Y, client.Width, height,
                WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate | WindowMessages.SwpShowWindow);
            if (widget is D2DComboWidget)
            {
                User32.SendMessage(h.Hwnd, 0x0153 /* CB_SETITEMHEIGHT */, unchecked((nint)(-1)), client.Height - 6);
            }

            var rgn = D2DNative.CreateRectRgn(visible.X - client.X, visible.Y - client.Y, visible.Right - client.X, visible.Bottom - client.Y);
            D2DNative.SetWindowRgn(h.Hwnd, rgn, true); // hệ thống sở hữu region sau khi gọi
            D2DNative.EnableWindow(h.Hwnd, widget.Enabled);
        }
    }

    private void OnViewFocusMoved(D2DWidget? widget)
    {
        var host = widget is null ? null : _hosted.FirstOrDefault(h => ReferenceEquals(h.Widget, widget));
        User32.SetFocus(host?.Hwnd ?? _hwnd);
    }

    private void ReadHosted(nint hwnd)
    {
        if (!_hostedByHandle.TryGetValue(hwnd, out var h))
        {
            return;
        }

        switch (h.Widget)
        {
            case D2DFieldWidget f:
            {
                var length = User32.GetWindowTextLength(hwnd);
                var buffer = new char[length + 1];
                fixed (char* p = buffer)
                {
                    User32.GetWindowText(hwnd, p, buffer.Length);
                }

                f.Box.Text = new string(buffer, 0, length);
                View.NotifyChanged(f);
                break;
            }

            case D2DComboWidget c:
                c.Selected = (int)User32.SendMessage(hwnd, ControlConstants.CbGetCurSel, 0, 0);
                View.NotifyChanged(c);
                break;
        }

        PushDependents();
    }

    /// <summary>Bật/tắt control thật theo trạng thái của view (tick cha đổi).</summary>
    public void PushDependents()
    {
        foreach (var h in _hosted)
        {
            D2DNative.EnableWindow(h.Hwnd, h.Widget.Enabled);
        }
    }

    // ---- thông điệp -----------------------------------------------------------------------------------------------------

    private static int LowWord(nint v) => (short)((ulong)v & 0xFFFF);

    private static int HighWord(nint v) => (short)(((ulong)v >> 16) & 0xFFFF);

    private nint Process(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WindowMessages.WmNcCreate:
                _hwnd = hwnd;
                break;
            case WindowMessages.WmSize:
                if (_surface is not null && !_inSizeChange)
                {
                    var w = LowWord(lParam) & 0xFFFF;
                    var h = HighWord(lParam) & 0xFFFF;
                    if (w > 0 && h > 0)
                    {
                        _clientWidth = w;
                        _clientHeight = h;
                        _surface.Resize(w, h, 1.0);
                        View.Layout(w, h, Scale);
                        SyncHosted();
                        InvalidateView();
                    }
                }

                return 0;
            case WindowMessages.WmDpiChanged:
            {
                var suggested = (Rect*)lParam;
                var newDpi = (uint)(wParam & 0xFFFF);
                _dpi = newDpi;
                if (suggested is not null)
                {
                    _inSizeChange = true;
                    User32.SetWindowPos(hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                        WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
                    _inSizeChange = false;
                }

                ApplySize();
                RecreateFont();
                ApplyFontToHosted();
                View.Layout(_clientWidth, _clientHeight, Scale);
                SyncHosted();
                InvalidateView();
                return 0;
            }

            case WindowMessages.WmEraseBkgnd:
                return 1;
            case WindowMessages.WmPaint:
                if (_surface is not null)
                {
                    RenderNow();
                }

                D2DNative.ValidateRect(hwnd, null);
                if (View.NeedsRedraw)
                {
                    InvalidateView();
                }

                return 0;
            case WindowMessages.WmSetFocus:
                View.SetWindowFocused(true);
                if (View.Focused is { } focused && _hosted.FirstOrDefault(h => ReferenceEquals(h.Widget, focused)) is { } host)
                {
                    User32.SetFocus(host.Hwnd);
                }

                AfterInput();
                return 0;
            case WindowMessages.WmKillFocus:
                if (!_hostedByHandle.ContainsKey(wParam))
                {
                    View.SetWindowFocused(false);
                    AfterInput();
                }

                return 0;
            case WindowMessages.WmMouseMove:
                View.MouseMove(LowWord(lParam), HighWord(lParam), (wParam & 0x0001) != 0);
                AfterInput();
                return 0;
            case WindowMessages.WmLButtonDown or WindowMessages.WmLButtonDblClk:
                User32.SetFocus(hwnd);
                View.MouseDown(LowWord(lParam), HighWord(lParam), message == WindowMessages.WmLButtonDblClk);
                if (View.IsDragging)
                {
                    User32.SetCapture(hwnd);
                }

                AfterInput();
                return 0;
            case WindowMessages.WmLButtonUp:
                View.MouseUp(LowWord(lParam), HighWord(lParam));
                if (PendingUser32.GetCapture() == hwnd)
                {
                    User32.ReleaseCapture();
                }

                AfterInput();
                return 0;
            case WindowMessages.WmMouseWheel:
                View.Wheel(HighWord(wParam) / 120);
                AfterInput();
                return 0;
            case 0x0115 /* WM_VSCROLL */:
                HandleVScroll((int)(wParam & 0xFFFF), (int)((wParam >> 16) & 0xFFFF));
                AfterInput();
                return 0;
            case WindowMessages.WmKeyDown or WindowMessages.WmSysKeyDown:
            {
                var ctrl = D2DNative.GetKeyState(0x11) < 0;
                var shift = D2DNative.GetKeyState(0x10) < 0;
                var alt = (lParam & (1 << 29)) != 0;
                if (View.KeyDown((int)wParam, ctrl, shift, alt))
                {
                    AfterInput();
                    return 0;
                }

                break;
            }

            case WindowMessages.WmChar:
                if (View.Char((char)wParam))
                {
                    AfterInput();
                    return 0;
                }

                break;
            case WindowMessages.WmTimer when wParam == (nint)BlinkTimerId:
                View.BlinkTick();
                AfterInput();
                return 0;
            case WindowMessages.WmSetCursor when LowWord(lParam) == 1 /* HTCLIENT */:
            {
                var p2 = default(Point);
                D2DNative.GetCursorPos(&p2);
                User32.ScreenToClient(hwnd, &p2);
                var kind = View.CursorAt(p2.X, p2.Y);
                User32.SetCursor(User32.LoadCursor(0, kind == D2DCursorKind.IBeam ? 32513 : User32.IdcArrow));
                return 1;
            }

            case WmImeStartComposition:
                PlaceComposition();
                break;
            case WindowMessages.WmCommand when lParam != 0:
            {
                var notification = HighWord(wParam) & 0xFFFF;
                if (notification is 0x0300 /* EN_CHANGE */ or 1 /* CBN_SELCHANGE */ && _hostedByHandle.ContainsKey(lParam))
                {
                    ReadHosted(lParam);
                    InvalidateView();
                }

                return 0;
            }

            case DialogConstants.WmCtlColorEdit or DialogConstants.WmCtlColorListBox or DialogConstants.WmCtlColorStatic or DialogConstants.WmCtlColorBtn
                when _options.Dark:
            {
                var p = View.Palette;
                _ = Gdi32.SetTextColor(wParam, ToColorRef(message == DialogConstants.WmCtlColorStatic ? p.TextDisabled : p.Text));
                _ = Gdi32.SetBkColor(wParam, ToColorRef(p.Input));
                if (_inputBrush == 0)
                {
                    _inputBrush = Gdi32.CreateSolidBrush(ToColorRef(p.Input));
                }

                return _inputBrush;
            }

            case WmGetObject when GetObjectHandler is { } handler:
                if (handler(wParam, lParam, hwnd) is { } result)
                {
                    return result;
                }

                break;
            case WindowMessages.WmDestroy:
                _destroyed = true;
                D2DNative.KillTimer(hwnd, BlinkTimerId);
                if (_options.QuitOnClose)
                {
                    User32.PostQuitMessage(0);
                }

                break;
        }

        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static uint ToColorRef(ColorF c)
        => (uint)Math.Round(c.R * 255) | ((uint)Math.Round(c.G * 255) << 8) | ((uint)Math.Round(c.B * 255) << 16);

    private void AfterInput()
    {
        if (View.NeedsRedraw)
        {
            InvalidateView();
        }

        if (_hosted.Count > 0)
        {
            SyncHosted();
        }
    }

    /// <summary>WM_VSCROLL tới cửa sổ (trợ năng, bàn di chuột, test): cùng mã SB_* với thanh cuộn Win32.</summary>
    private void HandleVScroll(int code, int position)
    {
        var line = View.LineStepPx;
        switch (code)
        {
            case 0:
                View.ScrollTo(View.OffsetPx - line);
                break;
            case 1:
                View.ScrollTo(View.OffsetPx + line);
                break;
            case 2:
                View.ScrollTo(View.OffsetPx - Math.Max(line, View.Viewport.Height - line));
                break;
            case 3:
                View.ScrollTo(View.OffsetPx + Math.Max(line, View.Viewport.Height - line));
                break;
            case 4 or 5:
                View.ScrollTo(position);
                break;
            case 6:
                View.ScrollTo(0);
                break;
            case 7:
                View.ScrollTo(View.MaxOffsetPx);
                break;
        }
    }

    private void PlaceComposition()
    {
        if (View.Focused is not D2DFieldWidget f || _options.EditMode != D2DEditMode.SelfDrawn)
        {
            return;
        }

        var context = D2DNative.ImmGetContext(_hwnd);
        if (context == 0)
        {
            return;
        }

        var form = new D2DNative.CompositionForm
        {
            Style = 0x0002 /* CFS_POINT */,
            CurrentPos = new Point { X = f.BoxBounds.X + 6, Y = f.BoxBounds.Y + View.Viewport.Y - View.OffsetPx + 2 },
        };
        D2DNative.ImmSetCompositionWindow(context, &form);
        D2DNative.ImmReleaseContext(_hwnd, context);
    }

    /// <summary>Hình chữ nhật client -> màn hình (UIA).</summary>
    public PxRect ClientToScreenRect(PxRect r)
    {
        var p = new Point { X = r.X, Y = r.Y };
        D2DNative.ClientToScreen(_hwnd, &p);
        return new PxRect(p.X, p.Y, r.Width, r.Height);
    }

    // ---- vòng đời -------------------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_hwnd != 0 && !_destroyed)
        {
            User32.DestroyWindow(_hwnd);
        }

        _surface?.Dispose();
        _surface = null;
        _text.Dispose();
        if (_font != 0)
        {
            Gdi32.DeleteObject(_font);
            _font = 0;
        }

        if (_inputBrush != 0)
        {
            Gdi32.DeleteObject(_inputBrush);
            _inputBrush = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void RethrowPending()
    {
        var p = _pending;
        _pending = null;
        p?.Throw();
    }

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
                var windowClass = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    Style = 0x0008 /* CS_DBLCLKS */,
                    WndProc = &SurfaceWndProc,
                    Instance = Kernel32.GetModuleHandle(0),
                    Cursor = User32.LoadCursor(0, User32.IdcArrow),
                    ClassName = className,
                };
                if (User32.RegisterClassEx(&windowClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx (SettingsD2D) failed");
                }
            }

            classRegistered = true;
        }
    }

    [UnmanagedCallersOnly]
    private static nint SurfaceWndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        D2DSettingsWindow? window = null;
        try
        {
            if (message == WindowMessages.WmNcCreate)
            {
                var cookie = *(nint*)lParam;
                window = GCHandle.FromIntPtr(cookie).Target as D2DSettingsWindow;
                if (window is null)
                {
                    return 0;
                }

                User32.SetWindowLongPtr(hwnd, WindowMessages.GwlpUserData, cookie);
            }
            else
            {
                var cookie = User32.GetWindowLongPtr(hwnd, WindowMessages.GwlpUserData);
                window = cookie == 0 ? null : GCHandle.FromIntPtr(cookie).Target as D2DSettingsWindow;
            }

            if (window is null)
            {
                return User32.DefWindowProc(hwnd, message, wParam, lParam);
            }

            var result = window.Process(hwnd, message, wParam, lParam);
            if (message == WindowMessages.WmNcDestroy)
            {
                User32.SetWindowLongPtr(hwnd, WindowMessages.GwlpUserData, 0);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (window is not null)
            {
                window._pending ??= ExceptionDispatchInfo.Capture(ex);
            }

            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HostedSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            var window = GCHandle.FromIntPtr((nint)refData).Target as D2DSettingsWindow;
            if (window is not null)
            {
                switch (message)
                {
                    case WindowMessages.WmSetFocus:
                        if (window._hostedByHandle.TryGetValue(hwnd, out var host))
                        {
                            window.View.SetFocus(host.Widget, fromHost: true);
                            window.InvalidateView();
                        }

                        break;
                    case WindowMessages.WmKeyDown:
                        if (wParam is 0x09 /* TAB */ or 0x21 or 0x22 && window.HostedKeyDown((int)wParam))
                        {
                            return 0;
                        }

                        break;
                    case WindowMessages.WmChar when wParam == 0x09:
                        return 0;
                    case WindowMessages.WmMouseWheel:
                        User32.SendMessage(window._hwnd, message, wParam, lParam);
                        return 0;
                }
            }

            if (message == WindowMessages.WmNcDestroy)
            {
                CommonControls.RemoveWindowSubclass(hwnd, &HostedSubclassProc, id);
            }
        }
        catch (Exception)
        {
            // không được ném qua ranh giới native
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool HostedKeyDown(int vk)
    {
        var shift = D2DNative.GetKeyState(0x10) < 0;
        var handled = View.KeyDown(vk, false, shift, false);
        AfterInput();
        return handled;
    }

    private sealed class Win32Clipboard(D2DSettingsWindow owner) : ID2DClipboard
    {
        public string? GetText()
        {
            if (!User32.OpenClipboard(owner._hwnd))
            {
                return null;
            }

            try
            {
                var handle = User32.GetClipboardData(WindowMessages.CfUnicodeText);
                if (handle == 0)
                {
                    return null;
                }

                var ptr = Kernel32.GlobalLock(handle);
                if (ptr == 0)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }
            finally
            {
                User32.CloseClipboard();
            }
        }

        public void SetText(string text)
        {
            if (!User32.OpenClipboard(owner._hwnd))
            {
                return;
            }

            try
            {
                User32.EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var memory = Kernel32.GlobalAlloc(WindowMessages.GmemMoveable, (nuint)bytes);
                var ptr = Kernel32.GlobalLock(memory);
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                ((char*)ptr)[text.Length] = '\0';
                Kernel32.GlobalUnlock(memory);
                User32.SetClipboardData(WindowMessages.CfUnicodeText, memory);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
    }
}
