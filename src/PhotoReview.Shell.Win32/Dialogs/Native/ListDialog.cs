using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Nội dung của hộp thoại danh sách (SkippedFiles / BatchReview).</summary>
/// <param name="Heading">Tiêu đề lớn phía trên (null = không có).</param>
/// <param name="Intro">Đoạn dẫn nhiều dòng dưới tiêu đề (null = không có).</param>
/// <param name="IntroColorKey">Khoá màu <c>Dark.*</c> của đoạn dẫn.</param>
/// <param name="ConfirmText">Chữ nút xác nhận (IDOK); null = chỉ có nút đóng.</param>
/// <param name="CancelText">Chữ nút Huỷ/Đóng (IDCANCEL).</param>
/// <param name="InitialWidthDlu">Cỡ client ban đầu (dialog-unit).</param>
internal sealed record ListDialogContent(
    string Title,
    string? Heading,
    string? Intro,
    string IntroColorKey,
    IReadOnlyList<string> Lines,
    string? ConfirmText,
    string CancelText,
    int InitialWidthDlu,
    int InitialHeightDlu)
{
    /// <summary>Tệp bị bỏ qua (WPF: <c>SkippedFilesWindow</c>, 720 x 420, chỉ nút Đóng).</summary>
    public static ListDialogContent ForSkippedFiles(IReadOnlyList<SkippedEntry> entries) => new(
        SkippedFilesPresenter.Title,
        Heading: null,
        SkippedFilesPresenter.Intro,
        "Dark.Warning",
        SkippedFilesPresenter.BuildLines(entries),
        ConfirmText: null,
        SkippedFilesPresenter.CloseText,
        InitialWidthDlu: 470,
        InitialHeightDlu: 205);

    /// <summary>Xác nhận hàng loạt (WPF: <c>BatchReviewWindow</c>, 720 x 560, Huỷ + xác nhận).</summary>
    public static ListDialogContent ForBatchReview(IReadOnlyList<BatchReviewItem> items) => new(
        Tr.BatchReviewTitle,
        Heading: Tr.BatchReviewTitle,
        BatchReviewPresenter.Summary(items.Count),
        "Dark.TextSecondary",
        BatchReviewPresenter.BuildLines(items),
        Tr.BatchReviewConfirm,
        Tr.CommonCancel,
        InitialWidthDlu: 470,
        InitialHeightDlu: 280);
}

/// <summary>
/// Danh sách chỉ đọc + nút (view Win32 của <see cref="SkippedFilesPresenter"/> / <see cref="BatchReviewPresenter"/>). Kéo giãn được,
/// cuộn dọc + ngang (đường dẫn dài). Trả <see cref="NativeDialog.IdOk"/> chỉ khi bấm nút xác nhận (BatchReview).
/// </summary>
internal sealed class ListDialog : NativeDialog
{
    internal const int HeadingId = 300;
    internal const int IntroId = 301;
    internal const int ListId = 302;
    internal const int ConfirmId = IdOk;
    internal const int CancelId = IdCancel;

    private readonly ListDialogContent _content;

    public ListDialog(ListDialogContent content, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _content = content ?? throw new ArgumentNullException(nameof(content));
        Items = BuildItems();
    }

    protected override string Title => _content.Title;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override bool Resizable => true;

    protected override IReadOnlyList<int> HeaderControlIds => _content.Heading is null ? [] : [HeadingId];

    protected override int DefaultButtonId => _content.ConfirmText is null ? CancelId : ConfirmId;

    protected override int InitialFocusId => ListId;

    protected override uint StaticTextColor(int id)
        => id == IntroId ? DialogColors.FromKey(_content.IntroColorKey) : base.StaticTextColor(id);

    private List<DialogItem> BuildItems()
    {
        var items = new List<DialogItem>(5);
        if (_content.Heading is { } heading)
        {
            items.Add(new(DialogControlClass.Static, HeadingId, heading, DialogStyles.Child | DialogConstants.SsNoPrefix));
        }

        if (_content.Intro is { } intro)
        {
            items.Add(new(DialogControlClass.Static, IntroId, intro, DialogStyles.Child | DialogConstants.SsNoPrefix));
        }

        items.Add(new(DialogControlClass.ListBox, ListId, string.Empty,
            DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.WsVScroll | DialogConstants.WsHScroll
            | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight,
            DialogConstants.WsExClientEdge));
        items.Add(new(DialogControlClass.Button, CancelId, _content.CancelText,
            DialogStyles.Child | DialogConstants.WsTabStop
            | (_content.ConfirmText is null ? DialogConstants.BsDefPushButton : DialogConstants.BsPushButton)));
        if (_content.ConfirmText is { } confirm)
        {
            items.Add(new(DialogControlClass.Button, ConfirmId, confirm,
                DialogStyles.Child | DialogConstants.WsTabStop | DialogConstants.BsDefPushButton));
        }

        return items;
    }

    protected override unsafe void OnInitialized()
    {
        var list = Item(ListId);
        if (list == 0)
        {
            return;
        }

        User32.SendMessage(list, DialogConstants.LbInitStorage, _content.Lines.Count, (nint)_content.Lines.Count * 256);
        var widest = 0;
        foreach (var line in _content.Lines)
        {
            fixed (char* text = line)
            {
                User32.SendMessage(list, DialogConstants.LbAddString, 0, (nint)text);
            }

            widest = Math.Max(widest, MeasureTextWidth(line));
        }

        User32.SendMessage(list, DialogConstants.LbSetHorizontalExtent, widest + Dx(8), 0);
    }

    private int MarginX => Dx(10);

    private int MarginY => Dy(8);

    private int ButtonHeight() => Math.Max(Dy(14), LineHeight + Dy(6));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(_content.InitialWidthDlu), Dy(_content.InitialHeightDlu));

    protected override (int Width, int Height) MinimumClientSize()
    {
        var buttons = _content.ConfirmText is null ? ButtonRowWidth(CancelId) : ButtonRowWidth(CancelId, ConfirmId);
        return (Math.Max(Dx(300), buttons + 2 * MarginX), Dy(150));
    }

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var content = Math.Max(1, clientWidth - 2 * MarginX);
        var y = MarginY;
        if (_content.Heading is not null)
        {
            var heading = MeasureItemHeight(HeadingId, content);
            Place(HeadingId, MarginX, y, content, heading);
            y += heading + Dy(3);
        }

        if (_content.Intro is not null)
        {
            var intro = MeasureItemHeight(IntroId, content);
            Place(IntroId, MarginX, y, content, intro);
            y += intro + Dy(6);
        }

        var buttonHeight = ButtonHeight();
        var buttonTop = clientHeight - MarginY - buttonHeight;
        Place(ListId, MarginX, y, content, Math.Max(Dy(20), buttonTop - Dy(6) - y));
        if (_content.ConfirmText is null)
        {
            PlaceButtonRow(clientWidth, MarginX, buttonTop, buttonHeight, CancelId);
        }
        else
        {
            PlaceButtonRow(clientWidth, MarginX, buttonTop, buttonHeight, CancelId, ConfirmId);
        }
    }
}
