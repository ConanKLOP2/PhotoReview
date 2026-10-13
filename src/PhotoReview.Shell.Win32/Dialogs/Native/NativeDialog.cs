using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.Shell.Win32.Hosting.PendingInterop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// WP-35 GĐ1 (phương án B, CHƯA XÁC NHẬN): khung hộp thoại Win32 gốc, AOT-sạch (không WPF, không DllImport, không phản chiếu).
/// Cửa sổ do <c>CreateDialogIndirectParam</c> tạo từ template trong bộ nhớ (<see cref="DialogTemplate"/>) nên dialog manager lo
/// Tab/phím tắt/Enter/Esc (<c>IsDialogMessage</c>), IME, UIA và clipboard. Khung tự lo phần dialog manager không làm:
/// <list type="bullet">
/// <item>Modal đúng: owner bị vô hiệu hoá, vòng lặp lồng riêng; <c>WM_QUIT</c> được POST LẠI và hộp thoại đóng (bài học #486:
/// vòng lồng không được nuốt WM_QUIT); đóng từ ngoài (<see cref="Close"/>) khi shell thoát.</item>
/// <item>DPI từng màn hình: font theo DPI (<c>SystemParametersInfoForDpi</c>), đơn vị dialog-unit tính từ font đó, bố cục tính lại
/// ở <c>WM_DPICHANGED</c>; không phụ thuộc việc hệ thống tự co.</item>
/// <item>Giao diện tối: <see cref="INativeDialogChrome"/> (title bar, chủ đề điều khiển) + WM_CTLCOLOR* theo bảng màu Dark.*.</item>
/// <item>Bố cục theo dialog-unit nhưng chiều cao chữ nhiều dòng ĐO thật (<see cref="MeasureTextHeight"/>) nên chuỗi tiếng Việt dài
/// không bị cắt.</item>
/// </list>
/// Gọi trên một thread (UI thread của shell). Mỗi đối tượng dùng một lần.
/// </summary>
internal abstract unsafe class NativeDialog
{
    public const int IdOk = 1;
    public const int IdCancel = 2;

    private const uint SizeMinimized = 1;
    private const uint WsDefaultDialog = 0x80000000 /* WS_POPUP */ | 0x00C00000 /* WS_CAPTION */ | 0x00080000 /* WS_SYSMENU */;

    [ThreadStatic]
    private static ExceptionDispatchInfo? s_pending;

    private static readonly string s_alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly INativeDialogChrome _chrome;
    private readonly List<nint> _children = [];
    private GCHandle _self;
    private nint _hwnd;
    private nint _owner;
    private nint _font;
    private nint _headerFont;
    private nint _windowBrush;
    private nint _inputBrush;
    private bool _used;
    private bool _ownerDisabled;
    private bool _laidOut;
    private bool _done;
    private bool _modeless;
    private bool _cleanedUp;
    private MessageLoop? _loop;
    private ModelessFilter? _filter;
    private Action? _closedCallback;
    private nint _tooltip;
    private char* _tipBuffer;
    private int _tipCapacity;
    private readonly Dictionary<nint, Func<int, string?>> _itemTips = [];
    private readonly Dictionary<nint, int> _lastTipItem = [];
    private readonly Dictionary<nint, (int, int)> _sorts = [];
    private int _result = IdCancel;
    private uint _dpi = 96;
    private int _avgCharWidth = 6;
    private int _lineHeight = 15;

    protected NativeDialog(INativeDialogChrome? chrome = null)
    {
        _chrome = chrome ?? DarkDialogChrome.Instance;
    }

    /// <summary>HWND của hộp thoại (0 trước khi tạo và sau khi huỷ).</summary>
    public nint Hwnd => _hwnd;

    /// <summary>True khi <see cref="ShowModal"/> đã kết thúc hoặc <see cref="Close"/> đã được gọi.</summary>
    public bool IsClosed => _done;

    /// <summary>DPI hiện dùng để tính font và đơn vị.</summary>
    public uint Dpi => _dpi;

    /// <summary>Font thân hiện hành (0 khi chưa tạo).</summary>
    public nint BodyFont => _font;

    /// <summary>Tiêu đề cửa sổ.</summary>
    protected abstract string Title { get; }

    /// <summary>Các điều khiển con theo thứ tự Z (= thứ tự Tab).</summary>
    protected abstract IReadOnlyList<DialogItem> Items { get; }

    /// <summary>Có khung kéo giãn (WS_THICKFRAME) hay không.</summary>
    protected virtual bool Resizable => false;

    /// <summary>Điều khiển dùng font tiêu đề (to hơn, đậm).</summary>
    protected virtual IReadOnlyList<int> HeaderControlIds => [];

    /// <summary>Điều khiển nhận focus ban đầu (0 = điều khiển đầu tiên có WS_TABSTOP).</summary>
    protected virtual int InitialFocusId => 0;

    /// <summary>Nút mặc định (Enter): <see cref="IdOk"/>, hoặc <see cref="IdCancel"/> với hộp thoại chỉ có nút Đóng.</summary>
    protected virtual int DefaultButtonId => IdOk;

    /// <summary>Kích thước vùng client mong muốn (px) theo DPI/font hiện tại; không-resizable thì dùng cả khi nội dung đổi.</summary>
    protected abstract (int Width, int Height) PreferredClientSize();

    /// <summary>Kích thước client nhỏ nhất khi kéo giãn.</summary>
    protected virtual (int Width, int Height) MinimumClientSize() => PreferredClientSize();

    /// <summary>Đặt vị trí/kích thước điều khiển cho vùng client này (px).</summary>
    protected abstract void LayoutControls(int clientWidth, int clientHeight);

    /// <summary>Sau khi tạo điều khiển + font + giao diện tối, trước khi tính bố cục (đặt văn bản, nạp danh sách...).</summary>
    protected virtual void OnInitialized()
    {
    }

    /// <summary>Enter / nút IDOK: true = đóng với <see cref="IdOk"/>; false = giữ hộp thoại mở (đã báo lỗi).</summary>
    protected virtual bool OnOk() => true;

    /// <summary>WM_COMMAND của điều khiển khác IDOK/IDCANCEL; true = đã xử lý.</summary>
    protected virtual bool OnCommand(int id, int notification) => false;

    /// <summary>
    /// Esc / IDCANCEL từ dialog manager (không phải X/Alt+F4): true = đã xử lý (không đóng). Mặc định false = đóng như Huỷ.
    /// Benchmark dùng để Esc huỷ lượt chạy trước, chỉ đóng khi rảnh.
    /// </summary>
    protected virtual bool OnCancel() => false;

    /// <summary>WM_NOTIFY của điều khiển <paramref name="id"/>; true = đã xử lý và <paramref name="result"/> là giá trị trả về (DWLP_MSGRESULT).</summary>
    protected virtual bool OnNotify(int id, int code, nint header, out nint result)
    {
        result = 0;
        return false;
    }

    /// <summary>WM_HSCROLL/WM_VSCROLL của trackbar <paramref name="id"/> (<paramref name="code"/> = TB_*).</summary>
    protected virtual void OnScroll(int id, int code)
    {
    }

    /// <summary>Chữ + huy hiệu của dòng <paramref name="index"/> của listbox owner-draw <paramref name="id"/>.</summary>
    protected virtual (string Text, string? Badge) ListItemText(int id, int index)
        => (ListBoxControl.TextAt(Item(id), index), null);

    /// <summary>Gọi một lần ngay trước khi cửa sổ bị huỷ (đóng bình thường, đóng từ ngoài, ngoại lệ).</summary>
    protected virtual void OnClosed()
    {
    }

    /// <summary>
    /// WP-35 GĐ3a: thông điệp cửa sổ mà khung không xử lý (WM_APP của chính hộp thoại, WM_MOUSEWHEEL do cửa sổ chứa trang chuyển lên...).
    /// true = đã xử lý và <paramref name="result"/> là kết quả (DWLP_MSGRESULT); false = để dialog manager xử lý mặc định.
    /// </summary>
    protected virtual bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        return false;
    }

    /// <summary>
    /// WP-35 GĐ3a: phím (WM_KEYDOWN...) TRƯỚC khi <c>IsDialogMessage</c> thấy nó (Ctrl+Tab đổi trang: dialog manager không làm).
    /// true = đã xử lý, không đưa tiếp cho dialog manager.
    /// </summary>
    protected virtual bool OnDialogKey(in Msg message) => false;

    /// <summary>Màu chữ (COLORREF) của một STATIC.</summary>
    protected virtual uint StaticTextColor(int id) => DialogColors.Text;

    // ---- chạy modal --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Hiện hộp thoại modal trên <paramref name="owner"/> (0 = không owner) và chạy vòng lặp tới khi đóng.
    /// </summary>
    /// <param name="ready">Test/cầu nối: gọi sau khi cửa sổ đã hiện và owner đã bị vô hiệu hoá, trước vòng lặp.</param>
    /// <returns><see cref="IdOk"/> hoặc <see cref="IdCancel"/> (huỷ, X, Esc, Alt+F4, WM_QUIT, <see cref="Close"/>).</returns>
    /// <param name="loop">Vòng lặp của shell: nếu có, vòng modal chạy trong nó (khung hình, việc nền và phím của cửa sổ WPF modeless
    /// vẫn chạy); null = vòng GetMessage đơn giản (test).</param>
    public int ShowModal(nint owner, Action<NativeDialog>? ready = null, MessageLoop? loop = null)
    {
        if (_used)
        {
            throw new InvalidOperationException("A NativeDialog instance can be shown once.");
        }

        _used = true;
        _owner = owner != 0 && User32.IsWindow(owner) ? owner : 0;
        CommonControlsInit.Ensure();
        var template = DialogTemplate.Build(Title, DialogStyle(), DialogExStyle(), Items);
        _self = GCHandle.Alloc(this);
        try
        {
            fixed (byte* pinned = template)
            {
                _hwnd = User32.CreateDialogIndirectParam(Kernel32.GetModuleHandle(0), pinned, _owner, &DialogProc, GCHandle.ToIntPtr(_self));
            }

            RethrowPending();
            if (_hwnd == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateDialogIndirectParam failed.");
            }

            if (!_done)
            {
                if (_owner != 0 && User32.IsWindowEnabled(_owner))
                {
                    User32.EnableWindow(_owner, false);
                    _ownerDisabled = true;
                }

                User32.ShowWindow(_hwnd, WindowMessages.SwShow);
                User32.SetForegroundWindow(_hwnd);
                ready?.Invoke(this);
                if (!_done)
                {
                    RunLoop(loop);
                }
            }
        }
        finally
        {
            Cleanup();
        }

        RethrowPending();
        return _result;
    }

    /// <summary>
    /// WP-35 GĐ2: hiện hộp thoại MODELESS (không chặn, owner không bị vô hiệu hoá). Trả về ngay sau khi cửa sổ hiện; Tab/Esc/Enter do
    /// <c>IsDialogMessage</c> trong vòng lặp của shell (bộ lọc đăng ký vào <paramref name="loop"/>; null = người gọi tự bơm, test).
    /// Cửa sổ tự dọn khi đóng (X, nút, <see cref="Close"/>); <paramref name="closed"/> gọi đúng một lần sau khi cửa sổ đã bị huỷ.
    /// </summary>
    public void ShowModeless(nint owner, MessageLoop? loop = null, Action<NativeDialog>? ready = null, Action? closed = null)
    {
        if (_used)
        {
            throw new InvalidOperationException("A NativeDialog instance can be shown once.");
        }

        _used = true;
        _closedCallback = closed;
        _owner = owner != 0 && User32.IsWindow(owner) ? owner : 0;
        CommonControlsInit.Ensure();
        var template = DialogTemplate.Build(Title, DialogStyle(), DialogExStyle(), Items);
        _self = GCHandle.Alloc(this);
        try
        {
            fixed (byte* pinned = template)
            {
                _hwnd = User32.CreateDialogIndirectParam(Kernel32.GetModuleHandle(0), pinned, _owner, &DialogProc, GCHandle.ToIntPtr(_self));
            }

            RethrowPending();
            if (_hwnd == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateDialogIndirectParam failed.");
            }
        }
        catch
        {
            Cleanup();
            throw;
        }

        if (_done)
        {
            Cleanup();
            return;
        }

        _modeless = true;
        _loop = loop;
        if (loop is not null)
        {
            _filter = new ModelessFilter(this);
            loop.AddMessageFilter(_filter);
        }

        User32.ShowWindow(_hwnd, WindowMessages.SwShow);
        User32.SetForegroundWindow(_hwnd);
        try
        {
            ready?.Invoke(this);
        }
        catch
        {
            EndModal(IdCancel);
            throw;
        }
    }

    /// <summary>Đưa cửa sổ lên trước (khôi phục nếu đang thu nhỏ). No-op khi đã đóng.</summary>
    public void Activate()
    {
        if (_hwnd == 0 || !User32.IsWindow(_hwnd))
        {
            return;
        }

        if (User32.IsIconic(_hwnd))
        {
            User32.ShowWindow(_hwnd, WindowMessages.SwRestore);
        }

        User32.SetForegroundWindow(_hwnd);
    }

    /// <summary>Đóng hộp thoại như Huỷ (đóng từ ngoài, ví dụ shell đang thoát). Idempotent, gọi trên UI thread.</summary>
    public void Close() => EndModal(IdCancel);

    /// <summary>Đóng với kết quả cho trước.</summary>
    protected void EndModal(int result)
    {
        if (_done)
        {
            return;
        }

        _result = result;
        _done = true;
        if (_modeless)
        {
            Cleanup(); // modeless: không có vòng lặp riêng nên cửa sổ được dọn ngay
            return;
        }

        if (_hwnd != 0)
        {
            User32.PostMessage(_hwnd, WindowMessages.WmNull, 0, 0); // đánh thức GetMessage của vòng lồng đang chờ
        }
    }

    /// <summary>Bộ lọc của vòng lặp shell cho cửa sổ modeless: Tab/Esc/Enter/phím tắt qua IsDialogMessage.</summary>
    private sealed class ModelessFilter(NativeDialog dialog) : IRawMessageFilter
    {
        public bool PreFilterMessage(in WindowMessage message) => false;

        public bool PreFilterRawMessage(in Msg message)
        {
            var hwnd = dialog._hwnd;
            if (hwnd == 0)
            {
                return false;
            }

            fixed (Msg* pointer = &message)
            {
                return User32.IsDialogMessage(hwnd, pointer);
            }
        }
    }

    private void RunLoop(MessageLoop? loop)
    {
        if (loop is not null)
        {
            if (!loop.RunNested(() => _done, ClaimDialogMessage))
            {
                _result = IdCancel; // WM_QUIT (already reposted by the shell loop)
                _done = true;
            }

            return;
        }

        Msg message;
        while (!_done)
        {
            var got = User32.GetMessage(&message, 0, 0, 0);
            if (got == 0)
            {
                // WM_QUIT: KHÔNG nuốt (#486) - post lại cho vòng lặp ngoài rồi đóng hộp thoại như Huỷ.
                User32.PostQuitMessage((int)message.WParam);
                _result = IdCancel;
                _done = true;
                break;
            }

            if (got < 0)
            {
                _result = IdCancel;
                _done = true;
                break;
            }

            if (_hwnd != 0 && (OnDialogKey(message) || User32.IsDialogMessage(_hwnd, &message)))
            {
                continue;
            }

            User32.TranslateMessage(&message);
            User32.DispatchMessage(&message);
        }
    }

    /// <summary>Message cho hộp thoại (Tab, Enter, Esc, phím tắt) xử lý bằng IsDialogMessage trước khi tới bộ lọc/dispatch.</summary>
    private bool ClaimDialogMessage(ref Msg message)
    {
        if (_hwnd != 0 && OnDialogKey(message))
        {
            return true;
        }

        fixed (Msg* pointer = &message)
        {
            return _hwnd != 0 && User32.IsDialogMessage(_hwnd, pointer);
        }
    }

    private void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _done = true;
        if (_filter is not null)
        {
            _loop?.RemoveMessageFilter(_filter);
            _filter = null;
        }

        try
        {
            OnClosed();
        }
        catch (Exception ex)
        {
            s_pending ??= ExceptionDispatchInfo.Capture(ex);
        }

        foreach (var list in _listViews)
        {
            if (User32.IsWindow(list))
            {
                CommonControls.RemoveWindowSubclass(list, &ListViewSubclassProc, ListViewSubclassId);
            }
        }

        _listViews.Clear();
        foreach (var list in _itemTips.Keys)
        {
            if (User32.IsWindow(list))
            {
                CommonControls.RemoveWindowSubclass(list, &ItemTipSubclassProc, ItemTipSubclassId);
            }
        }

        _itemTips.Clear();
        _lastTipItem.Clear();
        if (_ownerDisabled && User32.IsWindow(_owner))
        {
            User32.EnableWindow(_owner, true); // trước khi huỷ hộp thoại: kích hoạt trả về owner
        }

        _ownerDisabled = false;
        if (_hwnd != 0)
        {
            var hwnd = _hwnd;
            if (User32.IsWindow(hwnd))
            {
                User32.SetWindowLongPtr(hwnd, DialogConstants.DwlpUser, 0);
                User32.DestroyWindow(hwnd);
            }

            _hwnd = 0;
        }

        DeleteObject(ref _font);
        DeleteObject(ref _headerFont);
        DeleteObject(ref _windowBrush);
        DeleteObject(ref _inputBrush);
        _children.Clear();
        _tooltip = 0;
        if (_tipBuffer != null)
        {
            NativeMemory.Free(_tipBuffer);
            _tipBuffer = null;
            _tipCapacity = 0;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        var callback = _closedCallback;
        _closedCallback = null;
        callback?.Invoke();
    }

    private static void DeleteObject(ref nint handle)
    {
        if (handle != 0)
        {
            Gdi32.DeleteObject(handle);
            handle = 0;
        }
    }

    /// <summary>Kiểu cửa sổ thêm (ví dụ nút thu nhỏ/phóng to của cửa sổ modeless).</summary>
    protected virtual uint ExtraWindowStyle => 0;

    /// <summary>Khung hộp thoại (DS_MODALFRAME): false cho cửa sổ modeless kiểu cửa sổ thường (có nút thu nhỏ/phóng to).</summary>
    protected virtual bool UsesModalFrame => true;

    private uint DialogStyle()
        => WsDefaultDialog | (UsesModalFrame ? DialogConstants.DsModalFrame : 0) | DialogConstants.DsNoFailCreate
            | (Resizable ? WindowMessages.WsThickFrame : 0) | ExtraWindowStyle;

    private uint DialogExStyle()
        => (UsesModalFrame ? DialogConstants.WsExDlgModalFrame : 0) | DialogConstants.WsExControlParent;

    // ---- WndProc ---------------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static nint DialogProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        NativeDialog? dialog = null;
        try
        {
            if (message == DialogConstants.WmInitDialog)
            {
                User32.SetWindowLongPtr(hwnd, DialogConstants.DwlpUser, lParam);
                dialog = GCHandle.FromIntPtr(lParam).Target as NativeDialog;
            }
            else
            {
                var cookie = User32.GetWindowLongPtr(hwnd, DialogConstants.DwlpUser);
                dialog = cookie == 0 ? null : GCHandle.FromIntPtr(cookie).Target as NativeDialog;
            }

            return dialog is null ? 0 : dialog.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            s_pending ??= ExceptionDispatchInfo.Capture(ex);
            dialog?.EndModal(IdCancel);
            return 0;
        }
    }

    private static void RethrowPending()
    {
        var pending = s_pending;
        if (pending is not null)
        {
            s_pending = null;
            pending.Throw();
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case DialogConstants.WmInitDialog:
                InitializeDialog(hwnd);
                return 0; // FALSE: focus đặt tay trong InitializeDialog

            case WindowMessages.WmCommand:
                return HandleCommand((int)(wParam & 0xFFFF), (int)((wParam >> 16) & 0xFFFF)) ? 1 : 0;

            case WindowMessages.WmClose:
                EndModal(IdCancel);
                return 1;

            case ControlConstants.WmNotify:
                return HandleNotify(hwnd, lParam);

            case ControlConstants.WmDrawItem:
                return DrawOwnerItem((byte*)lParam) ? 1 : 0;

            case ControlConstants.WmHScroll:
            case ControlConstants.WmVScroll:
                if (lParam != 0)
                {
                    OnScroll(User32.GetDlgCtrlID(lParam), (int)(wParam & 0xFFFF));
                }

                return 0;

            case WindowMessages.WmSize:
                if (_laidOut && (uint)wParam != SizeMinimized)
                {
                    RunLayout();
                }

                return 1;

            case WindowMessages.WmGetMinMaxInfo:
                if (Resizable && _laidOut)
                {
                    var info = (PendingMinMaxInfo*)lParam;
                    var min = MinimumClientSize();
                    var frame = WindowSizeForClient(min.Width, min.Height);
                    info->MinTrackSize = new Point { X = frame.Width, Y = frame.Height };
                }

                return 1;

            case WindowMessages.WmDpiChanged:
                OnDpiChanged((uint)(wParam & 0xFFFF), (Rect*)lParam);
                return 1;

            case DialogConstants.WmCtlColorDlg:
                return _windowBrush;

            case DialogConstants.WmCtlColorBtn:
            case DialogConstants.WmCtlColorMsgBox:
            case DialogConstants.WmCtlColorScrollBar:
                _ = Gdi32.SetTextColor(wParam, DialogColors.Text);
                _ = Gdi32.SetBkColor(wParam, DialogColors.Window);
                return _windowBrush;

            case DialogConstants.WmCtlColorStatic:
                _ = Gdi32.SetTextColor(wParam, StaticTextColor(User32.GetDlgCtrlID(lParam)));
                _ = Gdi32.SetBkColor(wParam, DialogColors.Window);
                return _windowBrush;

            case DialogConstants.WmCtlColorEdit:
            case DialogConstants.WmCtlColorListBox:
                _ = Gdi32.SetTextColor(wParam, DialogColors.TextStrong);
                _ = Gdi32.SetBkColor(wParam, DialogColors.Input);
                return _inputBrush;

            default:
                if (OnWindowMessage(message, wParam, lParam, out var handledResult))
                {
                    User32.SetWindowLongPtr(hwnd, DialogConstants.DwlpMsgResult, handledResult);
                    return 1;
                }

                return 0;
        }
    }

    private bool HandleCommand(int id, int notification)
    {
        if (id == IdOk && notification == DialogConstants.BnClicked)
        {
            if (OnOk())
            {
                EndModal(IdOk);
            }

            return true;
        }

        if (id == IdCancel && notification == DialogConstants.BnClicked)
        {
            if (!OnCancel())
            {
                EndModal(IdCancel);
            }

            return true;
        }

        return OnCommand(id, notification);
    }

    private nint HandleNotify(nint hwnd, nint lParam)
    {
        var from = *(nint*)lParam;
        var code = *(int*)(lParam + 16);
        if (code == ControlConstants.TtnGetDispInfo)
        {
            ProvideTipText(lParam);
            return 0;
        }

        var id = from != 0 ? User32.GetDlgCtrlID(from) : 0;
        if (code == ControlConstants.NmCustomDraw && IsButton(from))
        {
            // Nút/checkbox/radio/group box có chủ đề: chữ lấy màu từ chủ đề (tối xám với radio/group box) - đặt màu chữ sáng ở PREPAINT.
            if (*(uint*)(lParam + 24) == ControlConstants.CddsPrePaint)
            {
                _ = Gdi32.SetTextColor(*(nint*)(lParam + 32), DialogColors.TextStrong);
            }

            User32.SetWindowLongPtr(hwnd, DialogConstants.DwlpMsgResult, ControlConstants.CdrfDoDefault);
            return 1;
        }

        if (OnNotify(id, code, lParam, out var result))
        {
            User32.SetWindowLongPtr(hwnd, DialogConstants.DwlpMsgResult, result);
            return 1;
        }

        return 0;
    }

    private const nuint ListViewSubclassId = 0x4C563335; // "LV35"
    private readonly List<nint> _listViews = [];

    /// <summary>
    /// Header của listview gửi NM_CUSTOMDRAW cho listview (cha của header), listview KHÔNG chuyển lên hộp thoại - nên subclass listview để
    /// tự vẽ header tối (<see cref="ListViewControl.DrawHeaderItem"/>; chủ đề ItemsView vẽ chữ xám khó đọc).
    /// </summary>
    private void HookListView(nint list)
    {
        _listViews.Add(list);
        CommonControls.SetWindowSubclass(list, &ListViewSubclassProc, ListViewSubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    [UnmanagedCallersOnly]
    private static nint ListViewSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (message == ControlConstants.WmNotify && lParam != 0 && *(int*)(lParam + 16) == ControlConstants.NmCustomDraw
                && *(nint*)lParam != 0 && *(nint*)lParam == ListViewControl.Header(hwnd) && refData != 0
                && GCHandle.FromIntPtr((nint)refData).Target is NativeDialog dialog)
            {
                var sort = dialog._sorts.TryGetValue(hwnd, out var state) ? state : (-1, 0);
                return ListViewControl.DrawHeaderItem(lParam, dialog.Dx(3), sort.Item1, sort.Item2);
            }

            if (message == ControlConstants.WmNcDestroy)
            {
                CommonControls.RemoveWindowSubclass(hwnd, &ListViewSubclassProc, id);
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged; header chỉ là phần thưởng thẩm mỹ.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Lớp "Button" (nút, tick, radio, group box): chữ của chúng coi '&amp;' là phím tắt (phải nhân đôi để hiện).</summary>
    protected static bool IsButton(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        char* name = stackalloc char[16];
        var length = User32.GetClassName(hwnd, name, 16);
        return length == 6 && new ReadOnlySpan<char>(name, length).Equals("Button", StringComparison.OrdinalIgnoreCase);
    }

    private bool DrawOwnerItem(byte* drawItem)
    {
        if (drawItem == null)
        {
            return false;
        }

        var id = *(int*)(drawItem + 4);
        var itemId = *(int*)(drawItem + 8);
        var control = *(nint*)(drawItem + 24);
        (string Text, string? Badge) item = itemId >= 0 ? ListItemText(id, itemId) : (string.Empty, null);
        var font = control != 0 ? (nint)User32.SendMessage(control, DialogConstants.WmGetFont, 0, 0) : 0;
        ListBoxControl.DrawItem(drawItem, item.Text, item.Badge, font, Dx(4));
        return true;
    }

    // ---- tooltip -----------------------------------------------------------------------------------------------------

    private const nuint ItemTipSubclassId = 0x57543335; // "WT35"

    /// <summary>Tooltip chữ cố định cho điều khiển <paramref name="id"/> (hiện khi rê chuột, dark theo bảng màu).</summary>
    protected void SetTooltip(int id, string text)
    {
        var control = Item(id);
        if (control == 0 || EnsureTooltip() == 0)
        {
            return;
        }

        fixed (char* p = text)
        {
            AddTool(control, (nint)p);
        }
    }

    /// <summary>
    /// Tooltip THEO DÒNG của listbox <paramref name="id"/>: <paramref name="textOf"/>(chỉ số dòng dưới con trỏ) trả chữ (null/rỗng =
    /// không hiện). Listbox được subclass để đổi dòng thì tooltip hiện lại (một tool cho cả listbox, nên cuộn không lệch vùng).
    /// </summary>
    protected void SetItemTooltips(int id, Func<int, string?> textOf)
    {
        var list = Item(id);
        if (list == 0 || EnsureTooltip() == 0)
        {
            return;
        }

        _itemTips[list] = textOf;
        _lastTipItem[list] = -1;
        AddTool(list, ControlConstants.LpstrTextCallback);
        CommonControls.SetWindowSubclass(list, &ItemTipSubclassProc, ItemTipSubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    private nint EnsureTooltip()
    {
        if (_tooltip != 0)
        {
            return _tooltip;
        }

        const uint WsPopup = 0x80000000;
        const uint WsExTopmost = 0x00000008;
        const int UseDefault = unchecked((int)0x80000000);
        _tooltip = User32.CreateWindowEx(WsExTopmost, ControlConstants.TooltipClass, string.Empty,
            WsPopup | ControlConstants.TtsAlwaysTip | ControlConstants.TtsNoPrefix,
            UseDefault, UseDefault, UseDefault, UseDefault, _hwnd, 0, Kernel32.GetModuleHandle(0), 0);
        if (_tooltip != 0)
        {
            // Không visual style để TTM_SETTIPBKCOLOR/TEXTCOLOR có hiệu lực (chủ đề hệ thống sẽ bỏ qua màu).
            Uxtheme.SetWindowTheme(_tooltip, string.Empty, string.Empty);
            User32.SendMessage(_tooltip, ControlConstants.TtmSetTipBkColor, (nint)DialogColors.FromKey("Dark.Tooltip"), 0);
            User32.SendMessage(_tooltip, ControlConstants.TtmSetTipTextColor, (nint)DialogColors.TextStrong, 0);
            User32.SendMessage(_tooltip, ControlConstants.TtmSetMaxTipWidth, 0, Dx(240));
        }

        return _tooltip;
    }

    private void AddTool(nint control, nint text)
    {
        byte* tool = stackalloc byte[ControlConstants.ToolInfoSize];
        new Span<byte>(tool, ControlConstants.ToolInfoSize).Clear();
        *(uint*)tool = ControlConstants.ToolInfoSize;
        *(uint*)(tool + 4) = ControlConstants.TtfIdIsHwnd | ControlConstants.TtfSubclass;
        *(nint*)(tool + 8) = _hwnd;
        *(nint*)(tool + 16) = control;
        *(nint*)(tool + 48) = text;
        User32.SendMessage(_tooltip, ControlConstants.TtmAddTool, 0, (nint)tool);
    }

    /// <summary>Số tool đã đăng ký (test).</summary>
    internal int TooltipToolCount => _tooltip == 0 ? 0 : (int)User32.SendMessage(_tooltip, ControlConstants.TtmGetToolCount, 0, 0);

    /// <summary>Chữ tooltip hiện dành cho dòng <paramref name="index"/> của listbox <paramref name="id"/> (test).</summary>
    internal string? ItemTooltipText(int id, int index)
        => _itemTips.TryGetValue(Item(id), out var provider) ? provider(index) : null;

    private void ProvideTipText(nint header)
    {
        // TTF_IDISHWND: idFrom = HWND của điều khiển; NMTTDISPINFOW.lpszText ở offset 24.
        var control = *(nint*)(header + 8);
        string? text = null;
        if (_itemTips.TryGetValue(control, out var provider))
        {
            Point point = default;
            if (User32.GetCursorPos(&point))
            {
                User32.ScreenToClient(control, &point);
                var index = ListBoxControl.ItemFromPoint(control, point.X, point.Y);
                _lastTipItem[control] = index;
                text = index >= 0 ? provider(index) : null;
            }
        }

        text ??= string.Empty;
        EnsureTipBuffer(text.Length + 1);
        text.AsSpan().CopyTo(new Span<char>(_tipBuffer, text.Length));
        _tipBuffer[text.Length] = (char)0;
        *(char**)(header + 24) = _tipBuffer;
    }

    private void EnsureTipBuffer(int chars)
    {
        if (_tipCapacity >= chars)
        {
            return;
        }

        var capacity = Math.Max(chars, 256);
        _tipBuffer = _tipBuffer == null
            ? (char*)NativeMemory.Alloc((nuint)capacity, sizeof(char))
            : (char*)NativeMemory.Realloc(_tipBuffer, (nuint)capacity * sizeof(char));
        _tipCapacity = capacity;
    }

    [UnmanagedCallersOnly]
    private static nint ItemTipSubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (message == ControlConstants.WmMouseMove && refData != 0
                && GCHandle.FromIntPtr((nint)refData).Target is NativeDialog dialog)
            {
                dialog.OnItemTipMouseMove(hwnd, (short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
            }
            else if (message == ControlConstants.WmNcDestroy)
            {
                CommonControls.RemoveWindowSubclass(hwnd, &ItemTipSubclassProc, id);
            }
        }
        catch (Exception)
        {
            // Ngoại lệ không được vượt ranh giới unmanaged; tooltip chỉ là phần thưởng.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void OnItemTipMouseMove(nint list, int x, int y)
    {
        var index = ListBoxControl.ItemFromPoint(list, x, y);
        if (_lastTipItem.TryGetValue(list, out var last) && last != index)
        {
            _lastTipItem[list] = index;
            if (_tooltip != 0)
            {
                User32.SendMessage(_tooltip, ControlConstants.TtmPop, 0, 0); // hiện lại với chữ của dòng mới
            }
        }
    }

    // ---- khởi tạo / bố cục / DPI ----------------------------------------------------------------------------------------

    private void InitializeDialog(nint hwnd)
    {
        _hwnd = hwnd;
        _dpi = DpiFor(_owner != 0 ? _owner : hwnd);
        _windowBrush = Gdi32.CreateSolidBrush(DialogColors.Window);
        _inputBrush = Gdi32.CreateSolidBrush(DialogColors.Input);
        var controls = new List<DialogChromeControl>(Items.Count);
        foreach (var item in Items)
        {
            var child = User32.GetDlgItem(hwnd, item.Id);
            if (child != 0)
            {
                if (item.Class == DialogControlClass.ListView)
                {
                    HookListView(child);
                }

                _children.Add(child);
                controls.Add(new DialogChromeControl(child, item.Class));
            }
        }

        ApplyFonts();
        _chrome.Apply(hwnd, controls);
        User32.SendMessage(hwnd, DialogConstants.DmSetDefId, DefaultButtonId, 0);
        OnInitialized();

        var size = PreferredClientSize();
        PlaceInitially(size.Width, size.Height);
        _laidOut = true;
        RunLayout();
        var focus = InitialFocusId != 0 ? User32.GetDlgItem(hwnd, InitialFocusId) : FirstTabStop();
        if (focus != 0)
        {
            User32.SetFocus(focus);
        }
    }

    private nint FirstTabStop()
    {
        foreach (var child in _children)
        {
            var style = (uint)User32.GetWindowLongPtr(child, WindowMessages.GwlStyle);
            if ((style & DialogConstants.WsTabStop) != 0)
            {
                return child;
            }
        }

        return 0;
    }

    private static uint DpiFor(nint hwnd)
    {
        var dpi = hwnd != 0 ? User32.GetDpiForWindow(hwnd) : 0;
        return dpi != 0 ? dpi : Math.Max(User32.GetDpiForSystem(), 96);
    }

    /// <summary>Tạo lại font theo <see cref="_dpi"/>, gán cho cửa sổ + con, tính lại dialog-unit.</summary>
    private void ApplyFonts()
    {
        var oldFont = _font;
        var oldHeader = _headerFont;
        CreateFonts(_dpi, HeaderControlIds.Count > 0, out _font, out _headerFont);
        User32.SendMessage(_hwnd, DialogConstants.WmSetFont, _font, 0);
        var header = new HashSet<int>(HeaderControlIds);
        foreach (var child in _children)
        {
            var font = header.Contains(User32.GetDlgCtrlID(child)) && _headerFont != 0 ? _headerFont : _font;
            User32.SendMessage(child, DialogConstants.WmSetFont, font, 0);
        }

        DeleteIfSet(oldFont);
        DeleteIfSet(oldHeader);
        MeasureUnits();
    }

    private static void DeleteIfSet(nint handle)
    {
        if (handle != 0)
        {
            Gdi32.DeleteObject(handle);
        }
    }

    internal static void CreateFonts(uint dpi, bool header, out nint body, out nint headerFont)
    {
        const int size = DialogConstants.NonClientMetricsSize;
        byte* buffer = stackalloc byte[size];
        new Span<byte>(buffer, size).Clear();
        *(uint*)buffer = size;
        byte* logFont = buffer + DialogConstants.NonClientMessageFontOffset;
        if (!User32.SystemParametersInfoForDpi(DialogConstants.SpiGetNonClientMetrics, size, buffer, 0, dpi) || *(int*)logFont == 0)
        {
            // Dự phòng: Segoe UI 9 pt theo DPI.
            new Span<byte>(buffer, size).Clear();
            *(int*)logFont = -(int)Math.Round(9.0 * dpi / 72.0);
            *(int*)(logFont + 16) = 400;
            logFont[23] = 1; // DEFAULT_CHARSET
            logFont[26] = 5; // CLEARTYPE_QUALITY
            var face = "Segoe UI";
            var faceSpan = new Span<char>(logFont + 28, 32);
            face.AsSpan().CopyTo(faceSpan);
        }

        body = Gdi32.CreateFontIndirect(logFont);
        headerFont = 0;
        if (header)
        {
            *(int*)logFont = *(int*)logFont * 14 / 10; // 1,4 lần (chiều cao âm = theo ký tự)
            *(int*)(logFont + 16) = 600;
            headerFont = Gdi32.CreateFontIndirect(logFont);
        }
    }

    /// <summary>Đơn vị dialog như GdiGetCharDimensions: bề rộng ký tự trung bình = (cx của 52 chữ / 26 + 1) / 2.</summary>
    private void MeasureUnits()
    {
        var hdc = User32.GetDC(_hwnd);
        if (hdc == 0)
        {
            return;
        }

        var old = Gdi32.SelectObject(hdc, _font);
        Point extent = default;
        fixed (char* text = s_alphabet)
        {
            if (Gdi32.GetTextExtentPoint32(hdc, text, s_alphabet.Length, &extent))
            {
                _avgCharWidth = Math.Max(1, (extent.X / 26 + 1) / 2);
                _lineHeight = Math.Max(1, extent.Y);
            }
        }

        Gdi32.SelectObject(hdc, old);
        _ = User32.ReleaseDC(_hwnd, hdc);
    }

    /// <summary>Dialog-unit ngang -> px (4 dlu = một ký tự trung bình).</summary>
    protected int Dx(int dialogUnits) => (dialogUnits * _avgCharWidth + 2) / 4;

    /// <summary>Dialog-unit dọc -> px (8 dlu = chiều cao ký tự).</summary>
    protected int Dy(int dialogUnits) => (dialogUnits * _lineHeight + 4) / 8;

    /// <summary>Chiều cao một dòng chữ (px).</summary>
    protected int LineHeight => _lineHeight;

    private readonly List<nint> _childHosts = [];

    /// <summary>Cookie (GCHandle) của hộp thoại cho <c>refData</c> của subclass của lớp dẫn xuất; hợp lệ tới sau khi cửa sổ bị huỷ.</summary>
    protected nuint SelfRefData => (nuint)GCHandle.ToIntPtr(_self);

    /// <summary>
    /// WP-35 GĐ3a: cửa sổ chứa (con của hộp thoại) mà điều khiển của template được <c>SetParent</c> vào (trang Settings cuộn được);
    /// <see cref="Item"/> tìm cả trong các cửa sổ này để id vẫn tra được.
    /// </summary>
    protected void AddChildHost(nint host) => _childHosts.Add(host);

    /// <summary>
    /// WP-35 GĐ3a: điều khiển do lớp dẫn xuất tạo SAU khi hộp thoại đã có (trang Settings tạo lười): đặt font thân hiện hành và cho vào
    /// danh sách con để đổi DPI/font cập nhật theo và <c>Cleanup</c> dọn.
    /// </summary>
    protected void RegisterChild(nint control)
    {
        if (control == 0)
        {
            return;
        }

        _children.Add(control);
        User32.SendMessage(control, DialogConstants.WmSetFont, _font, 0);
    }

    /// <summary>Áp giao diện tối cho điều khiển tạo thêm sau (cùng chủ đề như điều khiển của template; không đụng title bar).</summary>
    protected void ApplyChrome(IReadOnlyList<DialogChromeControl> controls) => _chrome.ApplyControls(_hwnd, controls);

    protected nint Item(int id)
    {
        var child = User32.GetDlgItem(_hwnd, id);
        for (var i = 0; child == 0 && i < _childHosts.Count; i++)
        {
            child = User32.GetDlgItem(_childHosts[i], id);
        }

        return child;
    }

    /// <summary>HWND của điều khiển <paramref name="id"/> (0 khi không có): test và view ghép.</summary>
    internal nint ControlHandle(int id) => Item(id);

    protected void Place(int id, int x, int y, int width, int height)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.MoveWindow(child, x, y, Math.Max(0, width), Math.Max(0, height), true);
        }
    }

    /// <summary>Chiều cao dòng (px) của listbox owner-draw cố định; gọi lại mỗi lần bố cục (đổi DPI/font).</summary>
    protected void SetListItemHeight(int id, int heightPx)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.SendMessage(child, ControlConstants.LbSetItemHeight, 0, Math.Max(1, heightPx));
        }
    }

    /// <summary>Mũi tên sắp xếp của header listview <paramref name="id"/>: <paramref name="direction"/> 0 = không, 1 = tăng, 2 = giảm.</summary>
    protected void SetListViewSort(int id, int column, int direction)
    {
        var list = Item(id);
        if (list == 0)
        {
            return;
        }

        _sorts[list] = (column, direction);
        var header = ListViewControl.Header(list);
        if (header != 0)
        {
            User32.InvalidateRect(header, null, true);
        }
    }

    protected void SetItemText(int id, string text)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.SetWindowText(child, text);
        }
    }

    protected string GetItemText(int id) => GetText(Item(id));

    protected static string GetText(nint hwnd)
    {
        if (hwnd == 0)
        {
            return string.Empty;
        }

        var length = User32.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = User32.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, Math.Max(0, copied));
        }
    }

    protected void ShowItem(int id, bool visible)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.ShowWindow(child, visible ? WindowMessages.SwShowNa : WindowMessages.SwHide);
        }
    }

    /// <summary>Chiều cao (px) chữ của điều khiển <paramref name="id"/> khi xuống dòng trong <paramref name="widthPx"/>.</summary>
    protected int MeasureItemHeight(int id, int widthPx)
    {
        var child = Item(id);
        return child == 0
            ? 0
            : MeasureTextHeight(GetText(child), widthPx, (nint)User32.SendMessage(child, DialogConstants.WmGetFont, 0, 0));
    }

    /// <summary>Chiều cao chữ nhiều dòng (px, tối thiểu một dòng) với font cho trước.</summary>
    protected int MeasureTextHeight(string text, int widthPx, nint font)
    {
        var hdc = User32.GetDC(_hwnd);
        if (hdc == 0)
        {
            return _lineHeight;
        }

        var old = Gdi32.SelectObject(hdc, font != 0 ? font : _font);
        var rect = new Rect { Left = 0, Top = 0, Right = Math.Max(1, widthPx), Bottom = 0 };
        fixed (char* p = text)
        {
            _ = User32.DrawText(hdc, p, text.Length, &rect, DialogConstants.DtCalcRect | DialogConstants.DtWordBreak | DialogConstants.DtNoPrefix);
        }

        Gdi32.SelectObject(hdc, old);
        _ = User32.ReleaseDC(_hwnd, hdc);
        return Math.Max(_lineHeight, rect.Bottom - rect.Top);
    }

    /// <summary>Bề rộng (px) của một dòng chữ với font thân.</summary>
    protected int MeasureTextWidth(string text, nint font = 0)
    {
        var hdc = User32.GetDC(_hwnd);
        if (hdc == 0)
        {
            return 0;
        }

        var old = Gdi32.SelectObject(hdc, font != 0 ? font : _font);
        Point extent = default;
        fixed (char* p = text)
        {
            Gdi32.GetTextExtentPoint32(hdc, p, text.Length, &extent);
        }

        Gdi32.SelectObject(hdc, old);
        _ = User32.ReleaseDC(_hwnd, hdc);
        return extent.X;
    }

    /// <summary>Bề rộng (px) nhỏ nhất của nút có chữ này: chữ + đệm, không dưới 50 dlu.</summary>
    protected int ButtonWidth(params int[] ids)
    {
        var width = Dx(50);
        foreach (var id in ids)
        {
            width = Math.Max(width, MeasureTextWidth(GetItemText(id)) + Dx(16));
        }

        return width;
    }

    /// <summary>Đặt các nút thành một hàng căn phải (thứ tự trái -> phải), cùng bề rộng; trả về bề rộng cả hàng.</summary>
    protected int PlaceButtonRow(int clientWidth, int rightMargin, int top, int height, params int[] ids)
    {
        var width = ButtonWidth(ids);
        var gap = Dx(6);
        var x = clientWidth - rightMargin - ids.Length * width - (ids.Length - 1) * gap;
        foreach (var id in ids)
        {
            Place(id, x, top, width, height);
            x += width + gap;
        }

        return ids.Length * width + (ids.Length - 1) * gap;
    }

    /// <summary>Bề rộng hàng nút (như <see cref="PlaceButtonRow"/>) để tính bề rộng tối thiểu của client.</summary>
    protected int ButtonRowWidth(params int[] ids) => ids.Length * ButtonWidth(ids) + (ids.Length - 1) * Dx(6);

    /// <summary>Kích thước cửa sổ (gồm khung) cho vùng client cho trước ở DPI hiện tại.</summary>
    protected (int Width, int Height) WindowSizeForClient(int clientWidth, int clientHeight)
    {
        var rect = new Rect { Left = 0, Top = 0, Right = clientWidth, Bottom = clientHeight };
        var style = (uint)User32.GetWindowLongPtr(_hwnd, WindowMessages.GwlStyle);
        var exStyle = (uint)User32.GetWindowLongPtr(_hwnd, WindowMessages.GwlExStyle);
        User32.AdjustWindowRectExForDpi(&rect, style, false, exStyle, _dpi);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Đổi kích thước vùng client (giữ góc trên-trái); gọi khi nội dung đổi cỡ (ví dụ hiện thông báo lỗi).</summary>
    protected void ResizeClient(int clientWidth, int clientHeight)
    {
        if (_hwnd == 0)
        {
            return;
        }

        var frame = WindowSizeForClient(clientWidth, clientHeight);
        User32.SetWindowPos(_hwnd, 0, 0, 0, frame.Width, frame.Height,
            WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
    }

    /// <summary>Vùng client hiện tại (px).</summary>
    protected (int Width, int Height) CurrentClientSize()
    {
        Rect rect = default;
        User32.GetClientRect(_hwnd, &rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Tính lại bố cục cho vùng client hiện tại.</summary>
    protected void RunLayout()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var client = CurrentClientSize();
        LayoutControls(client.Width, client.Height);
        User32.InvalidateRect(_hwnd, null, true);
    }

    /// <summary>Đặt cỡ + căn giữa owner, kẹp vào vùng làm việc của màn hình chứa owner.</summary>
    private void PlaceInitially(int clientWidth, int clientHeight)
    {
        var frame = WindowSizeForClient(clientWidth, clientHeight);
        Rect work = default;
        var monitor = User32.MonitorFromWindow(_owner, WindowMessages.MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (monitor != 0 && User32.GetMonitorInfo(monitor, &info))
        {
            work = info.Work;
        }
        else
        {
            work = new Rect { Left = 0, Top = 0, Right = User32.GetSystemMetrics(WindowMessages.SmCxScreen), Bottom = User32.GetSystemMetrics(WindowMessages.SmCyScreen) };
        }

        int x;
        int y;
        Rect ownerRect = default;
        if (_owner != 0 && User32.GetWindowRect(_owner, &ownerRect))
        {
            x = ownerRect.Left + (ownerRect.Right - ownerRect.Left - frame.Width) / 2;
            y = ownerRect.Top + (ownerRect.Bottom - ownerRect.Top - frame.Height) / 2;
        }
        else
        {
            x = work.Left + (work.Right - work.Left - frame.Width) / 2;
            y = work.Top + (work.Bottom - work.Top - frame.Height) / 2;
        }

        if (Resizable)
        {
            // Cửa sổ kéo giãn lớn hơn vùng làm việc (màn hình nhỏ/CI 1024x768): co lại cho vừa (người dùng kéo to lại được).
            frame = (Math.Min(frame.Width, work.Right - work.Left), Math.Min(frame.Height, work.Bottom - work.Top));
        }

        x = Math.Max(work.Left, Math.Min(x, work.Right - frame.Width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - frame.Height));
        User32.SetWindowPos(_hwnd, 0, x, y, frame.Width, frame.Height, WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
    }

    private void OnDpiChanged(uint newDpi, Rect* suggested)
    {
        if (newDpi == 0 || newDpi == _dpi || _hwnd == 0)
        {
            return;
        }

        _dpi = newDpi;
        ApplyFonts();
        if (Resizable)
        {
            // Cỡ theo đề xuất của hệ thống (đã nhân tỉ lệ), không nhỏ hơn cỡ tối thiểu ở DPI mới.
            User32.SetWindowPos(_hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left,
                suggested->Bottom - suggested->Top, WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
        }
        else
        {
            var size = PreferredClientSize();
            var frame = WindowSizeForClient(size.Width, size.Height);
            User32.SetWindowPos(_hwnd, 0, suggested->Left, suggested->Top, frame.Width, frame.Height,
                WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
        }

        RunLayout();
    }
}
