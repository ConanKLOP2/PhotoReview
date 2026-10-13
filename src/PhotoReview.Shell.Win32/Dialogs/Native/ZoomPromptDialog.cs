using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// "Click zoom level &gt; Custom...": một ô số 10..800 + Apply/Cancel (view Win32 của <see cref="ClickZoomCustomPresenter"/>;
/// WPF: <c>ClickZoomCustomDialog</c>). Nhập sai: thông báo "nhập 25..N" hiện dưới ô, hộp thoại giữ mở; Enter = Apply, Esc/X/Alt+F4 = Huỷ.
/// </summary>
internal sealed class ZoomPromptDialog : NativeDialog
{
    internal const int LabelId = 100;
    internal const int EditId = 101;
    internal const int ErrorId = 102;
    internal const int CancelId = IdCancel;
    internal const int ApplyId = IdOk;

    private readonly ClickZoomCustomPresenter _presenter;
    private bool _errorVisible;

    public ZoomPromptDialog(int currentPercent, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _presenter = new ClickZoomCustomPresenter(currentPercent);
    }

    /// <summary>Giá trị đã kiểm hợp lệ sau khi đóng bằng Apply; null khi huỷ.</summary>
    public int? Value { get; private set; }

    /// <summary>Thông báo lỗi đang hiện (null khi không có).</summary>
    public string? ErrorMessage { get; private set; }

    protected override string Title => Tr.DialogClickZoomCustomTitle;

    protected override IReadOnlyList<DialogItem> Items { get; } =
    [
        new(DialogControlClass.Static, LabelId, Tr.DialogClickZoomCustomLabel, DialogStyles.Child | DialogConstants.SsNoPrefix),
        new(DialogControlClass.Edit, EditId, string.Empty,
            DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.EsAutoHScroll, DialogConstants.WsExClientEdge),
        new(DialogControlClass.Static, ErrorId, string.Empty, WindowMessages.WsChild | DialogConstants.SsNoPrefix),
        new(DialogControlClass.Button, CancelId, Tr.CommonCancel, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsPushButton),
        new(DialogControlClass.Button, ApplyId, Tr.CommonApply, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsDefPushButton),
    ];

    protected override int InitialFocusId => EditId;

    protected override void OnInitialized()
    {
        SetItemText(EditId, _presenter.InitialText);
        var edit = Item(EditId);
        if (edit != 0)
        {
            User32.SendMessage(edit, DialogConstants.EmSetSel, 0, -1);
        }
    }

    protected override uint StaticTextColor(int id) => id == ErrorId ? DialogColors.Warning : base.StaticTextColor(id);

    protected override bool OnOk()
    {
        if (_presenter.TryAccept(GetItemText(EditId)) is { } error)
        {
            ErrorMessage = error;
            _errorVisible = true;
            SetItemText(ErrorId, error);
            ShowItem(ErrorId, true);
            var size = PreferredClientSize();
            ResizeClient(size.Width, size.Height);
            RunLayout();
            var edit = Item(EditId);
            if (edit != 0)
            {
                User32.SetFocus(edit);
                User32.SendMessage(edit, DialogConstants.EmSetSel, 0, -1);
            }

            return false;
        }

        Value = _presenter.Value;
        return true;
    }

    private int MarginX => Dx(12);

    private int MarginY => Dy(10);

    protected override (int Width, int Height) PreferredClientSize()
    {
        var width = Math.Max(Dx(200), ButtonRowWidth(CancelId, ApplyId) + 2 * MarginX);
        var content = width - 2 * MarginX;
        var height = MarginY + MeasureItemHeight(LabelId, content) + Dy(4) + EditHeight();
        if (_errorVisible)
        {
            height += Dy(4) + MeasureItemHeight(ErrorId, content);
        }

        height += Dy(10) + ButtonHeight() + MarginY;
        return (width, height);
    }

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var content = clientWidth - 2 * MarginX;
        var y = MarginY;
        var labelHeight = MeasureItemHeight(LabelId, content);
        Place(LabelId, MarginX, y, content, labelHeight);
        y += labelHeight + Dy(4);
        Place(EditId, MarginX, y, content, EditHeight());
        y += EditHeight();
        if (_errorVisible)
        {
            y += Dy(4);
            var errorHeight = MeasureItemHeight(ErrorId, content);
            Place(ErrorId, MarginX, y, content, errorHeight);
            y += errorHeight;
        }

        y += Dy(10);
        PlaceButtonRow(clientWidth, MarginX, y, ButtonHeight(), CancelId, ApplyId);
    }

    private int EditHeight() => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight() => Math.Max(Dy(14), LineHeight + Dy(6));
}

/// <summary>Kiểu con dùng chung của các điều khiển hộp thoại.</summary>
internal static class DialogStyles
{
    /// <summary>WS_CHILD | WS_VISIBLE.</summary>
    public const uint Child = WindowMessages.WsChild | WindowMessages.WsVisible;
}
