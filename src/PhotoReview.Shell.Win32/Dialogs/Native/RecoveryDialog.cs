using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PhotoReview.App;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Phần phụ thuộc có thể thay của cửa sổ Recovery (test); null = thật.</summary>
/// <param name="Messages">Hộp thoại xác nhận/thông báo modal (TaskDialog thật); test thay bản ghi lại.</param>
/// <param name="Copy">Đưa đường dẫn vào clipboard (true = thành công); null = nút Copy path không làm gì.</param>
/// <param name="Explorer">Mở Explorer với tham số (ném <see cref="Win32Exception"/> khi không mở được); null = <c>explorer.exe</c> thật.</param>
/// <param name="AutoCheck">Bắt đầu kiểm tra nền ngay khi hộp thoại tạo xong (mặc định true như WPF); test tắt để điều khiển từng bước.</param>
internal sealed record RecoveryDialogSeams(
    ITaskDialogPort? Messages = null,
    Func<string, bool>? Copy = null,
    Action<string>? Explorer = null,
    bool AutoCheck = true);

/// <summary>
/// WP-35 GĐ4 (phương án B, CHƯA XÁC NHẬN): cửa sổ Recovery MODAL - view Win32 của <see cref="RecoveryWindowPresenter"/> (WPF:
/// <c>RecoveryWindow</c>). Presenter (đóng băng ở GĐ0, ADR 0003/0005/0007) lo MỌI thứ tự/điều kiện của kiểm tra nền, Clear và Retry;
/// view chỉ chép giá trị vào điều khiển, hỏi xác nhận bằng TaskDialog modal và gọi presenter đúng chỗ WPF gọi:
/// <list type="bullet">
/// <item>danh sách owner-draw đa chọn (Extended): tiêu đề + dòng phụ + huy hiệu tô theo <see cref="RecoveryTone"/>;</item>
/// <item>bộ lọc (combobox), kiểm tra lại + dòng trạng thái tiến độ;</item>
/// <item>chi tiết của MỘT hàng chọn: verdict, lỗi journal, nguồn/đích (đường dẫn, trạng thái, Copy path, Show in Explorer),
/// giải thích/hành động/thành viên nhóm trong ô chỉ đọc cuộn được;</item>
/// <item>Retry/Clear selected/Clear all (xác nhận trước khi gọi delegate, đúng thứ tự presenter), Close.</item>
/// </list>
/// Không ghi tệp nào ngoài delegate Retry/Dismiss do người gọi cấp. Đóng giữa chừng: huỷ kiểm tra nền; từ chối đóng (X/Esc/Close)
/// khi đang Retry (R2-A-01); đóng từ ngoài (shell thoát) luôn đóng để không treo.
/// </summary>
internal sealed class RecoveryDialog : NativeDialog, IRecoveryCheckView
{
    internal const int HeaderId = 100;
    internal const int SummaryId = 101;
    internal const int FilterLabelId = 102;
    internal const int FilterId = 103;
    internal const int RecheckId = 104;
    internal const int CheckStatusId = 105;
    internal const int ListId = 106;
    internal const int FilterEmptyId = 107;
    internal const int DetailsEmptyId = 108;
    internal const int VerdictLabelId = 110;
    internal const int VerdictBadgeId = 111;
    internal const int ErrorId = 112;
    internal const int SourceTitleId = 113;
    internal const int SourceStatusId = 114;
    internal const int SourcePathId = 115;
    internal const int CopySourceId = 116;
    internal const int ShowSourceId = 117;
    internal const int DestinationTitleId = 118;
    internal const int DestinationStatusId = 119;
    internal const int DestinationPathId = 120;
    internal const int CopyDestinationId = 121;
    internal const int ShowDestinationId = 122;
    internal const int NoDestinationId = 123;
    internal const int DetailsId = 124;
    internal const int ClearSelectedId = 130;
    internal const int ClearAllId = 131;
    internal const int RetryId = 132;
    internal const int CloseId = IdCancel;

    private const uint SsCenter = 0x1;
    private const string Crlf = "\r\n";
    private const nint CursorArrow = 32512;
    private const nint CursorWait = 32514;

    private readonly RecoveryWindowPresenter _presenter;
    private readonly ITaskDialogPort _messages;
    private readonly Func<string, bool> _copy;
    private readonly Action<string> _explorer;
    private readonly bool _autoCheck;
    private readonly List<(RecoveryRowModel Row, Action Handler)> _subscriptions = [];
    private List<RecoveryRowModel> _visible = [];
    private bool _initialized;
    private bool _busy;
    private RecoveryTone _verdictTone = RecoveryTone.Neutral;
    private RecoveryTone _sourceTone = RecoveryTone.Neutral;
    private RecoveryTone _destinationTone = RecoveryTone.Neutral;
    private string _sourcePath = string.Empty;
    private string _destinationPath = string.Empty;
    private bool _hasDetails;
    private bool _errorVisible;
    private bool _sourceVisible;
    private bool _destinationVisible;
    private bool _noDestinationVisible;

    public RecoveryDialog(IReadOnlyList<JournalEntry> entries, Func<JournalEntry, Task<RecoveryRetryResult>>? retry = null,
        Func<IReadOnlyList<JournalEntry>, DismissOutcome>? dismiss = null, IFileSystem? fileSystem = null,
        Func<bool>? allowPermanentDelete = null, RecoveryDialogSeams? seams = null, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _presenter = new RecoveryWindowPresenter(entries, retry, dismiss, fileSystem, allowPermanentDelete);
        _messages = seams?.Messages ?? new NativeTaskDialogPort();
        _copy = seams?.Copy ?? (static _ => false);
        _explorer = seams?.Explorer ?? StartExplorer;
        _autoCheck = seams?.AutoCheck ?? true;
        Items = BuildItems();
    }

    /// <summary>Presenter (test: hàng, trạng thái Retry).</summary>
    internal RecoveryWindowPresenter Presenter => _presenter;

    /// <summary>Task của lượt kiểm tra gần nhất (test: chờ lượt kiểm tra kết thúc).</summary>
    internal Task? LastCheckTask { get; private set; }

    /// <summary>Task của lượt Retry gần nhất do nút Retry khởi động (test).</summary>
    internal Task? LastRetryTask { get; private set; }

    /// <summary>Retry đang chạy (con trỏ chờ, Retry/Clear khoá).</summary>
    internal bool IsBusy => _busy;

    protected override string Title => Tr.RecoveryTitle;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override bool Resizable => true;

    protected override IReadOnlyList<int> HeaderControlIds => [HeaderId];

    protected override int InitialFocusId => ListId;

    protected override int DefaultButtonId => CloseId;

    protected override bool CanCloseByUser => _presenter.CanClose;

    protected override bool WaitCursor => _busy;

    private static List<DialogItem> BuildItems()
    {
        const uint child = DialogStyles.Child;
        const uint tab = DialogConstants.WsTabStop;
        const uint text = DialogStyles.Child | DialogConstants.SsNoPrefix;
        const uint readOnlyLine = child | tab | ControlConstants.EsMultiline | ControlConstants.EsReadOnly;
        return
        [
            new(DialogControlClass.Static, HeaderId, Tr.RecoveryHeader, text),
            new(DialogControlClass.Static, SummaryId, string.Empty, text),
            new(DialogControlClass.Static, FilterLabelId, Tr.RecoveryFilterLabel, text),
            new(DialogControlClass.ComboBox, FilterId, string.Empty, child | ComboBoxControl.DefaultStyle),
            new(DialogControlClass.Button, RecheckId, Tr.RecoveryCheckRecheck, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, CheckStatusId, string.Empty, text),
            new(DialogControlClass.ListBox, ListId, string.Empty,
                child | tab | DialogConstants.WsVScroll | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight
                | ControlConstants.LbsExtendedSel | ControlConstants.LbsOwnerDrawFixed | ControlConstants.LbsHasStrings,
                DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, FilterEmptyId, Tr.RecoveryFilterEmpty, child | DialogConstants.SsNoPrefix | SsCenter),
            new(DialogControlClass.Static, DetailsEmptyId, Tr.RecoveryDetailEmpty, child | DialogConstants.SsNoPrefix | SsCenter),
            new(DialogControlClass.Static, VerdictLabelId, Tr.RecoveryDetailVerdict, text),
            new(DialogControlClass.Static, VerdictBadgeId, string.Empty, text),
            new(DialogControlClass.Static, ErrorId, string.Empty, text),
            new(DialogControlClass.Static, SourceTitleId, Tr.RecoveryDetailSource, text),
            new(DialogControlClass.Static, SourceStatusId, string.Empty, text),
            new(DialogControlClass.Edit, SourcePathId, string.Empty, readOnlyLine, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, CopySourceId, Tr.RecoveryDetailCopyPath, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ShowSourceId, Tr.RecoveryDetailShowInExplorer, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, DestinationTitleId, Tr.RecoveryDetailDestination, text),
            new(DialogControlClass.Static, DestinationStatusId, string.Empty, text),
            new(DialogControlClass.Edit, DestinationPathId, string.Empty, readOnlyLine, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, CopyDestinationId, Tr.RecoveryDetailCopyPath, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ShowDestinationId, Tr.RecoveryDetailShowInExplorer, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, NoDestinationId, Tr.RecoveryDetailNoDestination, text),
            new(DialogControlClass.Edit, DetailsId, string.Empty,
                child | tab | DialogConstants.WsVScroll | ControlConstants.EsMultiline | ControlConstants.EsAutoVScroll
                | ControlConstants.EsReadOnly, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, ClearSelectedId, Tr.RecoveryClearSelected, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ClearAllId, Tr.RecoveryClearAll, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, RetryId, Tr.RecoveryRetry, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, CloseId, Tr.CommonClose, child | tab | DialogConstants.BsDefPushButton),
        ];
    }

    // ---- khởi tạo / đóng -------------------------------------------------------------------------------------------------

    protected override void OnInitialized()
    {
        var filter = Item(FilterId);
        ComboBoxControl.Add(filter, Tr.RecoveryFilterAll);
        ComboBoxControl.Add(filter, Tr.RecoveryFilterCanRetry);
        ComboBoxControl.Add(filter, Tr.RecoveryFilterAlreadyDone);
        ComboBoxControl.Add(filter, Tr.RecoveryFilterProblems);
        ComboBoxControl.Select(filter, 0);

        SetTooltip(FilterId, Tr.RecoveryFilterAutomationName);
        SetTooltip(RecheckId, Tr.RecoveryCheckRecheckAutomationName);
        SetTooltip(ClearSelectedId, Tr.RecoveryClearSelectedAutomationName);
        SetTooltip(ClearAllId, Tr.RecoveryClearAllAutomationName);
        SetTooltip(RetryId, Tr.RecoveryRetryAutomationName);
        SetTooltip(CopySourceId, Tr.RecoveryDetailCopyPathAutomationName);
        SetTooltip(ShowSourceId, Tr.RecoveryDetailShowInExplorerAutomationName);
        SetTooltip(CopyDestinationId, Tr.RecoveryDetailCopyPathAutomationName);
        SetTooltip(ShowDestinationId, Tr.RecoveryDetailShowInExplorerAutomationName);

        foreach (var row in _presenter.Rows)
        {
            var captured = row;
            Action handler = () => OnRowResultChanged(captured);
            row.ResultChanged += handler;
            _subscriptions.Add((row, handler));
        }

        RefreshEntries();
        _initialized = true;

        // WPF: la kiem tra nen bat dau khi cua so mo (Loaded), tren luong nen, va bi huy khi dong.
        if (_autoCheck)
        {
            StartChecks();
        }
    }

    protected override void OnClosed()
    {
        // Đóng giữa chừng = huỷ kiểm tra nền (lượt kiểm tra kết thúc bằng đường huỷ bình thường, không ghi gì vào hàng).
        _presenter.CancelCheck();
        foreach (var (row, handler) in _subscriptions)
        {
            row.ResultChanged -= handler;
        }

        _subscriptions.Clear();
    }

    /// <summary>Esc / nút Close: bị từ chối khi đang Retry (cửa sổ không đóng trước khi kết quả về, R2-A-01).</summary>
    protected override bool OnCancel() => !_presenter.CanClose;

    // ---- danh sách ---------------------------------------------------------------------------------------------------------

    private nint ListHandle => Item(ListId);

    private RecoveryFilter CurrentFilter => (RecoveryFilter)Math.Max(0, ComboBoxControl.SelectedIndex(Item(FilterId)));

    /// <summary>Các hàng đang chọn (theo thứ tự hiển thị).</summary>
    internal IReadOnlyList<RecoveryRowModel> SelectedRows()
    {
        var list = ListHandle;
        if (list == 0)
        {
            return [];
        }

        var result = new List<RecoveryRowModel>();
        foreach (var index in ListBoxControl.SelectedIndices(list))
        {
            if (index >= 0 && index < _visible.Count)
            {
                result.Add(_visible[index]);
            }
        }

        return result;
    }

    /// <summary>Hàng duy nhất đang chọn, hoặc null khi không chọn hay chọn nhiều (như <c>SelectedRow</c> của WPF).</summary>
    private RecoveryRowModel? SingleSelected()
    {
        var selected = SelectedRows();
        return selected.Count == 1 ? selected[0] : null;
    }

    /// <summary>Các hàng đang hiển thị sau bộ lọc (test).</summary>
    internal IReadOnlyList<RecoveryRowModel> VisibleRows => _visible;

    /// <summary>Chọn hàng hiển thị <paramref name="visibleIndex"/> (như người dùng bấm) và cập nhật chi tiết/nút (test).</summary>
    internal void SelectRows(params int[] visibleIndices)
    {
        var list = ListHandle;
        ListBoxControl.SelectAll(list, false);
        foreach (var index in visibleIndices)
        {
            ListBoxControl.SetSelected(list, index, true);
        }

        OnSelectionChanged();
    }

    /// <summary>Chữ + huy hiệu của dòng hiển thị <paramref name="index"/> (test).</summary>
    internal (string Text, string? Badge, string? Subtitle, uint? Color) RowView(int index)
        => (ListItemText(ListId, index).Text, ListItemText(ListId, index).Badge, ListItemSubtitle(ListId, index),
            ListItemBadgeColor(ListId, index));

    protected override (string Text, string? Badge) ListItemText(int id, int index)
    {
        if (id == ListId && index >= 0 && index < _visible.Count)
        {
            var row = _visible[index];
            return (row.Title, row.BadgeText);
        }

        return base.ListItemText(id, index);
    }

    protected override string? ListItemSubtitle(int id, int index)
        => id == ListId && index >= 0 && index < _visible.Count ? _visible[index].Subtitle : null;

    protected override uint? ListItemBadgeColor(int id, int index)
        => id == ListId && index >= 0 && index < _visible.Count ? ToColorRef(_visible[index].BadgeTone) : null;

    internal static uint ToColorRef(RecoveryTone tone)
    {
        var (r, g, b) = RecoveryPresentation.Rgb(tone);
        return (uint)(r | (g << 8) | (b << 16));
    }

    /// <summary>Dựng lại danh sách theo bộ lọc, giữ chọn của các hàng còn hiển thị (như <c>RefreshEntries</c> của WPF).</summary>
    private void RefreshEntries()
    {
        var list = ListHandle;
        if (list == 0)
        {
            return;
        }

        var selected = SelectedRows().ToHashSet();
        var top = (int)User32.SendMessage(list, ControlConstants.LbGetTopIndex, 0, 0);
        var shown = _presenter.Shown(CurrentFilter);
        _visible = shown;
        ListBoxControl.Clear(list);
        User32.SendMessage(list, DialogConstants.LbInitStorage, shown.Count, shown.Count * 64);
        foreach (var row in shown)
        {
            ListBoxControl.Add(list, row.Title);
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (selected.Contains(shown[i]))
            {
                ListBoxControl.SetSelected(list, i, true);
            }
        }

        if (top > 0 && top < shown.Count)
        {
            User32.SendMessage(list, ControlConstants.LbSetTopIndex, top, 0);
        }

        SetItemText(SummaryId, _presenter.SummaryText);
        var empty = _presenter.FilterEmptyVisible(shown.Count);
        ShowItem(FilterEmptyId, empty);
        if (empty && User32.GetFocus() == list)
        {
            User32.SetFocus(Item(FilterId));
        }

        ShowItem(ListId, !empty);
        UpdateDetails();
        UpdateButtons();
    }

    private static unsafe void Invalidate(nint hwnd) => User32.InvalidateRect(hwnd, null, true);

    private void OnSelectionChanged()
    {
        UpdateDetails();
        UpdateButtons();
    }

    private void OnRowResultChanged(RecoveryRowModel row)
    {
        var list = ListHandle;
        if (list == 0)
        {
            return;
        }

        Invalidate(list); // huy hieu "Checking..." -> ket qua
        if (ReferenceEquals(row, SingleSelected()))
        {
            UpdateDetails();
            UpdateButtons();
        }
    }

    // ---- chi tiết -----------------------------------------------------------------------------------------------------------

    /// <summary>Toàn bộ chữ của ô chi tiết cuộn được (giải thích, hành động, nguồn/đích hiện tại + journal, thành viên nhóm).</summary>
    internal static string BuildDetailsText(RecoveryDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var text = new StringBuilder();
        text.Append(details.ExplainText);
        if (details.ActionText.Length > 0)
        {
            text.Append(Crlf).Append(Crlf).Append(Tr.RecoveryDetailAction).Append(": ").Append(details.ActionText);
        }

        if (details.SourceVisible && details.Source is { } source)
        {
            AppendPath(text, source);
        }

        if (details.DestinationVisible && details.Destination is { } destination)
        {
            AppendPath(text, destination);
        }

        if (details.GroupMembers is { } members)
        {
            text.Append(Crlf).Append(Crlf).Append(details.GroupHeading);
            foreach (var member in members)
            {
                text.Append(Crlf).Append(Crlf).Append(member.Status).Append(Crlf).Append(member.Source).Append(Crlf).Append(member.Destination);
            }
        }

        return text.ToString();
    }

    private static void AppendPath(StringBuilder text, RecoveryPathView view)
    {
        text.Append(Crlf).Append(Crlf).Append(view.Title);
        if (view.NowText.Length > 0)
        {
            text.Append(Crlf).Append(view.NowText);
        }

        if (view.JournalText.Length > 0)
        {
            text.Append(Crlf).Append(view.JournalText);
        }

        if (!string.IsNullOrEmpty(view.Note))
        {
            text.Append(Crlf).Append(view.Note);
        }
    }

    private void UpdateDetails()
    {
        if (ListHandle == 0)
        {
            return;
        }

        var row = SingleSelected();
        _hasDetails = row is not null;
        ShowItem(DetailsEmptyId, row is null);
        ShowItem(VerdictLabelId, row is not null);
        ShowItem(VerdictBadgeId, row is not null);
        ShowItem(DetailsId, row is not null);
        if (row is null)
        {
            _errorVisible = _sourceVisible = _destinationVisible = _noDestinationVisible = false;
            _sourcePath = _destinationPath = string.Empty;
            ShowItem(ErrorId, false);
            ShowItem(SourceTitleId, false);
            ShowItem(SourceStatusId, false);
            ShowItem(SourcePathId, false);
            ShowItem(CopySourceId, false);
            ShowItem(ShowSourceId, false);
            ShowItem(DestinationTitleId, false);
            ShowItem(DestinationStatusId, false);
            ShowItem(DestinationPathId, false);
            ShowItem(CopyDestinationId, false);
            ShowItem(ShowDestinationId, false);
            ShowItem(NoDestinationId, false);
            SetItemText(DetailsId, string.Empty);
            Relayout();
            return;
        }

        var details = RecoveryWindowPresenter.Details(row);
        _verdictTone = details.VerdictTone;
        SetItemText(VerdictBadgeId, details.VerdictText);
        _errorVisible = details.ErrorVisible;
        SetItemText(ErrorId, details.ErrorText);
        ShowItem(ErrorId, _errorVisible);

        _sourceVisible = details.SourceVisible && details.Source is not null;
        if (details.Source is { } source && details.SourceVisible)
        {
            _sourceTone = source.StatusTone;
            _sourcePath = source.Path;
            SetItemText(SourceTitleId, source.Title);
            SetItemText(SourceStatusId, source.StatusText);
            SetItemText(SourcePathId, source.Path);
        }
        else
        {
            _sourcePath = string.Empty;
        }

        ShowItem(SourceTitleId, _sourceVisible);
        ShowItem(SourceStatusId, _sourceVisible);
        ShowItem(SourcePathId, _sourceVisible);
        ShowItem(CopySourceId, _sourceVisible);
        ShowItem(ShowSourceId, _sourceVisible);

        _destinationVisible = details.DestinationVisible && details.Destination is not null;
        if (details.Destination is { } destination && details.DestinationVisible)
        {
            _destinationTone = destination.StatusTone;
            _destinationPath = destination.Path;
            SetItemText(DestinationTitleId, destination.Title);
            SetItemText(DestinationStatusId, destination.StatusText);
            SetItemText(DestinationPathId, destination.Path);
        }
        else
        {
            _destinationPath = string.Empty;
        }

        ShowItem(DestinationTitleId, _destinationVisible);
        ShowItem(DestinationStatusId, _destinationVisible);
        ShowItem(DestinationPathId, _destinationVisible);
        ShowItem(CopyDestinationId, _destinationVisible);
        ShowItem(ShowDestinationId, _destinationVisible);

        _noDestinationVisible = details.NoDestinationVisible;
        ShowItem(NoDestinationId, _noDestinationVisible);
        SetItemText(DetailsId, BuildDetailsText(details));
        Relayout();
    }

    private void Relayout()
    {
        if (_initialized)
        {
            RunLayout();
        }
    }

    protected override uint StaticTextColor(int id) => id switch
    {
        VerdictBadgeId => ToColorRef(_verdictTone),
        SourceStatusId => ToColorRef(_sourceTone),
        DestinationStatusId => ToColorRef(_destinationTone),
        ErrorId => DialogColors.FromKey("Dark.ErrorText"),
        SummaryId or CheckStatusId => DialogColors.TextSecondary,
        VerdictLabelId or FilterEmptyId or DetailsEmptyId or NoDestinationId => DialogColors.FromKey("Dark.TextMuted"),
        SourcePathId or DestinationPathId or DetailsId => DialogColors.TextStrong,
        _ => base.StaticTextColor(id),
    };

    // ---- nút -----------------------------------------------------------------------------------------------------------------

    private void UpdateButtons()
    {
        if (ListHandle == 0)
        {
            return;
        }

        var selected = SelectedRows();
        var states = _presenter.ButtonStates(selected.Count == 1 ? selected[0] : null, selected.Count);
        var focus = User32.GetFocus();
        Enable(RetryId, states.Retry);
        Enable(ClearSelectedId, states.ClearSelected);
        Enable(ClearAllId, states.ClearAll);
        // Điều khiển đang giữ focus bị khoá thì chuyển focus về danh sách (Tab/Esc vẫn dùng được).
        if (focus != 0 && !User32.IsWindowEnabled(focus))
        {
            User32.SetFocus(ListHandle);
        }
    }

    private void Enable(int id, bool enabled)
    {
        var child = Item(id);
        if (child != 0)
        {
            User32.EnableWindow(child, enabled);
        }
    }

    internal bool IsEnabled(int id) => Item(id) != 0 && User32.IsWindowEnabled(Item(id));

    /// <summary>Màu chữ (COLORREF) hiện dùng cho một STATIC/ô chỉ đọc (test: màu verdict/trạng thái theo tone).</summary>
    internal uint TextColorOf(int id) => StaticTextColor(id);

    protected override bool OnCommand(int id, int notification)
    {
        switch (id)
        {
            case FilterId when notification == ControlConstants.CbnSelChange:
                RefreshEntries();
                return true;
            case ListId when notification == ControlConstants.LbnSelChange:
                OnSelectionChanged();
                return true;
        }

        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        switch (id)
        {
            case RecheckId:
                StartChecks();
                return true;
            case ClearSelectedId:
                Dismiss(SelectedRows().ToList());
                return true;
            case ClearAllId:
                Dismiss(_presenter.Rows.ToList());
                return true;
            case RetryId:
                Observe(LastRetryTask = RetryAsync(), "Recovery retry failed");
                return true;
            case CopySourceId:
                Copy(_sourcePath);
                return true;
            case ShowSourceId:
                ShowInExplorer(_sourcePath);
                return true;
            case CopyDestinationId:
                Copy(_destinationPath);
                return true;
            case ShowDestinationId:
                ShowInExplorer(_destinationPath);
                return true;
            default:
                return false;
        }
    }

    // ---- kiểm tra nền ---------------------------------------------------------------------------------------------------------

    /// <summary>Kiểm tra nền chỉ đọc mọi hàng (<see cref="RecoveryWindowPresenter.RunChecksAsync"/>): nút Re-check + lúc mở.</summary>
    internal void StartChecks()
    {
        LastCheckTask = _presenter.RunChecksAsync(this);
        Observe(LastCheckTask, "Recovery check failed");
    }

    void IRecoveryCheckView.SetCheckStatus(string text)
    {
        if (Hwnd != 0)
        {
            SetItemText(CheckStatusId, text);
        }
    }

    void IRecoveryCheckView.SetRecheckEnabled(bool enabled)
    {
        if (Hwnd == 0)
        {
            return;
        }

        var focus = User32.GetFocus();
        Enable(RecheckId, enabled);
        if (!enabled && focus != 0 && focus == Item(RecheckId))
        {
            User32.SetFocus(ListHandle);
        }
    }

    void IRecoveryCheckView.CheckCompleted()
    {
        if (Hwnd != 0)
        {
            RefreshEntries();
        }
    }

    // ---- Clear (dismiss) --------------------------------------------------------------------------------------------------------

    private void Dismiss(List<RecoveryRowModel> rows)
    {
        var prompt = _presenter.DismissPrompt(rows);
        if (prompt is null)
        {
            return;
        }

        if (!Confirm(Tr.RecoveryDismissConfirmTitle, prompt))
        {
            return;
        }

        var result = _presenter.Dismiss(rows);
        if (result.Error is not null)
        {
            Notify(Tr.RecoveryDismissFailedTitle, result.Error, warning: true);
            return;
        }

        if (Hwnd == 0)
        {
            return;
        }

        RefreshEntries();
        if (result.SkippedCount > 0)
        {
            Notify(Tr.RecoveryDismissStaleTitle, Tr.RecoveryDismissStaleMessage(result.SkippedCount), warning: false);
        }
    }

    // ---- Retry --------------------------------------------------------------------------------------------------------------------

    private bool Alive => Hwnd != 0 && !IsClosed;

    /// <summary>
    /// Retry của hàng đang chọn, đúng thứ tự của <c>Retry_Click</c> WPF: điều kiện bắt đầu -> xác nhận -> một lần gọi delegate (UI
    /// thread rảnh: Retry/Clear khoá, con trỏ chờ) -> thông báo theo <see cref="RecoveryWindowPresenter.DescribeRetry"/> ->
    /// đóng (thành công) hoặc kiểm tra lại (bị từ chối).
    /// </summary>
    private async Task RetryAsync()
    {
        var row = SingleSelected();
        if (!_presenter.CanStartRetry(row))
        {
            return;
        }

        var entry = row!.Entry;
        var confirm = _presenter.RetryConfirmText(row);
        if (!Confirm(Tr.DialogConfirmRetryTitle, confirm))
        {
            return;
        }

        RecoveryRetryResult result;
        try
        {
            result = await _presenter.ExecuteRetryAsync(entry, SetBusy, OnRowsChangedByPresenter);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Alive)
            {
                Notify(Tr.DialogRetryRejectedTitle, ex.Message, warning: true);
            }
            else
            {
                AppLog.Error("Recovery retry failed after the window closed", ex);
            }

            return;
        }

        if (!Alive)
        {
            // Phòng thủ: cửa sổ chỉ đóng giữa chừng khi shell thoát; kết quả (kể cả cảnh báo journal) ít nhất phải vào nhật ký.
            AppLog.Info("Recovery retry finished after the window closed: " + result.Message);
            return;
        }

        var notice = RecoveryWindowPresenter.DescribeRetry(result);
        Notify(notice.Title, notice.Message, notice.IsWarning);
        if (!Alive)
        {
            return;
        }

        if (notice.CloseWindow)
        {
            Close();
        }
        else if (notice.Recheck)
        {
            StartChecks();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (Hwnd == 0)
        {
            return;
        }

        UpdateButtons();
        // Con trỏ chờ có hiệu lực ở WM_SETCURSOR kế tiếp; ép cập nhật ngay.
        User32.SetCursor(User32.LoadCursor(0, busy ? CursorWait : CursorArrow));
    }

    private void OnRowsChangedByPresenter()
    {
        if (Alive)
        {
            RefreshEntries();
        }
    }

    // ---- hộp thoại xác nhận / thông báo / sao chép / Explorer ------------------------------------------------------------------------

    private bool Confirm(string title, string message)
        => _messages.Show(Hwnd, title, message, TaskDialogKind.Confirmation) == WindowMessages.IdYes;

    private void Notify(string title, string message, bool warning)
        => _messages.Show(Hwnd, title, message, warning ? TaskDialogKind.Error : TaskDialogKind.Information);

    private void Copy(string path)
    {
        if (path.Length > 0)
        {
            _ = _copy(path); // clipboard bận: người dùng vẫn chọn chữ + Ctrl+C được (như WPF)
        }
    }

    /// <summary>"Show in Explorer": mở Explorer tại tệp, hoặc thư mục tồn tại gần nhất (chỉ đọc, như <c>RecoveryPathPanel</c>).</summary>
    private void ShowInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                _explorer($"/select,\"{path}\"");
                return;
            }

            var folder = RecoveryPresentation.NearestExistingFolder(path, Directory.Exists);
            if (folder is null)
            {
                return;
            }

            _explorer($"\"{folder}\"");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Notify(Tr.RecoveryExplorerFailedTitle, ex.Message, warning: true);
        }
    }

    private static void StartExplorer(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    /// <summary>Task của presenter chạy tiếp trên UI thread; presenter lo lỗi của nó, view chỉ ghi log nếu còn ngoại lệ lọt ra.</summary>
    private static void Observe(Task task, string what)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                AppLog.Error(what, task.Exception!.GetBaseException());
            }

            return;
        }

        _ = task.ContinueWith(
            t => AppLog.Error(what, t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ---- bố cục -----------------------------------------------------------------------------------------------------------------

    private int Margin => Dx(10);

    private int FieldHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(700), Dy(330));

    protected override (int Width, int Height) MinimumClientSize()
    {
        var buttons = ButtonRowWidth(ClearSelectedId, ClearAllId) + Dx(16) + ButtonRowWidth(RetryId, CloseId) + (2 * Margin);
        var pane = Dx(150) + Dx(10) + (2 * ButtonWidth(CopySourceId, ShowSourceId)) + Dx(80) + Dx(8) + (2 * Margin);
        return (Math.Max(Math.Max(Dx(440), buttons), pane), Dy(220));
    }

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var m = Margin;
        var content = Math.Max(1, clientWidth - (2 * m));
        var y = m;

        var header = MeasureItemHeight(HeaderId, content);
        Place(HeaderId, m, y, content, header);
        y += header + Dy(2);
        var summary = MeasureItemHeight(SummaryId, content);
        Place(SummaryId, m, y, content, summary);
        y += summary + Dy(6);

        // Hàng lọc: nhãn + combobox + Re-check + trạng thái kiểm tra.
        var label = MeasureTextWidth(GetItemText(FilterLabelId));
        var rowHeight = Math.Max(FieldHeight, ButtonHeight);
        Place(FilterLabelId, m, y + ((rowHeight - LineHeight) / 2), label + Dx(2), LineHeight);
        var comboX = m + label + Dx(8);
        var comboWidth = Dx(85);
        Place(FilterId, comboX, y, comboWidth, FieldHeight + (4 * (LineHeight + Dy(2))));
        var recheckX = comboX + comboWidth + Dx(8);
        var recheckWidth = ButtonWidth(RecheckId);
        Place(RecheckId, recheckX, y, recheckWidth, rowHeight);
        var statusX = recheckX + recheckWidth + Dx(8);
        Place(CheckStatusId, statusX, y + ((rowHeight - LineHeight) / 2), Math.Max(Dx(40), clientWidth - m - statusX), LineHeight);
        y += rowHeight + Dy(6);

        // Hàng nút dưới cùng: [Clear selected][Clear all]   [Retry][Close].
        var bottomTop = clientHeight - m - ButtonHeight;
        var rightRow = PlaceButtonRow(clientWidth, m, bottomTop, ButtonHeight, RetryId, CloseId);
        PlaceButtonRow(clientWidth, m + rightRow + Dx(16), bottomTop, ButtonHeight, ClearSelectedId, ClearAllId);
        var contentBottom = bottomTop - Dy(8);

        // Hai cột: danh sách (5/11) và chi tiết (6/11).
        var gap = Dx(10);
        var leftWidth = Math.Max(Dx(150), (content - gap) * 5 / 11);
        var rightX = m + leftWidth + gap;
        var rightWidth = Math.Max(Dx(150), clientWidth - m - rightX);
        var height = Math.Max(Dy(30), contentBottom - y);
        Place(ListId, m, y, leftWidth, height);
        SetListItemHeight(ListId, (2 * LineHeight) + Dy(5));
        var emptyHeight = MeasureItemHeight(FilterEmptyId, leftWidth);
        Place(FilterEmptyId, m, y + Math.Max(0, (height - emptyHeight) / 2), leftWidth, emptyHeight);

        LayoutDetails(rightX, y, rightWidth, contentBottom);
    }

    private void LayoutDetails(int x, int top, int width, int bottom)
    {
        var detailsEmpty = MeasureItemHeight(DetailsEmptyId, width);
        Place(DetailsEmptyId, x, top + Math.Max(0, (bottom - top - detailsEmpty) / 2), width, detailsEmpty);
        if (!_hasDetails)
        {
            return;
        }

        var y = top;
        var verdictLabel = MeasureTextWidth(GetItemText(VerdictLabelId));
        Place(VerdictLabelId, x, y, verdictLabel + Dx(2), LineHeight);
        Place(VerdictBadgeId, x + verdictLabel + Dx(8), y, Math.Max(Dx(40), width - verdictLabel - Dx(8)), LineHeight);
        y += LineHeight + Dy(6);

        if (_errorVisible)
        {
            var error = MeasureItemHeight(ErrorId, width);
            Place(ErrorId, x, y, width, error);
            y += error + Dy(6);
        }

        if (_sourceVisible)
        {
            y = PlacePathGroup(x, y, width, SourceTitleId, SourceStatusId, SourcePathId, CopySourceId, ShowSourceId);
        }

        if (_destinationVisible)
        {
            y = PlacePathGroup(x, y, width, DestinationTitleId, DestinationStatusId, DestinationPathId, CopyDestinationId, ShowDestinationId);
        }

        if (_noDestinationVisible)
        {
            var none = MeasureItemHeight(NoDestinationId, width);
            Place(NoDestinationId, x, y, width, none);
            y += none + Dy(6);
        }

        Place(DetailsId, x, y, width, Math.Max(Dy(30), bottom - y));
    }

    private int PlacePathGroup(int x, int y, int width, int titleId, int statusId, int pathId, int copyId, int showId)
    {
        var title = MeasureTextWidth(GetItemText(titleId));
        Place(titleId, x, y, title + Dx(2), LineHeight);
        Place(statusId, x + title + Dx(10), y, Math.Max(Dx(40), width - title - Dx(10)), LineHeight);
        y += LineHeight + Dy(3);

        var button = ButtonWidth(copyId, showId);
        var gap = Dx(4);
        var pathWidth = Math.Max(Dx(60), width - (2 * button) - (2 * gap));
        var text = GetItemText(pathId);
        // Đường dẫn dài xuống dòng như TextBox WPF; tối đa 6 dòng (đủ MAX_PATH), nút Copy path luôn chép đủ.
        var wrapped = MeasureTextHeight(text, Math.Max(1, pathWidth - Dx(6)), BodyFont);
        var pathHeight = Math.Clamp(wrapped + Dy(4), FieldHeight, (6 * LineHeight) + Dy(4));
        Place(pathId, x, y, pathWidth, pathHeight);
        Place(copyId, x + pathWidth + gap, y, button, ButtonHeight);
        Place(showId, x + pathWidth + gap + button + gap, y, button, ButtonHeight);
        return y + pathHeight + Dy(8);
    }
}
