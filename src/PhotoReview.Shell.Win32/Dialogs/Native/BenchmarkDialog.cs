using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using PhotoReview.App;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// WP-35 GĐ2 (phương án B, CHƯA XÁC NHẬN): cửa sổ Benchmark MODELESS - view Win32 của <see cref="BenchmarkPresenter"/> (WPF:
/// <c>BenchmarkWindow</c>). Không chặn shell: Tab/Esc/Enter do <c>IsDialogMessage</c> trong vòng lặp shell; tiến độ từ luồng nền
/// được presenter đẩy về UI qua <see cref="IUiScheduler"/>; đóng giữa chừng = <see cref="BenchmarkPresenter.Dispose"/> (huỷ lượt chạy).
/// Presenter lo mọi quy tắc (danh sách profile, chạy/huỷ, xếp hạng, báo cáo); view chỉ chép giá trị vào điều khiển.
/// </summary>
internal sealed unsafe class BenchmarkDialog : NativeDialog, IBenchmarkView, IDisposable
{
    internal const int HeaderId = 100;
    internal const int DescriptionId = 101;
    internal const int FolderId = 102;
    internal const int BrowseId = 103;
    internal const int LimitLabelId = 104;
    internal const int LimitId = 105;
    internal const int ProfilesLabelId = 106;
    internal const int SelectAllId = 107;
    internal const int SelectNoneId = 108;
    internal const int ProfilesId = 109;
    internal const int RunId = 110;
    internal const int QuickCheckId = 111;
    internal const int CancelRunId = 112;
    internal const int ProgressId = 113;
    internal const int StatusId = 114;
    internal const int ResultsId = 115;
    internal const int OpenReportId = 116;
    internal const int OpenFolderId = 117;
    internal const int CloseId = 118;

    /// <summary>Số cột của bảng kết quả (khớp <see cref="BenchmarkResultRow.CellTexts"/>).</summary>
    internal const int ColumnCount = 10;

    private readonly BenchmarkPresenter _presenter;
    private readonly IUiScheduler _ui;
    private readonly Func<nint, string, string?, string?> _pickFolder;
    private readonly Action<string> _openInExplorer;
    private readonly string _initialFolder;
    private readonly EventHandler _languageHandler;
    private int _sortColumn = -1;
    private int _sortDirection;
    private bool _running;
    private bool _textsApplied;

    /// <param name="initialFolder">Thư mục ban đầu của ô thư mục (null = Pictures).</param>
    /// <param name="ui">Bộ lập lịch UI (tiến độ từ luồng nền).</param>
    /// <param name="sessionFactory">Tạo phiên đo cho một profile.</param>
    /// <param name="pickFolder">Hộp thoại chọn thư mục (owner, tiêu đề, thư mục đầu) -&gt; thư mục hoặc null.</param>
    /// <param name="openInExplorer">Mở Explorer với tham số (null = <c>explorer.exe</c> thật).</param>
    /// <param name="scan">Quét thư mục (null = thật).</param>
    /// <param name="save">Lưu báo cáo (null = thật).</param>
    public BenchmarkDialog(string? initialFolder, IUiScheduler ui, BenchmarkSessionFactory sessionFactory,
        Func<nint, string, string?, string?>? pickFolder = null, Action<string>? openInExplorer = null,
        BenchmarkFileScan? scan = null, BenchmarkSummarySave? save = null, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        _pickFolder = pickFolder ?? ((_, _, _) => null);
        _openInExplorer = openInExplorer ?? StartExplorer;
        _presenter = new BenchmarkPresenter(this, ui, sessionFactory, scan, save);
        _initialFolder = BenchmarkPresenter.InitialFolder(initialFolder);
        _languageHandler = (_, _) => _ui.Post(OnLanguageChanged);
        Items = BuildItems();
    }

    /// <summary>Huỷ lượt chạy đang có và giải phóng presenter (cũng được gọi khi cửa sổ đóng).</summary>
    public void Dispose() => _presenter.Dispose();

    /// <summary>Presenter (test: trạng thái chạy, hàng kết quả).</summary>
    internal BenchmarkPresenter Presenter => _presenter;

    /// <summary>Chỉ số cột đang sắp xếp (-1 = thứ tự của presenter) và hướng (0 không, 1 tăng, 2 giảm).</summary>
    internal (int Column, int Direction) Sort => (_sortColumn, _sortDirection);

    protected override string Title => Tr.BenchmarkWindowTitle;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override bool Resizable => true;

    protected override bool UsesModalFrame => false;

    protected override uint ExtraWindowStyle => WindowMessages.WsMinimizeBox | WindowMessages.WsMaximizeBox;

    protected override IReadOnlyList<int> HeaderControlIds => [HeaderId];

    protected override int InitialFocusId => RunId;

    protected override int DefaultButtonId => RunId;

    private static List<DialogItem> BuildItems()
    {
        const uint child = DialogStyles.Child;
        const uint tab = DialogConstants.WsTabStop;
        const uint text = DialogStyles.Child | DialogConstants.SsNoPrefix;
        const uint edit = child | tab | DialogConstants.EsAutoHScroll;
        return
        [
            new(DialogControlClass.Static, HeaderId, Tr.BenchmarkHeader, text),
            new(DialogControlClass.Static, DescriptionId, Tr.BenchmarkDescription, text),
            new(DialogControlClass.Edit, FolderId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, BrowseId, Tr.BenchmarkBrowse, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, LimitLabelId, Tr.BenchmarkImageLimitLabel, text),
            new(DialogControlClass.Edit, LimitId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, ProfilesLabelId, Tr.BenchmarkProfilesLabel, text),
            new(DialogControlClass.Button, SelectAllId, Tr.BenchmarkSelectAll, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, SelectNoneId, Tr.BenchmarkSelectNone, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.ListBox, ProfilesId, string.Empty,
                child | tab | DialogConstants.WsVScroll | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight
                | ControlConstants.LbsMultipleSel | ControlConstants.LbsOwnerDrawFixed | ControlConstants.LbsHasStrings,
                DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, RunId, Tr.BenchmarkRun, child | tab | DialogConstants.BsDefPushButton),
            new(DialogControlClass.Button, QuickCheckId, Tr.BenchmarkQuickCheck, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, CancelRunId, Tr.CommonCancel, child | tab | DialogConstants.BsPushButton | DialogConstants.WsDisabled),
            new(DialogControlClass.Progress, ProgressId, string.Empty, child | ProgressControl.DefaultStyle),
            new(DialogControlClass.Static, StatusId, string.Empty, text),
            new(DialogControlClass.ListView, ResultsId, string.Empty, child | ListViewControl.DefaultStyle),
            new(DialogControlClass.Button, OpenReportId, Tr.BenchmarkOpenReport, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, OpenFolderId, Tr.BenchmarkOpenResultFolder, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, CloseId, Tr.CommonClose, child | tab | DialogConstants.BsPushButton),
        ];
    }

    // ---- khởi tạo -----------------------------------------------------------------------------------------------------

    protected override void OnInitialized()
    {
        SetItemText(FolderId, _initialFolder);
        SetItemText(LimitId, BenchmarkPresenter.DefaultImageLimitText);

        var list = Item(ProfilesId);
        foreach (var item in _presenter.ProfileItems)
        {
            ListBoxControl.Add(list, item.Name);
        }

        var defaultIndex = IndexOfProfile(_presenter.DefaultSelection);
        if (defaultIndex >= 0)
        {
            ListBoxControl.SetSelected(list, defaultIndex, true);
        }

        var results = Item(ResultsId);
        ListViewControl.Configure(results, DialogColors.FromKey("Dark.Window"), DialogColors.Text);
        AddColumns();
        ProgressControl.Configure(Item(ProgressId));

        SetItemTooltips(ProfilesId, index => index >= 0 && index < _presenter.ProfileItems.Count
            ? _presenter.ProfileItems[index].Description
            : null);
        SetTooltip(FolderId, Tr.BenchmarkFolderAutomationName);
        SetTooltip(LimitId, Tr.BenchmarkImageLimitAutomationName);
        SetTooltip(QuickCheckId, Tr.BenchmarkQuickCheckAutomationName);

        ((INotifyCollectionChanged)_presenter.Rows).CollectionChanged += OnRowsChanged;
        Localizer.CurrentChanged += _languageHandler;
        _textsApplied = true;
    }

    private int IndexOfProfile(BenchmarkProfileItem item)
    {
        for (var i = 0; i < _presenter.ProfileItems.Count; i++)
        {
            if (ReferenceEquals(_presenter.ProfileItems[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] ColumnHeaders() =>
    [
        BenchmarkResultRow.BestSymbol, Tr.BenchmarkColumnProfile, Tr.BenchmarkColumnLoadingMode, Tr.BenchmarkColumnWorkload,
        Tr.BenchmarkColumnSamples, "P50", "P95", "P99", "Max", Tr.BenchmarkColumnStatus,
    ];

    private void AddColumns()
    {
        var results = Item(ResultsId);
        var headers = ColumnHeaders();
        for (var i = 0; i < headers.Length; i++)
        {
            ListViewControl.AddColumn(results, i, headers[i], Dx(40), rightAligned: i is >= 4 and <= 8);
        }
    }

    protected override void OnClosed()
    {
        Localizer.CurrentChanged -= _languageHandler;
        if (_textsApplied)
        {
            ((INotifyCollectionChanged)_presenter.Rows).CollectionChanged -= OnRowsChanged;
        }

        // Đóng giữa chừng = huỷ lượt chạy (lượt chạy kết thúc bằng đường huỷ bình thường, không hàng Fail giả).
        _presenter.Dispose();
    }

    // ---- ngôn ngữ ---------------------------------------------------------------------------------------------------------

    /// <summary>Đổi ngôn ngữ khi cửa sổ modeless đang mở: chữ của điều khiển + tên profile + ô bảng đều lấy lại từ catalog.</summary>
    private void OnLanguageChanged()
    {
        if (IsClosed || Hwnd == 0)
        {
            return;
        }

        User32.SetWindowText(Hwnd, Tr.BenchmarkWindowTitle);
        SetItemText(HeaderId, Tr.BenchmarkHeader);
        SetItemText(DescriptionId, Tr.BenchmarkDescription);
        SetItemText(BrowseId, Tr.BenchmarkBrowse);
        SetItemText(LimitLabelId, Tr.BenchmarkImageLimitLabel);
        SetItemText(ProfilesLabelId, Tr.BenchmarkProfilesLabel);
        SetItemText(SelectAllId, Tr.BenchmarkSelectAll);
        SetItemText(SelectNoneId, Tr.BenchmarkSelectNone);
        SetItemText(RunId, Tr.BenchmarkRun);
        SetItemText(QuickCheckId, Tr.BenchmarkQuickCheck);
        SetItemText(CancelRunId, Tr.CommonCancel);
        SetItemText(OpenReportId, Tr.BenchmarkOpenReport);
        SetItemText(OpenFolderId, Tr.BenchmarkOpenResultFolder);
        SetItemText(CloseId, Tr.CommonClose);
        var headers = ColumnHeaders();
        for (var i = 0; i < headers.Length; i++)
        {
            ListViewControl.SetColumnText(Item(ResultsId), i, headers[i]);
        }

        _presenter.NotifyLanguageChanged(); // tên/mô tả profile + RefreshRows
        var list = Item(ProfilesId);
        User32.InvalidateRect(list, null, true);
        RunLayout();
    }

    // ---- bảng kết quả ---------------------------------------------------------------------------------------------------

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();

    /// <summary>Dựng lại bảng từ <see cref="BenchmarkPresenter.Rows"/> theo cột đang sắp xếp; giữ hàng đang chọn.</summary>
    private void SyncRows()
    {
        var results = Item(ResultsId);
        if (results == 0)
        {
            return;
        }

        var selectedSource = -1;
        var selected = ListViewControl.SelectedRow(results);
        if (selected >= 0)
        {
            selectedSource = (int)ListViewControl.RowParam(results, selected);
        }

        var rows = _presenter.Rows;
        var order = new List<int>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            order.Add(i);
        }

        if (_sortColumn >= 0 && _sortDirection != 0)
        {
            var ascending = _sortDirection == 1;
            order.Sort((a, b) =>
            {
                var compare = CompareRows(rows[a], rows[b], _sortColumn);
                if (compare == 0)
                {
                    compare = a.CompareTo(b); // ổn định
                }

                return ascending ? compare : -compare;
            });
        }

        ListViewControl.Clear(results);
        var newSelection = -1;
        for (var displayed = 0; displayed < order.Count; displayed++)
        {
            var source = order[displayed];
            ListViewControl.AddRow(results, rows[source].CellTexts, source);
            if (source == selectedSource)
            {
                newSelection = displayed;
            }
        }

        if (newSelection >= 0)
        {
            ListViewControl.SelectRow(results, newSelection);
        }
    }

    /// <summary>So sánh hai hàng theo cột: cột số theo giá trị, cột chữ theo chữ đang hiển thị (như sắp xếp của DataGrid).</summary>
    internal static int CompareRows(BenchmarkResultRow left, BenchmarkResultRow right, int column) => column switch
    {
        4 => left.Count.CompareTo(right.Count),
        5 => left.Phase.P50.CompareTo(right.Phase.P50),
        6 => left.Phase.P95.CompareTo(right.Phase.P95),
        7 => left.Phase.P99.CompareTo(right.Phase.P99),
        8 => left.Phase.Max.CompareTo(right.Phase.Max),
        _ => CultureInfo.CurrentCulture.CompareInfo.Compare(left.CellTexts[Math.Clamp(column, 0, ColumnCount - 1)],
            right.CellTexts[Math.Clamp(column, 0, ColumnCount - 1)], CompareOptions.None),
    };

    /// <summary>Sắp xếp theo cột (như bấm header): lần đầu tăng, bấm lại cùng cột thì đảo.</summary>
    internal void SortBy(int column)
    {
        _sortDirection = _sortColumn == column && _sortDirection == 1 ? 2 : 1;
        _sortColumn = column;
        SetListViewSort(ResultsId, column, _sortDirection);
        SyncRows();
    }

    // ---- thông điệp -----------------------------------------------------------------------------------------------------

    protected override bool OnNotify(int id, int code, nint header, out nint result)
    {
        result = 0;
        if (id != ResultsId)
        {
            return false;
        }

        if (code == ControlConstants.NmCustomDraw)
        {
            result = ListViewControl.CustomDraw(header, RowColors);
            return true;
        }

        if (code == ControlConstants.LvnColumnClick)
        {
            SortBy(*(int*)(header + 28));
            return true;
        }

        return false;
    }

    private (uint Back, uint Text) RowColors(int displayedRow, nint source)
    {
        var rows = _presenter.Rows;
        if (source >= 0 && source < rows.Count)
        {
            var row = rows[(int)source];
            if (row.IsBest)
            {
                return (DialogColors.FromKey("Dark.SuccessBackground"), DialogColors.Text);
            }

            if (row.Status == BenchmarkResultStatus.Fail)
            {
                return (DialogColors.FromKey("Dark.ErrorBackground"), DialogColors.Text);
            }
        }

        return (DialogColors.FromKey(displayedRow % 2 == 0 ? "Dark.Panel" : "Dark.RowAlt"), DialogColors.Text);
    }

    protected override (string Text, string? Badge) ListItemText(int id, int index)
    {
        if (id == ProfilesId && index >= 0 && index < _presenter.ProfileItems.Count)
        {
            var item = _presenter.ProfileItems[index];
            return (item.Name, BenchmarkText.LoadingModeText(item.Profile.LoadingMode));
        }

        return base.ListItemText(id, index);
    }

    protected override uint StaticTextColor(int id) => id switch
    {
        DescriptionId => DialogColors.FromKey("Dark.TextSubtle"),
        StatusId => DialogColors.TextSecondary,
        _ => base.StaticTextColor(id),
    };

    protected override bool OnCommand(int id, int notification)
    {
        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        switch (id)
        {
            case BrowseId:
                Browse();
                return true;
            case SelectAllId:
                ListBoxControl.SelectAll(Item(ProfilesId), true);
                return true;
            case SelectNoneId:
                ListBoxControl.SelectAll(Item(ProfilesId), false);
                return true;
            case RunId:
                Run();
                return true;
            case QuickCheckId:
                Observe(_presenter.QuickCheckAsync(GetItemText(FolderId), GetItemText(LimitId)));
                return true;
            case CancelRunId:
                _presenter.Cancel();
                return true;
            case OpenReportId:
                _presenter.OpenReport();
                return true;
            case OpenFolderId:
                _presenter.OpenResultFolder();
                return true;
            case CloseId:
                Close();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Esc: lúc đang chạy thì huỷ lượt chạy (cửa sổ ở lại); lúc rảnh thì đóng cửa sổ.</summary>
    protected override bool OnCancel()
    {
        if (!_presenter.IsRunning)
        {
            return false;
        }

        _presenter.Cancel();
        return true;
    }

    private void Browse()
    {
        var current = GetItemText(FolderId);
        var folder = _pickFolder(Hwnd, Tr.BenchmarkPickFolderTitle, Directory.Exists(current) ? current : null);
        if (!string.IsNullOrEmpty(folder))
        {
            SetItemText(FolderId, folder);
        }
    }

    private void Run()
    {
        var list = Item(ProfilesId);
        var profiles = ListBoxControl.SelectedIndices(list).Select(i => _presenter.ProfileItems[i].Profile).ToArray();
        Observe(_presenter.RunSelectedAsync(profiles, GetItemText(FolderId), GetItemText(LimitId)));
    }

    /// <summary>
    /// Task của presenter chạy tiếp trên UI thread (bối cảnh đồng bộ của shell); presenter đã bắt lỗi của lượt chạy, nên chỉ ghi
    /// log nếu vẫn có ngoại lệ lọt ra (thay cho <c>async void</c> của bản WPF).
    /// </summary>
    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                AppLog.Error("Benchmark run failed", task.Exception!.GetBaseException());
            }

            return;
        }

        _ = task.ContinueWith(
            static t => AppLog.Error("Benchmark run failed", t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void StartExplorer(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Could not start Explorer", ex);
        }
    }

    // ---- IBenchmarkView -----------------------------------------------------------------------------------------------

    void IBenchmarkView.SetStatus(string text)
    {
        if (Hwnd == 0)
        {
            return;
        }

        SetItemText(StatusId, text);
        RunLayout(); // dòng trạng thái xuống dòng: bảng kết quả co/giãn theo
    }

    void IBenchmarkView.SetProgress(double fraction)
    {
        if (Hwnd != 0)
        {
            ProgressControl.SetFraction(Item(ProgressId), fraction);
        }
    }

    void IBenchmarkView.SetRunning(bool running)
    {
        if (Hwnd == 0)
        {
            return;
        }

        _running = running;
        var focus = User32.GetFocus();
        Enable(RunId, !running);
        Enable(QuickCheckId, !running);
        Enable(CancelRunId, running);
        Enable(BrowseId, !running);
        Enable(ProfilesId, !running);
        Enable(LimitId, !running);
        Enable(FolderId, !running);
        // Điều khiển đang giữ focus bị khoá thì chuyển focus tới nút hợp lệ (Tab/Esc vẫn dùng được).
        if (focus != 0 && !User32.IsWindowEnabled(focus))
        {
            User32.SetFocus(Item(running ? CancelRunId : RunId));
        }
    }

    void IBenchmarkView.RefreshRows() => SyncRows();

    void IBenchmarkView.OpenInExplorer(string arguments) => _openInExplorer(arguments);

    private void Enable(int id, bool enabled)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.EnableWindow(child, enabled);
        }
    }

    // ---- bố cục -------------------------------------------------------------------------------------------------------------

    private int Margin => Dx(10);

    private int FieldHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(520), Dy(330));

    protected override (int Width, int Height) MinimumClientSize()
        => (Math.Max(Dx(400), ButtonRowWidth(OpenReportId, OpenFolderId, CloseId) + (2 * Margin)), Dy(280));

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var m = Margin;
        var content = Math.Max(1, clientWidth - (2 * m));
        var y = m;

        var header = MeasureItemHeight(HeaderId, content);
        Place(HeaderId, m, y, content, header);
        y += header + Dy(2);
        var description = MeasureItemHeight(DescriptionId, content);
        Place(DescriptionId, m, y, content, description);
        y += description + Dy(8);

        // Thư mục + Chọn thư mục.
        var browse = ButtonWidth(BrowseId);
        Place(FolderId, m, y, Math.Max(Dx(40), content - browse - Dx(4)), FieldHeight);
        Place(BrowseId, m + content - browse, y, browse, FieldHeight);
        y += FieldHeight + Dy(6);

        // Giới hạn ảnh.
        var limitLabel = MeasureTextWidth(GetItemText(LimitLabelId));
        Place(LimitLabelId, m, y + ((FieldHeight - LineHeight) / 2), limitLabel + Dx(2), LineHeight);
        Place(LimitId, m + limitLabel + Dx(8), y, Dx(40), FieldHeight);
        y += FieldHeight + Dy(6);

        // Nhãn danh sách profile + Chọn hết / Bỏ chọn.
        var buttons = ButtonWidth(SelectAllId, SelectNoneId);
        var labelWidth = Math.Max(Dx(60), content - (2 * buttons) - Dx(12));
        var label = MeasureItemHeight(ProfilesLabelId, labelWidth);
        var rowHeight = Math.Max(label, ButtonHeight);
        Place(ProfilesLabelId, m, y + ((rowHeight - label) / 2), labelWidth, label);
        Place(SelectAllId, m + content - (2 * buttons) - Dx(4), y + ((rowHeight - ButtonHeight) / 2), buttons, ButtonHeight);
        Place(SelectNoneId, m + content - buttons, y + ((rowHeight - ButtonHeight) / 2), buttons, ButtonHeight);
        y += rowHeight + Dy(3);

        // Danh sách profile (cao cố định như WPF: 120 px) + hàng nút chạy.
        SetListItemHeight(ProfilesId, LineHeight + Dy(5));
        var listHeight = Dy(64);
        Place(ProfilesId, m, y, content, listHeight);
        y += listHeight + Dy(6);
        var runWidth = ButtonWidth(RunId, QuickCheckId, CancelRunId);
        Place(RunId, m, y, runWidth, ButtonHeight);
        Place(QuickCheckId, m + runWidth + Dx(6), y, runWidth, ButtonHeight);
        Place(CancelRunId, m + (2 * (runWidth + Dx(6))), y, runWidth, ButtonHeight);
        y += ButtonHeight + Dy(8);

        // Tiến độ + trạng thái.
        Place(ProgressId, m, y, content, Math.Max(Dy(4), Dy(5)));
        y += Dy(5) + Dy(4);
        var status = string.IsNullOrEmpty(GetItemText(StatusId)) ? 0 : MeasureItemHeight(StatusId, content);
        Place(StatusId, m, y, content, status);
        y += status + Dy(6);

        // Hàng nút dưới cùng + bảng kết quả lấp phần còn lại.
        var bottomTop = clientHeight - m - ButtonHeight;
        PlaceButtonRow(clientWidth, m, bottomTop, ButtonHeight, OpenReportId, OpenFolderId, CloseId);
        var resultsHeight = Math.Max(Dy(30), bottomTop - Dy(8) - y);
        Place(ResultsId, m, y, content, resultsHeight);
        FitColumns(content);
    }

    /// <summary>Độ rộng cột theo dialog-unit (cột Profile và Status chia phần còn lại 2:1) mỗi lần bố cục, nên đúng ở mọi DPI.</summary>
    private void FitColumns(int listWidth)
    {
        var results = Item(ResultsId);
        if (results == 0)
        {
            return;
        }

        int[] fixedUnits = [16, 0, 54, 62, 32, 36, 36, 36, 36, 0];
        var fixedPx = fixedUnits.Sum(Dx);
        var flex = Math.Max(0, listWidth - fixedPx - Dx(14)); // thanh cuộn dọc
        var profile = Math.Max(Dx(70), flex * 2 / 3);
        var status = Math.Max(Dx(50), flex - profile);
        for (var i = 0; i < fixedUnits.Length; i++)
        {
            var width = i == 1 ? profile : i == 9 ? status : Dx(fixedUnits[i]);
            ListViewControl.SetColumnWidth(results, i, width);
        }
    }
}
