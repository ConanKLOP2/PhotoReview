using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// FEAT-EXTERNAL-EDITORS: hộp thoại nhập một dòng chữ (tên hiển thị của trình chỉnh sửa vừa thêm): nhãn + ô nhập + Apply/Cancel.
/// Cách làm theo <see cref="ZoomPromptDialog"/>. Ô trống bị từ chối (hộp thoại giữ mở); Enter = Apply, Esc = Huỷ.
/// </summary>
internal sealed class TextPromptDialog : NativeDialog
{
    internal const int LabelId = 100;
    internal const int EditId = 101;
    internal const int CancelId = IdCancel;
    internal const int ApplyId = IdOk;

    private readonly string _title;
    private readonly string _initial;

    public TextPromptDialog(string title, string label, string initial, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _title = title;
        _initial = initial ?? string.Empty;
        Items =
        [
            new(DialogControlClass.Static, LabelId, label, DialogStyles.Child | DialogConstants.SsNoPrefix),
            new(DialogControlClass.Edit, EditId, string.Empty,
                DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.EsAutoHScroll, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, CancelId, Tr.CommonCancel, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ApplyId, Tr.CommonApply, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsDefPushButton),
        ];
    }

    /// <summary>Chữ đã nhập (đã cắt khoảng trắng hai đầu) sau khi đóng bằng Apply; null khi huỷ.</summary>
    public string? Value { get; private set; }

    protected override string Title => _title;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override int InitialFocusId => EditId;

    protected override void OnInitialized()
    {
        SetItemText(EditId, _initial);
        var edit = Item(EditId);
        if (edit != 0)
        {
            User32.SendMessage(edit, DialogConstants.EmSetSel, 0, -1);
        }
    }

    protected override bool OnOk()
    {
        var text = GetItemText(EditId).Trim();
        if (text.Length == 0)
        {
            var edit = Item(EditId);
            if (edit != 0)
            {
                User32.SetFocus(edit);
            }

            return false;
        }

        Value = text;
        return true;
    }

    private int MarginX => Dx(12);

    private int MarginY => Dy(10);

    protected override (int Width, int Height) PreferredClientSize()
    {
        var width = Math.Max(Dx(220), ButtonRowWidth(CancelId, ApplyId) + 2 * MarginX);
        var content = width - 2 * MarginX;
        var height = MarginY + MeasureItemHeight(LabelId, content) + Dy(4) + EditHeight() + Dy(10) + ButtonHeight() + MarginY;
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
        y += EditHeight() + Dy(10);
        PlaceButtonRow(clientWidth, MarginX, y, ButtonHeight(), CancelId, ApplyId);
    }

    private int EditHeight() => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight() => Math.Max(Dy(14), LineHeight + Dy(6));
}
