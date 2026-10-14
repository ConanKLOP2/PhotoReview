using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Kết quả của <see cref="ExternalEditorsDialog"/>: danh sách trình chỉnh sửa đã sửa + id của trình mặc định.</summary>
internal sealed record ExternalEditorsEdit(IReadOnlyList<ExternalEditor> Editors, string DefaultId);

/// <summary>
/// FEAT-EXTERNAL-EDITORS: quản lý danh sách trình chỉnh sửa ngoài (MODAL, mở từ trang Files của Settings). Cột trái: danh sách (huy
/// hiệu "default") + Add (chọn .exe)/Remove/Set default; cột phải: tên, chương trình (+ Browse), tham số (<c>{file}</c>). Cancel/Apply.
/// Dialog chỉ chép giá trị; quy tắc (tên gợi ý, mặc định, trùng id) ở <see cref="ExternalEditors"/> (Core). Cách làm theo
/// <see cref="ActionProfilesDialog"/>.
/// </summary>
internal sealed class ExternalEditorsDialog : NativeDialog
{
    internal const int ListHeaderId = 100;
    internal const int ListId = 101;
    internal const int AddId = 102;
    internal const int RemoveId = 103;
    internal const int SetDefaultId = 104;
    internal const int DetailsHeaderId = 106;
    internal const int NameLabelId = 110;
    internal const int NameId = 111;
    internal const int PathLabelId = 112;
    internal const int PathId = 113;
    internal const int BrowseId = 114;
    internal const int ArgumentsLabelId = 116;
    internal const int ArgumentsId = 117;
    internal const int HintId = 118;
    internal const int CancelId = IdCancel;
    internal const int ApplyId = IdOk;

    private readonly List<ExternalEditor> _editors;
    private readonly IShellFileDialog? _files;
    private readonly IDialogWarningSink _warnings;
    private readonly Func<string, string?> _suggestName;
    private string _defaultId;
    private int _open = -1;

    /// <param name="editors">Danh sách hiện có (được sao chép; dialog không sửa bản gốc).</param>
    /// <param name="defaultId">Id trình mặc định hiện tại.</param>
    /// <param name="files">Hộp thoại chọn tệp (null = nút Add/Browse không làm gì).</param>
    /// <param name="suggestName">Tên gợi ý từ đường dẫn (ProductName của tệp); null = tên tệp.</param>
    public ExternalEditorsDialog(IEnumerable<ExternalEditor> editors, string? defaultId, IShellFileDialog? files = null,
        IDialogWarningSink? warnings = null, Func<string, string?>? suggestName = null, INativeDialogChrome? chrome = null)
        : base(chrome)
    {
        _editors = [.. editors.Select(editor => editor.Clone())];
        _defaultId = defaultId ?? string.Empty;
        _files = files;
        _warnings = warnings ?? TaskDialogWarningSink.Instance;
        _suggestName = suggestName ?? (_ => null);
        Items = BuildItems();
    }

    /// <summary>Kết quả sau khi đóng bằng Apply (<see cref="NativeDialog.IdOk"/>).</summary>
    public ExternalEditorsEdit Outcome => new(_editors.Select(editor => editor.Clone()).ToList(), EffectiveDefaultId());

    protected override string Title => Tr.ExternalEditorsTitle;

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
            new(DialogControlClass.Static, ListHeaderId, Tr.ExternalEditorsListHeader, text),
            new(DialogControlClass.ListBox, ListId, string.Empty,
                child | tab | DialogConstants.WsVScroll | DialogConstants.LbsNotify | DialogConstants.LbsNoIntegralHeight
                | ControlConstants.LbsOwnerDrawFixed | ControlConstants.LbsHasStrings, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, AddId, Tr.ExternalEditorsAdd, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, RemoveId, Tr.ExternalEditorsRemove, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, SetDefaultId, Tr.ExternalEditorsSetDefault, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, DetailsHeaderId, Tr.ExternalEditorsDetailsHeader, text),
            new(DialogControlClass.Static, NameLabelId, Tr.ExternalEditorsNameLabel, text),
            new(DialogControlClass.Edit, NameId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, PathLabelId, Tr.ExternalEditorsPathLabel, text),
            new(DialogControlClass.Edit, PathId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Button, BrowseId, Tr.ExternalEditorsBrowse, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Static, ArgumentsLabelId, Tr.ExternalEditorsArgumentsLabel, text),
            new(DialogControlClass.Edit, ArgumentsId, string.Empty, edit, DialogConstants.WsExClientEdge),
            new(DialogControlClass.Static, HintId, Tr.ExternalEditorsArgumentsHint, text),
            new(DialogControlClass.Button, CancelId, Tr.CommonCancel, child | tab | DialogConstants.BsPushButton),
            new(DialogControlClass.Button, ApplyId, Tr.CommonApply, child | tab | DialogConstants.BsDefPushButton),
        ];
    }

    // ---- danh sách / chi tiết -------------------------------------------------------------------------------------------

    protected override void OnInitialized()
    {
        FillList();
        if (_editors.Count > 0)
        {
            SelectIndex(IndexOfDefault() is var index and >= 0 ? index : 0);
        }
    }

    private nint ListHandle => Item(ListId);

    private string EffectiveDefaultId()
        => _editors.Any(editor => string.Equals(editor.Id, _defaultId, StringComparison.Ordinal)) ? _defaultId : _editors.FirstOrDefault()?.Id ?? string.Empty;

    private int IndexOfDefault()
    {
        var id = EffectiveDefaultId();
        return _editors.FindIndex(editor => string.Equals(editor.Id, id, StringComparison.Ordinal));
    }

    private void FillList()
    {
        var list = ListHandle;
        var top = (int)User32.SendMessage(list, ControlConstants.LbGetTopIndex, 0, 0);
        ListBoxControl.Clear(list);
        foreach (var editor in _editors)
        {
            ListBoxControl.Add(list, editor.DisplayName);
        }

        if (top > 0 && top < _editors.Count)
        {
            User32.SendMessage(list, ControlConstants.LbSetTopIndex, top, 0);
        }
    }

    /// <summary>Chữ + huy hiệu của dòng <paramref name="index"/> (test).</summary>
    internal (string Text, string? Badge) RowText(int index) => ListItemText(ListId, index);

    protected override (string Text, string? Badge) ListItemText(int id, int index)
    {
        if (id == ListId && index >= 0 && index < _editors.Count)
        {
            var editor = _editors[index];
            return (editor.DisplayName, index == IndexOfDefault() ? Tr.ExternalEditorsDefaultBadge : null);
        }

        return base.ListItemText(id, index);
    }

    /// <summary>Ghi các trường vào editor đang mở rồi vẽ lại danh sách (tên có thể đổi).</summary>
    private void SaveCurrent()
    {
        if (_open < 0 || _open >= _editors.Count)
        {
            return;
        }

        var editor = _editors[_open];
        editor.Name = GetItemText(NameId).Trim();
        editor.Path = GetItemText(PathId).Trim();
        editor.Arguments = GetItemText(ArgumentsId);
        RefreshList();
    }

    private void RefreshList()
    {
        var list = ListHandle;
        var selected = ListBoxControl.SelectedIndex(list);
        FillList();
        if (selected >= 0 && selected < _editors.Count)
        {
            ListBoxControl.SelectSingle(list, selected);
        }
    }

    private void SelectIndex(int index)
    {
        ListBoxControl.SelectSingle(ListHandle, index);
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        SaveCurrent();
        var index = ListBoxControl.SelectedIndex(ListHandle);
        if (index < 0 || index >= _editors.Count)
        {
            return;
        }

        _open = index;
        var editor = _editors[index];
        SetItemText(NameId, editor.Name);
        SetItemText(PathId, editor.Path);
        SetItemText(ArgumentsId, editor.Arguments);
    }

    protected override bool OnCommand(int id, int notification)
    {
        switch (id)
        {
            case ListId when notification == ControlConstants.LbnSelChange:
                OnSelectionChanged();
                return true;
            case AddId when notification == DialogConstants.BnClicked:
                AddEditor();
                return true;
            case RemoveId when notification == DialogConstants.BnClicked:
                RemoveEditor();
                return true;
            case SetDefaultId when notification == DialogConstants.BnClicked:
                SetDefault();
                return true;
            case BrowseId when notification == DialogConstants.BnClicked:
                Browse();
                return true;
            default:
                return false;
        }
    }

    private string? PickExecutable(string currentPath)
    {
        if (_files is null)
        {
            return null;
        }

        string? initial = null;
        try
        {
            initial = !string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath) ? Path.GetDirectoryName(currentPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            // the system default folder
        }

        try
        {
            return _files.PickOpenFile(Hwnd, Tr.DialogExternalEditorAddTitle, FileFilterText.Parse(Tr.DialogFileFilterExecutable), initial);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _warnings.ShowWarning(Hwnd, Tr.DialogSettingsOpenFailedTitle, ex.Message);
            return null;
        }
    }

    private void AddEditor()
    {
        SaveCurrent();
        var picked = PickExecutable(string.Empty);
        if (string.IsNullOrWhiteSpace(picked) || _editors.Count >= ExternalEditors.MaxEditors)
        {
            return;
        }

        var editor = new ExternalEditor { Path = picked, Name = _suggestName(picked) ?? ExternalEditors.NameFromPath(picked) };
        _editors.Add(editor);
        if (_editors.Count == 1)
        {
            _defaultId = editor.Id;
        }

        FillList();
        SelectIndex(_editors.Count - 1);
    }

    private void RemoveEditor()
    {
        var index = ListBoxControl.SelectedIndex(ListHandle);
        if (index < 0 || index >= _editors.Count)
        {
            return;
        }

        var removed = _editors[index];
        _open = -1; // the removed row's fields must not be written back
        _editors.RemoveAt(index);
        if (string.Equals(removed.Id, _defaultId, StringComparison.Ordinal))
        {
            _defaultId = _editors.FirstOrDefault()?.Id ?? string.Empty;
        }

        FillList();
        if (_editors.Count > 0)
        {
            SelectIndex(Math.Min(index, _editors.Count - 1));
            return;
        }

        SetItemText(NameId, string.Empty);
        SetItemText(PathId, string.Empty);
        SetItemText(ArgumentsId, string.Empty);
    }

    private void SetDefault()
    {
        SaveCurrent();
        var index = ListBoxControl.SelectedIndex(ListHandle);
        if (index < 0 || index >= _editors.Count)
        {
            return;
        }

        _defaultId = _editors[index].Id;
        RefreshList();
    }

    private void Browse()
    {
        var picked = PickExecutable(GetItemText(PathId));
        if (!string.IsNullOrWhiteSpace(picked))
        {
            SetItemText(PathId, picked);
        }
    }

    protected override bool OnOk()
    {
        SaveCurrent();
        var blank = _editors.FindIndex(editor => string.IsNullOrWhiteSpace(editor.Path));
        if (blank >= 0)
        {
            SelectIndex(blank);
            _warnings.ShowWarning(Hwnd, Tr.ExternalEditorsTitle, Tr.DialogExternalEditorExeMissing(string.Empty));
            return false;
        }

        return true;
    }

    // ---- bố cục -----------------------------------------------------------------------------------------------------------

    private int Margin => Dx(10);

    private int FieldHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int ButtonHeight => Math.Max(Dy(14), LineHeight + Dy(6));

    private int LeftWidth(int clientWidth) => Math.Clamp(clientWidth / 3, Dx(100), Dx(170));

    protected override (int Width, int Height) PreferredClientSize() => (Dx(470), Dy(190));

    protected override (int Width, int Height) MinimumClientSize()
        => (Math.Max(Dx(380), ButtonRowWidth(CancelId, ApplyId) + (2 * Margin)), Dy(170));

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

        // Cột trái: tiêu đề, danh sách, hàng nút Add/Remove rồi Set default.
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
        Place(SetDefaultId, m, row2, leftWidth, buttonHeight);

        // Cột phải: tiêu đề + trường chi tiết xếp dọc.
        y = m;
        var details = MeasureItemHeight(DetailsHeaderId, rightWidth);
        Place(DetailsHeaderId, rightX, y, rightWidth, details);
        y += details + Dy(8);
        y = PlaceField(NameLabelId, NameId, rightX, y, rightWidth, FieldHeight);

        var pathLabel = MeasureItemHeight(PathLabelId, rightWidth);
        Place(PathLabelId, rightX, y, rightWidth, pathLabel);
        y += pathLabel + Dy(2);
        var browseWidth = Math.Max(Dx(48), MeasureTextWidth(GetItemText(BrowseId)) + Dx(14));
        Place(PathId, rightX, y, Math.Max(Dx(40), rightWidth - browseWidth - gap), FieldHeight);
        Place(BrowseId, rightX + rightWidth - browseWidth, y, browseWidth, FieldHeight);
        y += FieldHeight + Dy(8);

        y = PlaceField(ArgumentsLabelId, ArgumentsId, rightX, y, rightWidth, FieldHeight);
        var hint = MeasureItemHeight(HintId, rightWidth);
        Place(HintId, rightX, y - Dy(4), rightWidth, hint);
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
