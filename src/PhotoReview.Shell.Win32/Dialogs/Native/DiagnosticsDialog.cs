using System.Text;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// Chẩn đoán hiệu năng, chỉ đọc: 15 cặp nhãn/giá trị (view Win32 của <see cref="DiagnosticsPresenter"/>; WPF:
/// <c>DiagnosticsWindow</c>). Thêm so với bản WPF: nút "Sao chép" (và Ctrl+C không cần) đưa toàn bộ bảng vào clipboard.
/// </summary>
internal sealed class DiagnosticsDialog : NativeDialog
{
    internal const int HeaderId = 200;
    internal const int NoteId = 201;
    internal const int FirstLabelId = 210;
    internal const int FirstValueId = 240;
    internal const int CopyId = 270;
    internal const int CloseId = IdCancel;
    internal const int RowCount = 15;

    private readonly DiagnosticsDisplay _display;
    private readonly Func<string, bool>? _copy;

    /// <param name="copy">Đưa văn bản vào clipboard (true = thành công); null = nút Sao chép không làm gì.</param>
    public DiagnosticsDialog(DiagnosticsDisplay display, Func<string, bool>? copy = null, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _copy = copy;
        Items = BuildItems();
    }

    /// <summary>Số lần nút Sao chép đã đưa văn bản vào clipboard thành công.</summary>
    public int CopyCount { get; private set; }

    protected override string Title => Tr.DiagnosticsTitle;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override IReadOnlyList<int> HeaderControlIds { get; } = [HeaderId];

    protected override int InitialFocusId => CloseId;

    protected override int DefaultButtonId => CloseId;

    /// <summary>Nhãn của hàng <paramref name="row"/> (0..14) theo đúng thứ tự cột nhãn của bản WPF.</summary>
    internal static string LabelText(int row) => row switch
    {
        0 => Tr.DiagnosticsLabelCacheHits,
        1 => Tr.DiagnosticsLabelCacheMisses,
        2 => Tr.DiagnosticsLabelCacheHitRate,
        3 => Tr.DiagnosticsLabelSourceBytesRead,
        4 => Tr.DiagnosticsLabelSourceFileReads,
        5 => Tr.DiagnosticsLabelDecodeTime,
        6 => Tr.DiagnosticsLabelKeyToPresentLatency,
        7 => Tr.DiagnosticsLabelExplorerOrder,
        8 => Tr.DiagnosticsLabelExplorerSortGroup,
        9 => Tr.DiagnosticsLabelSourceOpens,
        10 => Tr.DiagnosticsLabelMetadataQueries,
        11 => Tr.DiagnosticsLabelSessionWrites,
        12 => Tr.DiagnosticsLabelDecoderFallbacks,
        13 => Tr.DiagnosticsLabelPresentLatencyHistogram,
        14 => Tr.DiagnosticsLabelCrossThreadPresents,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>Giá trị của hàng <paramref name="row"/> (cùng thứ tự với <see cref="LabelText"/>).</summary>
    internal static string ValueText(DiagnosticsDisplay display, int row) => row switch
    {
        0 => display.CacheHits,
        1 => display.CacheMisses,
        2 => display.HitRate,
        3 => display.SourceBytes,
        4 => display.SourceReads,
        5 => display.DecodeTime,
        6 => display.PresentLatency,
        7 => display.ExplorerOrder,
        8 => display.ExplorerMetadata,
        9 => display.SourceOpens,
        10 => display.StatCount,
        11 => display.SessionWrites,
        12 => display.DecoderFallbacks,
        13 => display.PresentHistogram,
        14 => display.CrossThreadPresents,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>Toàn bộ bảng dạng "nhãn&lt;TAB&gt;giá trị" mỗi dòng (nội dung nút Sao chép).</summary>
    internal static string BuildCopyText(DiagnosticsDisplay display)
    {
        var text = new StringBuilder();
        for (var row = 0; row < RowCount; row++)
        {
            text.Append(LabelText(row)).Append('\t').Append(ValueText(display, row)).Append(Environment.NewLine);
        }

        return text.ToString();
    }

    protected override void OnInitialized()
    {
    }

    protected override bool OnCommand(int id, int notification)
    {
        if (id == CopyId && notification == Interop.DialogConstants.BnClicked)
        {
            if (_copy?.Invoke(BuildCopyText(_display)) == true)
            {
                CopyCount++;
            }

            return true;
        }

        return false;
    }

    protected override uint StaticTextColor(int id) => id switch
    {
        NoteId => DialogColors.FromKey("Dark.TextSubtle"),
        _ => base.StaticTextColor(id),
    };

    private List<DialogItem> BuildItems()
    {
        var items = new List<DialogItem>(2 * RowCount + 4)
        {
            new(DialogControlClass.Static, HeaderId, Tr.DiagnosticsHeader, DialogStyles.Child | Interop.DialogConstants.SsNoPrefix),
            new(DialogControlClass.Static, NoteId, Tr.DiagnosticsPrivacyNote, DialogStyles.Child | Interop.DialogConstants.SsNoPrefix),
        };
        for (var row = 0; row < RowCount; row++)
        {
            items.Add(new(DialogControlClass.Static, FirstLabelId + row, LabelText(row), DialogStyles.Child | Interop.DialogConstants.SsNoPrefix));
            items.Add(new(DialogControlClass.Static, FirstValueId + row, ValueText(_display, row), DialogStyles.Child | Interop.DialogConstants.SsNoPrefix));
        }

        items.Add(new(DialogControlClass.Button, CopyId, Tr.DiagnosticsCopy,
            DialogStyles.Child | Interop.DialogConstants.WsTabStop | Interop.DialogConstants.BsPushButton));
        items.Add(new(DialogControlClass.Button, CloseId, Tr.CommonClose,
            DialogStyles.Child | Interop.DialogConstants.WsTabStop | Interop.DialogConstants.BsDefPushButton));
        return items;
    }

    private int MarginX => Dx(14);

    private int MarginY => Dy(12);

    private int LabelWidth => Dx(125);

    private int ColumnGap => Dx(6);

    protected override (int Width, int Height) PreferredClientSize()
    {
        var width = Dx(400);
        var heights = RowHeights(width);
        var height = MarginY + MeasureItemHeight(HeaderId, width - 2 * MarginX) + Dy(3)
            + MeasureItemHeight(NoteId, width - 2 * MarginX) + Dy(10);
        foreach (var row in heights)
        {
            height += row + Dy(3);
        }

        height += Dy(8) + ButtonHeight() + MarginY;
        return (width, height);
    }

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var content = clientWidth - 2 * MarginX;
        var y = MarginY;
        var headerHeight = MeasureItemHeight(HeaderId, content);
        Place(HeaderId, MarginX, y, content, headerHeight);
        y += headerHeight + Dy(3);
        var noteHeight = MeasureItemHeight(NoteId, content);
        Place(NoteId, MarginX, y, content, noteHeight);
        y += noteHeight + Dy(10);
        var valueX = MarginX + LabelWidth + ColumnGap;
        var valueWidth = clientWidth - MarginX - valueX;
        for (var row = 0; row < RowCount; row++)
        {
            var height = Math.Max(MeasureItemHeight(FirstLabelId + row, LabelWidth), MeasureItemHeight(FirstValueId + row, valueWidth));
            Place(FirstLabelId + row, MarginX, y, LabelWidth, height);
            Place(FirstValueId + row, valueX, y, valueWidth, height);
            y += height + Dy(3);
        }

        y += Dy(8);
        PlaceButtonRow(clientWidth, MarginX, y, ButtonHeight(), CopyId, CloseId);
    }

    private int[] RowHeights(int clientWidth)
    {
        var valueWidth = clientWidth - 2 * MarginX - LabelWidth - ColumnGap;
        var heights = new int[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            heights[row] = Math.Max(MeasureItemHeight(FirstLabelId + row, LabelWidth), MeasureItemHeight(FirstValueId + row, valueWidth));
        }

        return heights;
    }

    private int ButtonHeight() => Math.Max(Dy(14), LineHeight + Dy(6));
}
