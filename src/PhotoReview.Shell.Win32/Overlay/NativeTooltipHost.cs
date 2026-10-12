using System.Runtime.InteropServices;
using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>Một vùng tooltip: hộp (DIP, toạ độ client) và chữ.</summary>
internal readonly record struct OverlayTooltip(string Id, RectD Bounds, string Text);

/// <summary>
/// WP-23: nơi hiện tooltip của overlay. Production = <see cref="NativeTooltipHost"/> (<c>tooltips_class32</c>, thẻ chọn
/// native); test thay bằng bản giả ghi lại danh sách vùng.
/// </summary>
internal interface IOverlayTooltips : IDisposable
{
    /// <summary>Thay toàn bộ vùng tooltip (chỉ vùng đang hiện và nhận chuột). Gọi lặp với cùng danh sách là rẻ.</summary>
    void Sync(IReadOnlyList<OverlayTooltip> tools, double dpiScale);

    /// <summary>Chuyển một thông điệp chuột của cửa sổ cho control tooltip (<c>TTM_RELAYEVENT</c>).</summary>
    void RelayMouse(uint message, nint wParam, nint lParam);
}

/// <summary>
/// WP-23: tooltip hệ thống (<c>tooltips_class32</c>, TTS_ALWAYSTIP | TTS_NOPREFIX) cho các vùng hình chữ nhật của overlay.
/// Không subclass cửa sổ chủ: chuột được chuyển bằng <c>TTM_RELAYEVENT</c> từ handler thông điệp. Thời gian chờ/hiện lấy mặc
/// định của hệ thống (như <c>ToolTipService</c> của WPF). Chữ dài ngắt dòng ở <see cref="MaxWidthDip"/>. UI thread.
/// </summary>
internal sealed unsafe class NativeTooltipHost : IOverlayTooltips
{
    /// <summary>Rộng tối đa của một dòng tooltip (DIP) trước khi ngắt dòng.</summary>
    internal const double MaxWidthDip = 420;

    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint IccWin95Classes = 0x4;
    private const int CwUseDefault = unchecked((int)0x80000000);

    private readonly nint _owner;
    private nint _tip;
    private List<OverlayTooltip> _tools = [];
    private double _dpi = double.NaN;

    /// <param name="owner">Cửa sổ chủ (HWND shell); tooltip là cửa sổ popup sở hữu bởi nó.</param>
    public NativeTooltipHost(nint owner)
    {
        if (owner == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(owner));
        }

        _owner = owner;
        uint* init = stackalloc uint[2] { 8, IccWin95Classes };
        OverlayNative.InitCommonControlsEx(init);
        _tip = OverlayNative.CreateWindowEx(WsExTopmost, OverlayNative.TooltipClass, null,
            WsPopup | OverlayNative.TtsAlwaysTip | OverlayNative.TtsNoPrefix,
            CwUseDefault, CwUseDefault, CwUseDefault, CwUseDefault, owner, 0, OverlayNative.GetModuleHandle(null), 0);
        if (_tip == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Creating the tooltip control failed.");
        }
    }

    /// <summary>HWND của control tooltip (test: đọc số vùng/chữ).</summary>
    internal nint Handle => _tip;

    /// <summary>Số vùng đang đăng ký (<c>TTM_GETTOOLCOUNT</c>).</summary>
    internal int ToolCount => _tip == 0 ? 0 : (int)OverlayNative.SendMessage(_tip, OverlayNative.TtmGetToolCount, 0, 0);

    public void Sync(IReadOnlyList<OverlayTooltip> tools, double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (_tip == 0)
        {
            return;
        }

        if (dpiScale <= 0)
        {
            dpiScale = 1;
        }

        if (dpiScale == _dpi && tools.Count == _tools.Count && Same(tools))
        {
            return;
        }

        for (int i = _tools.Count - 1; i >= 0; i--)
        {
            OverlayToolInfo old = Info(i, default, 1, 0);
            OverlayNative.SendMessage(_tip, OverlayNative.TtmDelTool, 0, (nint)(&old));
        }

        if (dpiScale != _dpi)
        {
            _dpi = dpiScale;
            OverlayNative.SendMessage(_tip, OverlayNative.TtmSetMaxTipWidth, 0, (nint)Math.Round(MaxWidthDip * dpiScale));
        }

        _tools = [.. tools];
        for (int i = 0; i < _tools.Count; i++)
        {
            OverlayTooltip tool = _tools[i];
            nint text = Marshal.StringToCoTaskMemUni(tool.Text);
            try
            {
                OverlayToolInfo info = Info(i, tool.Bounds, dpiScale, text);
                OverlayNative.SendMessage(_tip, OverlayNative.TtmAddTool, 0, (nint)(&info));
            }
            finally
            {
                Marshal.FreeCoTaskMem(text); // TTM_ADDTOOL copia la chuỗi
            }
        }
    }

    public void RelayMouse(uint message, nint wParam, nint lParam)
    {
        if (_tip == 0 || _tools.Count == 0)
        {
            return;
        }

        var msg = new OverlayMsg
        {
            Hwnd = _owner,
            Message = message,
            WParam = wParam,
            LParam = lParam,
            X = (short)(lParam & 0xFFFF),
            Y = (short)((lParam >> 16) & 0xFFFF),
        };
        OverlayNative.SendMessage(_tip, OverlayNative.TtmRelayEvent, 0, (nint)(&msg));
    }

    /// <summary>Đặt độ trễ trước khi hiện (ms) - test dùng giá trị nhỏ để quan sát hiển thị.</summary>
    internal void SetInitialDelay(int milliseconds) =>
        OverlayNative.SendMessage(_tip, OverlayNative.TtmSetDelayTime, OverlayNative.TtdtInitial, milliseconds);

    /// <summary>Control tooltip đang hiện (cửa sổ nhìn thấy được).</summary>
    internal bool IsShowing => _tip != 0 && OverlayNative.IsWindowVisible(_tip);

    /// <summary>Chữ hiện hành của vùng tool <paramref name="index"/> (<c>TTM_GETTEXT</c>).</summary>
    internal string TextOf(int index)
    {
        if (_tip == 0 || index < 0 || index >= _tools.Count)
        {
            return string.Empty;
        }

        char* buffer = stackalloc char[512];
        OverlayToolInfo info = Info(index, default, 1, (nint)buffer);
        OverlayNative.SendMessage(_tip, OverlayNative.TtmGetText, 512, (nint)(&info));
        return new string(buffer);
    }

    public void Dispose()
    {
        nint tip = Interlocked.Exchange(ref _tip, 0);
        if (tip != 0)
        {
            OverlayNative.DestroyWindow(tip);
        }

        _tools = [];
    }

    private bool Same(IReadOnlyList<OverlayTooltip> tools)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] != _tools[i])
            {
                return false;
            }
        }

        return true;
    }

    private OverlayToolInfo Info(int index, RectD bounds, double dpi, nint text) => new()
    {
        CbSize = (uint)sizeof(OverlayToolInfo),
        Flags = 0,
        Hwnd = _owner,
        Id = (nuint)(index + 1),
        Left = (int)Math.Floor(bounds.X * dpi),
        Top = (int)Math.Floor(bounds.Y * dpi),
        Right = (int)Math.Ceiling((bounds.X + bounds.Width) * dpi),
        Bottom = (int)Math.Ceiling((bounds.Y + bounds.Height) * dpi),
        Text = text,
    };
}
