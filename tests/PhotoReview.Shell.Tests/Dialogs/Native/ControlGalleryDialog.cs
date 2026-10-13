using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ2: hộp thoại thử CHỈ cho test/ảnh chụp - một bản của mọi điều khiển mới của khung NativeDialog (combobox, listbox đa chọn
/// owner-draw có huy hiệu, listview report có sắp xếp + tô hàng, progress, trackbar, edit nhiều dòng/số, checkbox, radio, group box,
/// tooltip). Dùng để kiểm "dark" bằng ảnh chụp và hành vi điều khiển qua thông điệp thật.
/// </summary>
internal sealed unsafe class ControlGalleryDialog : NativeDialog
{
    public const int NameLabel = 100;
    public const int Combo = 101;
    public const int BadgeList = 102;
    public const int Report = 103;
    public const int Progress = 104;
    public const int Slider = 105;
    public const int Notes = 106;
    public const int Count = 107;
    public const int Check = 108;
    public const int RadioA = 109;
    public const int RadioB = 110;
    public const int Group = 111;
    public const int TipButton = 112;
    public const int SliderValue = 113;

    private readonly List<(string Name, string Badge)> _badges =
    [
        ("Fast Sequential", "Fast"), ("Preview Light", "Preview"), ("Original Correctness", "Original"), ("Rapid Key Press", "Preview"),
    ];

    private readonly List<string[]> _rows =
    [
        ["", "Fast Sequential", "Fast", "Sequential", "32", "12 ms", "18 ms", "22 ms", "31 ms", "Pass"],
        ["🏆", "Preview Light", "Preview", "Sequential", "32", "9 ms", "11 ms", "14 ms", "15 ms", "Pass"],
        ["", "Original Correctness", "Original", "Correctness", "3", "120 ms", "140 ms", "150 ms", "151 ms", "Fail"],
        ["", "Rapid Key Press", "Preview", "WarmNext", "16", "20 ms", "26 ms", "30 ms", "33 ms", "Warn"],
    ];

    private int _sortColumn = -1;
    private int _sortDirection;

    public ControlGalleryDialog()
    {
        Items = BuildItems();
    }

    public List<(int Id, int Code)> Notifications { get; } = [];

    public List<(int Id, int Code)> Scrolls { get; } = [];

    public List<int> ColumnClicks { get; } = [];

    public int LastCommandId { get; private set; }

    protected override string Title => "Control gallery";

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override bool Resizable => true;

    protected override (int Width, int Height) PreferredClientSize() => (Dx(420), Dy(270));

    protected override (int Width, int Height) MinimumClientSize() => (Dx(300), Dy(200));

    private static List<DialogItem> BuildItems()
    {
        const uint child = DialogStyles.Child;
        const uint tab = DialogConstants.WsTabStop;
        return
        [
            new(DialogControlClass.Static, NameLabel, "Operation", child | DialogConstants.SsNoPrefix),
            new(DialogControlClass.ComboBox, Combo, string.Empty, child | ComboBoxControl.DefaultStyle),
            new(DialogControlClass.ListBox, BadgeList, string.Empty,
                child | tab | DialogConstants.WsVScroll | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight
                | ControlConstants.LbsMultipleSel | ControlConstants.LbsOwnerDrawFixed | ControlConstants.LbsHasStrings,
                DialogConstants.WsExClientEdge),
            new(DialogControlClass.ListView, Report, string.Empty, child | ListViewControl.DefaultStyle),
            new(DialogControlClass.Progress, Progress, string.Empty, child | ProgressControl.DefaultStyle),
            new(DialogControlClass.Trackbar, Slider, string.Empty, child | TrackbarControl.DefaultStyle),
            new(DialogControlClass.Static, SliderValue, "0", child | DialogConstants.SsNoPrefix),
            new(DialogControlClass.Edit, Notes, "Line one\r\nLine two",
                child | tab | DialogConstants.WsVScroll | ControlConstants.EsMultiline | ControlConstants.EsAutoVScroll
                | ControlConstants.EsWantReturn, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Edit, Count, "64",
                child | tab | DialogConstants.EsAutoHScroll | ControlConstants.EsNumber, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, Group, "Options", child | ControlConstants.BsGroupBox),
            new(DialogControlClass.Button, Check, "Confirm before running", child | tab | ControlConstants.BsAutoCheckBox),
            new(DialogControlClass.Button, RadioA, "Move", child | tab | DialogConstants.WsGroup | ControlConstants.BsAutoRadioButton),
            new(DialogControlClass.Button, RadioB, "Copy", child | ControlConstants.BsAutoRadioButton),
            new(DialogControlClass.Button, 120, "classic", child | tab | ControlConstants.BsAutoRadioButton),
            new(DialogControlClass.Button, 121, "cfd", child | tab | ControlConstants.BsAutoRadioButton),
            new(DialogControlClass.Button, 122, "explorer", child | tab | ControlConstants.BsAutoRadioButton),
            new(DialogControlClass.Button, 123, "checkbox classic", child | tab | ControlConstants.BsAutoCheckBox),
            new(DialogControlClass.Button, TipButton, "Hover me", child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, IdCancel, "Cancel", child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, IdOk, "OK", child | tab | DialogConstants.BsDefPushButton),
        ];
    }

    protected override IReadOnlyList<int> HeaderControlIds => [];

    protected override void OnInitialized()
    {
        var combo = Item(Combo);
        ComboBoxControl.Add(combo, "Move");
        ComboBoxControl.Add(combo, "Copy");
        ComboBoxControl.Add(combo, "Recycle");
        ComboBoxControl.Select(combo, 0);

        var list = Item(BadgeList);
        foreach (var (name, _) in _badges)
        {
            ListBoxControl.Add(list, name);
        }

        ListBoxControl.SetSelected(list, 1, true);

        var report = Item(Report);
        ListViewControl.Configure(report, DialogColors.FromKey("Dark.Window"), DialogColors.Text);
        var headers = new[] { "🏆", "Profile", "Loading", "Workload", "N", "P50", "P95", "P99", "Max", "Status" };
        for (var i = 0; i < headers.Length; i++)
        {
            ListViewControl.AddColumn(report, i, headers[i], Dx(40), rightAligned: i is >= 4 and <= 8);
        }

        FillRows();

        ProgressControl.Configure(Item(Progress));
        ProgressControl.SetFraction(Item(Progress), 0.4);
        TrackbarControl.Configure(Item(Slider), 0, 100, 10, 1, 10);
        TrackbarControl.SetPosition(Item(Slider), 30);
        SetItemText(SliderValue, "30");
        PhotoReview.Shell.Interop.Uxtheme.SetWindowTheme(Item(120), string.Empty, string.Empty);
        PhotoReview.Shell.Interop.Uxtheme.SetWindowTheme(Item(121), "DarkMode_CFD", null);
        PhotoReview.Shell.Interop.Uxtheme.SetWindowTheme(Item(122), "Explorer", null);
        PhotoReview.Shell.Interop.Uxtheme.SetWindowTheme(Item(123), string.Empty, string.Empty);
        ButtonStateControl.SetChecked(Item(Check), true);
        ButtonStateControl.SetChecked(Item(RadioA), true);
        SetTooltip(TipButton, "A dark tooltip for a plain button");
        SetItemTooltips(BadgeList, index => index >= 0 && index < _badges.Count ? "Mode: " + _badges[index].Badge : null);
    }

    private void FillRows()
    {
        var report = Item(Report);
        ListViewControl.Clear(report);
        for (var i = 0; i < _rows.Count; i++)
        {
            ListViewControl.AddRow(report, _rows[i], i);
        }
    }

    /// <summary>Sắp xếp theo cột (như click header): lần đầu tăng, lần hai giảm.</summary>
    public void SortBy(int column)
    {
        _sortDirection = _sortColumn == column && _sortDirection == 1 ? 2 : 1;
        _sortColumn = column;
        var ordered = _sortDirection == 1
            ? _rows.OrderBy(r => r[column], StringComparer.CurrentCulture).ToList()
            : _rows.OrderByDescending(r => r[column], StringComparer.CurrentCulture).ToList();
        _rows.Clear();
        _rows.AddRange(ordered);
        FillRows();
        SetListViewSort(Report, column, _sortDirection);
    }

    protected override (string Text, string? Badge) ListItemText(int id, int index)
        => id == BadgeList && index < _badges.Count ? (_badges[index].Name, _badges[index].Badge) : base.ListItemText(id, index);

    protected override bool OnNotify(int id, int code, nint header, out nint result)
    {
        Notifications.Add((id, code));
        result = 0;
        if (id != Report)
        {
            return false;
        }

        if (code == ControlConstants.NmCustomDraw)
        {
            result = ListViewControl.CustomDraw(header, (row, param) =>
            {
                var data = _rows[(int)param];
                var back = data[0].Length > 0
                    ? DialogColors.FromKey("Dark.SuccessBackground")
                    : data[9] == "Fail"
                        ? DialogColors.FromKey("Dark.ErrorBackground")
                        : row % 2 == 0 ? DialogColors.FromKey("Dark.Panel") : DialogColors.FromKey("Dark.RowAlt");
                return (back, DialogColors.Text);
            });
            return true;
        }

        if (code == ControlConstants.LvnColumnClick)
        {
            var column = *(int*)(header + 28);
            ColumnClicks.Add(column);
            SortBy(column);
            return true;
        }

        return false;
    }

    protected override void OnScroll(int id, int code)
    {
        Scrolls.Add((id, code));
        if (id == Slider)
        {
            SetItemText(SliderValue, TrackbarControl.Position(Item(Slider)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    protected override bool OnCommand(int id, int notification)
    {
        LastCommandId = id;
        return false;
    }

    private int Margin => Dx(8);

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var m = Margin;
        var line = Math.Max(Dy(12), LineHeight + Dy(4));
        var buttonHeight = Math.Max(Dy(14), LineHeight + Dy(6));
        var leftWidth = Math.Max(Dx(120), clientWidth / 3);
        var rightX = m + leftWidth + m;
        var rightWidth = Math.Max(Dx(60), clientWidth - rightX - m);

        var y = m;
        Place(NameLabel, m, y, leftWidth, LineHeight);
        y += LineHeight + Dy(2);
        Place(Combo, m, y, leftWidth, line + (4 * (LineHeight + Dy(2))));
        y += line + Dy(6);
        SetListItemHeight(BadgeList, LineHeight + Dy(5));
        Place(BadgeList, m, y, leftWidth, Dy(60));
        y += Dy(60) + Dy(6);
        Place(Slider, m, y, leftWidth - Dx(20), Dy(18));
        Place(SliderValue, m + leftWidth - Dx(18), y + Dy(3), Dx(18), LineHeight);
        y += Dy(18) + Dy(4);
        Place(Progress, m, y, leftWidth, Dy(6));
        y += Dy(6) + Dy(6);
        Place(Count, m, y, Dx(50), line);
        y += line + Dy(4);
        Place(Notes, m, y, leftWidth, Dy(40));

        var buttonTop = clientHeight - m - buttonHeight;
        Place(Report, rightX, m, rightWidth, Dy(90));
        var widths = new[] { 14, 52, 28, 34, 14, 22, 22, 22, 22, 30 };
        for (var i = 0; i < widths.Length; i++)
        {
            ListViewControl.SetColumnWidth(Item(Report), i, Dx(widths[i]));
        }

        var groupTop = m + Dy(90) + Dy(6);
        Place(Group, rightX, groupTop, rightWidth, Dy(60));
        Place(Check, rightX + Dx(8), groupTop + Dy(14), rightWidth - Dx(16), line);
        Place(RadioA, rightX + Dx(8), groupTop + Dy(14) + line, Dx(60), line);
        Place(RadioB, rightX + Dx(8) + Dx(64), groupTop + Dy(14) + line, Dx(60), line);
        Place(120, rightX + Dx(8), groupTop + Dy(14) + (2 * line), Dx(60), line);
        Place(121, rightX + Dx(8) + Dx(64), groupTop + Dy(14) + (2 * line), Dx(60), line);
        Place(122, rightX + Dx(8) + Dx(128), groupTop + Dy(14) + (2 * line), Dx(60), line);
        Place(123, rightX + Dx(8) + Dx(128), groupTop + Dy(14) + line, Dx(80), line);
        Place(TipButton, rightX, groupTop + Dy(66), Dx(70), buttonHeight);
        PlaceButtonRow(clientWidth, m, buttonTop, buttonHeight, IdCancel, IdOk);
    }
}
