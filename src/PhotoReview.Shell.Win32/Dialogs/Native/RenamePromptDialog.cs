using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// FEAT-RENAME (PR3): hộp nhập tên mới của "Rename..." / F2. Một ô đơn dòng điền sẵn TÊN KHÔNG PHẦN MỞ RỘNG (chọn sẵn toàn bộ),
/// phần mở rộng hiện cố định bên phải ô. Tên được kiểm NGAY khi gõ qua <see cref="RenamePromptRequest.Validate"/>: lỗi hiện tại chỗ
/// dưới ô và nút "Rename" bị tắt; lần xác nhận cuối (<c>isCommit</c>) được phép hỏi cả đĩa. Enter = Rename (khi hợp lệ),
/// Esc/X/Alt+F4 = huỷ. Cách làm theo <see cref="ZoomPromptDialog"/> / <see cref="TextPromptDialog"/>.
/// </summary>
internal sealed class RenamePromptDialog : NativeDialog
{
    internal const int LabelId = 100;
    internal const int EditId = 101;
    internal const int ExtensionId = 102;
    internal const int ErrorId = 103;
    internal const int CancelId = IdCancel;
    internal const int RenameId = IdOk;

    private readonly RenamePromptRequest _request;
    private bool _errorVisible;

    public RenamePromptDialog(RenamePromptRequest request, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        Items =
        [
            new(DialogControlClass.Static, LabelId, Tr.DialogRenameLabel, DialogStyles.Child | DialogConstants.SsNoPrefix),
            new(DialogControlClass.Edit, EditId, string.Empty,
                DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.EsAutoHScroll, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, ExtensionId, request.Extension, DialogStyles.Child | DialogConstants.SsNoPrefix),
            new(DialogControlClass.Static, ErrorId, string.Empty, WindowMessages.WsChild | DialogConstants.SsNoPrefix),
            new(DialogControlClass.Button, CancelId, Tr.CommonCancel, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, RenameId, Tr.DialogRenameOk, DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsDefPushButton),
        ];
    }

    /// <summary>Chữ đã nhập (cắt khoảng trắng hai đầu) sau khi đóng bằng nút Rename; null khi huỷ.</summary>
    public string? Value { get; private set; }

    /// <summary>Lỗi đang hiện dưới ô (null khi không có).</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Nút Rename đang bật (tên đang gõ hợp lệ).</summary>
    public bool IsRenameEnabled
    {
        get
        {
            var button = Item(RenameId);
            return button != 0 && User32.IsWindowEnabled(button);
        }
    }

    /// <summary>Phần chọn hiện tại của ô nhập (test): chỉ số bắt đầu/kết thúc.</summary>
    public (int Start, int End) Selection
    {
        get
        {
            var edit = Item(EditId);
            if (edit == 0)
            {
                return (0, 0);
            }

            var packed = (long)User32.SendMessage(edit, DialogConstants.EmGetSel, 0, 0);
            return ((int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF));
        }
    }

    /// <summary>Chữ đang có trong ô nhập (test).</summary>
    public string CurrentText => GetItemText(EditId);

    /// <summary>Phần mở rộng đang hiện bên phải ô (test).</summary>
    public string ExtensionText => GetItemText(ExtensionId);

    protected override string Title => Tr.DialogRenameTitle;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override int InitialFocusId => EditId;

    protected override void OnInitialized()
    {
        SetItemText(EditId, _request.InitialName);
        var edit = Item(EditId);
        if (edit != 0)
        {
            User32.SendMessage(edit, DialogConstants.EmSetSel, 0, -1);
        }

        Revalidate();
    }

    protected override uint StaticTextColor(int id) => id switch
    {
        ErrorId => DialogColors.Warning,
        ExtensionId => DialogColors.TextSecondary,
        _ => base.StaticTextColor(id),
    };

    protected override bool OnCommand(int id, int notification)
    {
        if (id == EditId && notification == DialogConstants.EnChange)
        {
            Revalidate();
            return true;
        }

        return false;
    }

    protected override bool OnOk()
    {
        var text = GetItemText(EditId).Trim();
        // The confirming call may also look at the disk (a file the listing does not know).
        if (_request.Validate(text, true) is { } error)
        {
            ShowError(error);
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

    /// <summary>Kiểm tên đang gõ (không đụng đĩa), cập nhật dòng lỗi và nút Rename.</summary>
    private void Revalidate()
    {
        var text = GetItemText(EditId).Trim();
        ShowError(_request.Validate(text, false));
    }

    private void ShowError(string? error)
    {
        var changed = !string.Equals(error, ErrorMessage, StringComparison.Ordinal);
        ErrorMessage = error;
        var visible = !string.IsNullOrEmpty(error);
        var button = Item(RenameId);
        if (button != 0)
        {
            User32.EnableWindow(button, !visible);
        }

        if (!changed)
        {
            return;
        }

        SetItemText(ErrorId, error ?? string.Empty);
        ShowItem(ErrorId, visible);
        _errorVisible = visible;
        var size = PreferredClientSize();
        ResizeClient(size.Width, size.Height);
        RunLayout();
    }

    private int MarginX => Dx(12);

    private int MarginY => Dy(10);

    private int ExtensionWidth => _request.Extension.Length == 0 ? 0 : MeasureTextWidth(_request.Extension) + Dx(4);

    protected override (int Width, int Height) PreferredClientSize()
    {
        var width = Math.Max(Dx(260), ButtonRowWidth(CancelId, RenameId) + 2 * MarginX);
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

        var extension = ExtensionWidth;
        var gap = extension == 0 ? 0 : Dx(3);
        Place(EditId, MarginX, y, Math.Max(Dx(40), content - extension - gap), EditHeight());
        if (extension > 0)
        {
            // Vertically centred on the box: the label's text row, not its full cell.
            var textHeight = Math.Min(EditHeight(), LineHeight + Dy(2));
            Place(ExtensionId, MarginX + content - extension, y + ((EditHeight() - textHeight) / 2), extension, textHeight);
        }
        else
        {
            Place(ExtensionId, MarginX, y, 0, 0);
        }

        y += EditHeight();
        if (_errorVisible)
        {
            y += Dy(4);
            var errorHeight = MeasureItemHeight(ErrorId, content);
            Place(ErrorId, MarginX, y, content, errorHeight);
            y += errorHeight;
        }

        y += Dy(10);
        PlaceButtonRow(clientWidth, MarginX, y, ButtonHeight(), CancelId, RenameId);
    }

    private int EditHeight() => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight() => Math.Max(Dy(14), LineHeight + Dy(6));
}
