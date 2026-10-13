using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Hộp thoại cảnh báo của các view Win32 (TaskDialog thật; test thay bằng bản ghi lại).</summary>
internal interface IDialogWarningSink
{
    /// <summary>Hiện cảnh báo modal trên <paramref name="owner"/>; chặn tới khi người dùng đóng.</summary>
    void Warn(nint owner, string title, string message);
}

/// <summary><see cref="IDialogWarningSink"/> thật: <c>TaskDialogIndirect</c> (biểu tượng cảnh báo, một nút OK) như <c>MessageBox</c> của bản WPF.</summary>
internal sealed class TaskDialogWarningSink : IDialogWarningSink
{
    public static TaskDialogWarningSink Instance { get; } = new();

    public void Warn(nint owner, string title, string message)
        => new NativeTaskDialogPort().Show(owner, title, message, TaskDialogKind.Error);
}

/// <summary>"Tên|mẫu|tên|mẫu" (chuỗi bộ lọc kiểu WPF của catalog) -&gt; danh sách <see cref="FileDialogFilter"/>.</summary>
internal static class FileFilterText
{
    public static IReadOnlyList<FileDialogFilter> Parse(string text)
    {
        var parts = (text ?? string.Empty).Split('|');
        var filters = new List<FileDialogFilter>(parts.Length / 2);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            filters.Add(new FileDialogFilter(parts[i], parts[i + 1]));
        }

        return filters;
    }
}

/// <summary>
/// WP-35 GĐ2 (phương án B, CHƯA XÁC NHẬN): Action profiles - view Win32 MODAL của <see cref="ActionProfilesPresenter"/> (WPF:
/// <c>ActionProfilesWindow</c>). Danh sách (listbox owner-draw, huy hiệu = phím tắt) + Add/Remove/Import/Export; trường chi tiết
/// (tên, phím tắt, thao tác = combobox Move/Copy/Recycle, thư mục đích, xác nhận); Cancel/Apply. Presenter lo mọi quy tắc; view
/// chỉ chép giá trị. Modal-trong-modal đúng: hộp thoại nào cũng tự quản owner nên Settings (GĐ3-4) mở được nó bên trong.
/// </summary>
internal sealed class ActionProfilesDialog : NativeDialog
{
    internal const int ListHeaderId = 100;
    internal const int ListId = 101;
    internal const int AddId = 102;
    internal const int RemoveId = 103;
    internal const int ImportId = 104;
    internal const int ExportId = 105;
    internal const int DetailsHeaderId = 106;
    internal const int NameLabelId = 110;
    internal const int NameId = 111;
    internal const int ShortcutLabelId = 112;
    internal const int ShortcutId = 113;
    internal const int OperationLabelId = 114;
    internal const int OperationId = 115;
    internal const int DestinationLabelId = 116;
    internal const int DestinationId = 117;
    internal const int ConfirmId = 118;
    internal const int CancelId = IdCancel;
    internal const int ApplyId = IdOk;

    private const uint BsMultiline = 0x2000;

    private readonly ActionProfilesPresenter _presenter;
    private readonly IShellFileDialog? _files;
    private readonly IDialogWarningSink _warnings;

    public ActionProfilesDialog(IEnumerable<ReviewAction> actions, IShellFileDialog? files = null, IDialogWarningSink? warnings = null,
        INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _presenter = new ActionProfilesPresenter(actions);
        _files = files;
        _warnings = warnings ?? TaskDialogWarningSink.Instance;
        Items = BuildItems();
    }

    /// <summary>Danh sách sau khi sửa (hợp lệ sau Apply = <see cref="NativeDialog.IdOk"/>).</summary>
    public IReadOnlyList<ReviewAction> Actions => _presenter.Actions;

    protected override string Title => Tr.ActionProfilesTitle;

    protected override IReadOnlyList<DialogItem> Items { get; }

    protected override bool Resizable => true;

    protected override IReadOnlyList<int> HeaderControlIds => [ListHeaderId, DetailsHeaderId];

    protected override int InitialFocusId => ListId;

    protected override int DefaultButtonId => ApplyId;

    private static List<DialogItem> BuildItems()
    {
        const uint child = DialogStyles.Child;
        const uint tab = DialogConstants.WsTabStop;
        const uint text = DialogStyles.Child | DialogConstants.SsNoPrefix;
        const uint edit = child | tab | DialogConstants.EsAutoHScroll;
        return
        [
            new(DialogControlClass.Static, ListHeaderId, Tr.ActionProfilesListHeader, text),
            new(DialogControlClass.ListBox, ListId, string.Empty,
                child | tab | DialogConstants.WsVScroll | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight
                | ControlConstants.LbsOwnerDrawFixed | ControlConstants.LbsHasStrings, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, AddId, Tr.ActionProfilesAdd, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, RemoveId, Tr.ActionProfilesRemove, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ImportId, Tr.ActionProfilesImport, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ExportId, Tr.ActionProfilesExport, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, DetailsHeaderId, Tr.ActionProfilesDetailsHeader, text),
            new(DialogControlClass.Static, NameLabelId, Tr.ActionProfilesNameLabel, text),
            new(DialogControlClass.Edit, NameId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, ShortcutLabelId, Tr.ActionProfilesShortcutLabel, text),
            new(DialogControlClass.Edit, ShortcutId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, OperationLabelId, Tr.ActionProfilesOperationLabel, text),
            new(DialogControlClass.ComboBox, OperationId, string.Empty, child | ComboBoxControl.DefaultStyle),
            new(DialogControlClass.Static, DestinationLabelId, Tr.ActionProfilesDestinationLabel, text),
            new(DialogControlClass.Edit, DestinationId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, ConfirmId, Tr.ActionProfilesConfirmLabel,
                child | tab | ControlConstants.BsAutoCheckBox | BsMultiline),
            new(DialogControlClass.Button, CancelId, Tr.CommonCancel, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ApplyId, Tr.CommonApply, child | tab | DialogConstants.BsDefPushButton),
        ];
    }

    // ---- danh sách / chi tiết -------------------------------------------------------------------------------------------

    protected override void OnInitialized()
    {
        var combo = Item(OperationId);
        // Thứ tự khớp ActionProfilesPresenter.OperationToIndex: 0 Move, 1 Copy, 2 Recycle.
        ComboBoxControl.Add(combo, Tr.EnumFileOperationMove);
        ComboBoxControl.Add(combo, Tr.EnumFileOperationCopy);
        ComboBoxControl.Add(combo, Tr.EnumFileOperationRecycle);
        ComboBoxControl.Select(combo, 0);
        FillList();
        if (_presenter.Actions.Count > 0)
        {
            SelectIndex(0);
        }
    }

    private nint ListHandle => Item(ListId);

    private void FillList()
    {
        var list = ListHandle;
        var top = (int)User32.SendMessage(list, ControlConstants.LbGetTopIndex, 0, 0);
        ListBoxControl.Clear(list);
        foreach (var action in _presenter.Actions)
        {
            ListBoxControl.Add(list, action.Name);
        }

        if (top > 0 && top < _presenter.Actions.Count)
        {
            User32.SendMessage(list, ControlConstants.LbSetTopIndex, top, 0);
        }
    }

    /// <summary>Chữ + huy hiệu (phím tắt) của dòng <paramref name="index"/> (test).</summary>
    internal (string Text, string? Badge) RowText(int index) => ListItemText(ListId, index);

    protected override (string Text, string? Badge) ListItemText(int id, int index)
    {
        if (id == ListId && index >= 0 && index < _presenter.Actions.Count)
        {
            var action = _presenter.Actions[index];
            return (action.Name, string.IsNullOrEmpty(action.Shortcut) ? null : action.Shortcut);
        }

        return base.ListItemText(id, index);
    }

    private ActionProfileForm CurrentFields() => new(
        GetItemText(NameId),
        GetItemText(ShortcutId),
        Math.Max(0, ComboBoxControl.SelectedIndex(Item(OperationId))),
        GetItemText(DestinationId),
        ButtonStateControl.IsChecked(Item(ConfirmId)));

    /// <summary>Ghi các trường vào hàng đang mở rồi vẽ lại danh sách (chữ + huy hiệu), giữ chọn như <c>Items.Refresh()</c>.</summary>
    private void SaveCurrent()
    {
        if (_presenter.SaveCurrent(CurrentFields()))
        {
            RefreshList();
        }
    }

    private void RefreshList()
    {
        var list = ListHandle;
        var selected = ListBoxControl.SelectedIndex(list);
        FillList();
        if (selected >= 0 && selected < _presenter.Actions.Count)
        {
            ListBoxControl.SelectSingle(list, selected);
        }
    }

    /// <summary>Chọn dòng <paramref name="index"/> rồi chạy đúng thứ tự của handler chọn cũ: lưu hàng trước, mở hàng mới.</summary>
    private void SelectIndex(int index)
    {
        ListBoxControl.SelectSingle(ListHandle, index);
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        SaveCurrent();
        var index = ListBoxControl.SelectedIndex(ListHandle);
        var selected = index >= 0 && index < _presenter.Actions.Count ? _presenter.Actions[index] : null;
        var fields = _presenter.Open(selected);
        if (fields is null)
        {
            return;
        }

        SetItemText(NameId, fields.Name);
        SetItemText(ShortcutId, fields.Shortcut);
        SetItemText(DestinationId, fields.Destination);
        ButtonStateControl.SetChecked(Item(ConfirmId), fields.Confirm);
        ComboBoxControl.Select(Item(OperationId), fields.OperationIndex);
    }

    protected override bool OnCommand(int id, int notification)
    {
        switch (id)
        {
            case ListId when notification == ControlConstants.LbnSelChange:
                OnSelectionChanged();
                return true;
            case AddId when notification == DialogConstants.BnClicked:
                AddAction();
                return true;
            case RemoveId when notification == DialogConstants.BnClicked:
                RemoveAction();
                return true;
            case ImportId when notification == DialogConstants.BnClicked:
                ImportActions();
                return true;
            case ExportId when notification == DialogConstants.BnClicked:
                ExportActions();
                return true;
            default:
                return false;
        }
    }

    private void AddAction()
    {
        SaveCurrent();
        var action = _presenter.Add();
        FillList();
        SelectIndex(_presenter.Actions.IndexOf(action));
    }

    private void RemoveAction()
    {
        var index = ListBoxControl.SelectedIndex(ListHandle);
        if (index < 0 || index >= _presenter.Actions.Count)
        {
            return;
        }

        var next = _presenter.Remove(_presenter.Actions[index], index);
        FillList();
        if (next >= 0)
        {
            SelectIndex(next);
            return;
        }

        // Không còn gì để sửa: bỏ giá trị của hành động vừa xoá khỏi các trường chi tiết.
        SetItemText(NameId, string.Empty);
        SetItemText(ShortcutId, string.Empty);
        SetItemText(DestinationId, string.Empty);
        ButtonStateControl.SetChecked(Item(ConfirmId), false);
    }

    private void ImportActions()
    {
        if (_files is null)
        {
            return;
        }

        var path = _files.PickOpenFile(Hwnd, Tr.ActionProfilesImport, FileFilterText.Parse(Tr.DialogFileFilterJsonOrAll), null);
        if (path is null)
        {
            return;
        }

        var imported = ActionProfilesPresenter.TryImport(path, out var error);
        if (imported is null)
        {
            _warnings.Warn(Hwnd, Tr.DialogImportActionsFailedTitle, error);
            return;
        }

        _presenter.ReplaceAll(imported);
        FillList();
        SelectIndex(0);
    }

    private void ExportActions()
    {
        SaveCurrent();
        if (_files is null)
        {
            return;
        }

        var path = _files.PickSaveFile(Hwnd, Tr.ActionProfilesExport, FileFilterText.Parse(Tr.DialogFileFilterJson), "json",
            "photoreview-actions.json", null);
        if (path is null)
        {
            return;
        }

        if (ActionProfilesPresenter.TryExport(path, _presenter.Actions) is { } error)
        {
            _warnings.Warn(Hwnd, Tr.DialogExportActionsFailedTitle, error);
        }
    }

    protected override bool OnOk()
    {
        SaveCurrent();
        var result = _presenter.Apply();
        if (!result.Accepted)
        {
            _warnings.Warn(Hwnd, Tr.DialogActionProfilesInvalidTitle, result.Message ?? string.Empty);
            return false;
        }

        return true;
    }

    // ---- bố cục -----------------------------------------------------------------------------------------------------------

    private int Margin => Dx(10);

    private int FieldHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int LeftWidth(int clientWidth) => Math.Clamp(clientWidth / 3, Dx(100), Dx(170));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(440), Dy(230));

    protected override (int Width, int Height) MinimumClientSize()
        => (Math.Max(Dx(340), ButtonRowWidth(CancelId, ApplyId) + (2 * Margin)), Dy(210));

    protected override void LayoutControls(int clientWidth, int clientHeight)
    {
        var m = Margin;
        var leftWidth = LeftWidth(clientWidth);
        var rightX = m + leftWidth + m;
        var rightWidth = Math.Max(Dx(60), clientWidth - rightX - m);
        var buttonHeight = ButtonHeight;

        var bottomTop = clientHeight - m - buttonHeight;
        PlaceButtonRow(clientWidth, m, bottomTop, buttonHeight, CancelId, ApplyId);
        var contentBottom = bottomTop - Dy(8);

        // Cột trái: tiêu đề, danh sách, hai hàng nút 2 x 2.
        var y = m;
        var header = MeasureItemHeight(ListHeaderId, leftWidth);
        Place(ListHeaderId, m, y, leftWidth, header);
        y += header + Dy(4);
        var gap = Dx(4);
        var half = (leftWidth - gap) / 2;
        var row2 = contentBottom - buttonHeight;
        var row1 = row2 - Dy(4) - buttonHeight;
        Place(ListId, m, y, leftWidth, Math.Max(Dy(30), row1 - Dy(4) - y));
        SetListItemHeight(ListId, LineHeight + Dy(5));
        Place(AddId, m, row1, half, buttonHeight);
        Place(RemoveId, m + half + gap, row1, leftWidth - half - gap, buttonHeight);
        Place(ImportId, m, row2, half, buttonHeight);
        Place(ExportId, m + half + gap, row2, leftWidth - half - gap, buttonHeight);

        // Cột phải: tiêu đề + trường chi tiết xếp dọc.
        y = m;
        var details = MeasureItemHeight(DetailsHeaderId, rightWidth);
        Place(DetailsHeaderId, rightX, y, rightWidth, details);
        y += details + Dy(8);
        y = PlaceField(NameLabelId, NameId, rightX, y, rightWidth, FieldHeight);
        y = PlaceField(ShortcutLabelId, ShortcutId, rightX, y, rightWidth, FieldHeight);
        var comboHeight = FieldHeight + (4 * (LineHeight + Dy(2)));
        var operationLabel = MeasureItemHeight(OperationLabelId, rightWidth);
        Place(OperationLabelId, rightX, y, rightWidth, operationLabel);
        Place(OperationId, rightX, y + operationLabel + Dy(2), rightWidth, comboHeight);
        y += operationLabel + Dy(2) + FieldHeight + Dy(8);
        y = PlaceField(DestinationLabelId, DestinationId, rightX, y, rightWidth, FieldHeight);
        var box = Dx(14);
        var confirm = Math.Max(FieldHeight, MeasureTextHeight(GetItemText(ConfirmId), Math.Max(1, rightWidth - box - Dx(4)), BodyFont));
        Place(ConfirmId, rightX, y + Dy(2), rightWidth, confirm);
    }

    private int PlaceField(int labelId, int editId, int x, int y, int width, int editHeight)
    {
        var label = MeasureItemHeight(labelId, width);
        Place(labelId, x, y, width, label);
        y += label + Dy(2);
        Place(editId, x, y, width, editHeight);
        return y + editHeight + Dy(8);
    }
}
