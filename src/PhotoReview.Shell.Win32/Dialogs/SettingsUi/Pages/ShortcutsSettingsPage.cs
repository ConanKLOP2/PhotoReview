using PhotoReview.App.Presenters;
using PhotoReview.App.Services;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// WP-35 GĐ3b: trang "Shortcuts" (WPF: <c>ShortcutsPage</c>, 27 ô bắt phím). Mỗi lệnh một ô bắt phím (EDIT chỉ đọc, khung bắt phím qua
/// <see cref="SettingsPresenter.CaptureShortcut"/> như <c>ShortcutText_PreviewKeyDown</c>: tên phím chuẩn hoá, KHÔNG kèm Ctrl/Shift/Alt;
/// Backspace/Delete cũng là phím bình thường - Delete là phím mặc định của "Move to Recycle Bin"), 14 nút "✕" xoá cho phím tuỳ chọn
/// (<see cref="ShortcutMappings.OptionalNames"/>), nút "Khôi phục phím mặc định", dòng cảnh báo trùng phím trực tiếp (cùng phép kiểm của
/// Save: <see cref="SettingsPresenter.DuplicateShortcutWarning"/>), dấu "⚠" trên nhãn của ô không hợp lệ/trùng, và nút mở Action profiles
/// (modal lồng, <see cref="SettingsPageContext.EditActionProfiles"/>). Thuộc tính: Shortcuts (27 phím) + Actions (qua JSON dùng chung).
/// </summary>
internal sealed class ShortcutsSettingsPage : SettingsPageBase
{
    /// <summary>Một hàng của trang: thuộc tính <see cref="ShortcutMappings"/>, nhãn lệnh, tooltip của nút xoá (null = phím bắt buộc).</summary>
    internal sealed record ShortcutRow(string Property, Func<string> Label, Func<string>? ClearTip);

    private static ShortcutRow Required(string property, Func<string> label) => new(property, label, null);

    private static ShortcutRow Optional(string property, Func<string> label, Func<string> clearTip) => new(property, label, clearTip);

    /// <summary>Nhóm "Navigation" (thứ tự hàng = thứ tự Tab và thứ tự đọc).</summary>
    internal static readonly IReadOnlyList<ShortcutRow> NavigationRows =
    [
        Required(nameof(ShortcutMappings.Next), () => Tr.SettingsShortcutNext),
        Required(nameof(ShortcutMappings.Previous), () => Tr.SettingsShortcutPrevious),
        Required(nameof(ShortcutMappings.FirstImage), () => Tr.SettingsShortcutFirstImage),
        Optional(nameof(ShortcutMappings.LastImage), () => Tr.SettingsShortcutLastImage, () => Tr.SettingsShortcutLastImageClearAutomationName),
        Required(nameof(ShortcutMappings.NextFolder), () => Tr.SettingsShortcutNextFolder),
        Required(nameof(ShortcutMappings.PreviousFolder), () => Tr.SettingsShortcutPreviousFolder),
        Optional(nameof(ShortcutMappings.OpenFolder), () => Tr.SettingsShortcutOpenFolder, () => Tr.SettingsShortcutOpenFolderClearAutomationName),
        Optional(nameof(ShortcutMappings.ToggleCaptureMember), () => Tr.SettingsShortcutToggleCaptureMember,
            () => Tr.SettingsShortcutToggleCaptureMemberClearAutomationName),
    ];

    /// <summary>Nhóm "View".</summary>
    internal static readonly IReadOnlyList<ShortcutRow> ViewRows =
    [
        Required(nameof(ShortcutMappings.ZoomIn), () => Tr.SettingsShortcutZoomIn),
        Required(nameof(ShortcutMappings.ZoomOut), () => Tr.SettingsShortcutZoomOut),
        Optional(nameof(ShortcutMappings.ZoomActualSize), () => Tr.SettingsShortcutZoomActualSize,
            () => Tr.SettingsShortcutZoomActualSizeClearAutomationName),
        Required(nameof(ShortcutMappings.ToggleFit), () => Tr.SettingsShortcutToggleFit),
        Required(nameof(ShortcutMappings.Fullscreen), () => Tr.SettingsShortcutFullscreen),
        Optional(nameof(ShortcutMappings.ToggleInfoOverlay), () => Tr.SettingsShortcutToggleInfoOverlay,
            () => Tr.SettingsShortcutToggleInfoOverlayClearAutomationName),
        Optional(nameof(ShortcutMappings.ClickZoom), () => Tr.SettingsShortcutClickZoom, () => Tr.SettingsShortcutClickZoomClearAutomationName),
        Optional(nameof(ShortcutMappings.FitWidth), () => Tr.SettingsShortcutFitWidth, () => Tr.SettingsShortcutFitWidthClearAutomationName),
        Optional(nameof(ShortcutMappings.FitHeight), () => Tr.SettingsShortcutFitHeight, () => Tr.SettingsShortcutFitHeightClearAutomationName),
        Optional(nameof(ShortcutMappings.FitWidth2), () => Tr.SettingsShortcutFitWidth2, () => Tr.SettingsShortcutFitWidth2ClearAutomationName),
        Optional(nameof(ShortcutMappings.ToggleKeepZoom), () => Tr.SettingsShortcutToggleKeepZoom,
            () => Tr.SettingsShortcutToggleKeepZoomClearAutomationName),
        Optional(nameof(ShortcutMappings.CustomZoom), () => Tr.SettingsShortcutCustomZoom, () => Tr.SettingsShortcutCustomZoomClearAutomationName),
        Optional(nameof(ShortcutMappings.Refresh), () => Tr.SettingsShortcutRefresh, () => Tr.SettingsShortcutRefreshClearAutomationName),
    ];

    /// <summary>Nhóm "Actions".</summary>
    internal static readonly IReadOnlyList<ShortcutRow> ActionRows =
    [
        Required(nameof(ShortcutMappings.Skip), () => Tr.SettingsShortcutSkip),
        Required(nameof(ShortcutMappings.Undo), () => Tr.SettingsShortcutUndo),
        Required(nameof(ShortcutMappings.Compare), () => Tr.SettingsShortcutCompare),
        Optional(nameof(ShortcutMappings.MoveToFolder), () => Tr.SettingsShortcutMoveToFolder, () => Tr.SettingsShortcutMoveToFolderClearAutomationName),
        Optional(nameof(ShortcutMappings.CopyToFolder), () => Tr.SettingsShortcutCopyToFolder, () => Tr.SettingsShortcutCopyToFolderClearAutomationName),
        Required(nameof(ShortcutMappings.SendToRecycleBin), () => Tr.SettingsShortcutRecycle),
        Optional(nameof(ShortcutMappings.OpenInDefaultEditor), () => Tr.SettingsShortcutOpenInDefaultEditor,
            () => Tr.SettingsShortcutOpenInDefaultEditorClearAutomationName),
        Optional(nameof(ShortcutMappings.RenameFile), () => Tr.SettingsShortcutRenameFile,
            () => Tr.SettingsShortcutRenameFileClearAutomationName),
    ];

    /// <summary>Mọi hàng của trang theo thứ tự hiển thị.</summary>
    internal static IEnumerable<ShortcutRow> AllRows => NavigationRows.Concat(ViewRows).Concat(ActionRows);

    private const string Marker = "⚠ ";
    private const int EnChange = 0x0300;

    private readonly Dictionary<string, SettingsKeyCell> _cells = new(StringComparer.Ordinal);
    private readonly HashSet<string> _marked = new(StringComparer.Ordinal);
    private int _warning;
    private int _resetKeys;
    private int _editActions;
    private string _warningText = string.Empty;

    public override string Key => "Shortcuts";

    /// <summary>Id điều khiển (test): nút khôi phục phím, nút Action profiles, dòng cảnh báo.</summary>
    internal (int ResetKeys, int EditActions, int Warning) Ids => (_resetKeys, _editActions, _warning);

    /// <summary>Ô của thuộc tính <paramref name="property"/> (test).</summary>
    internal SettingsKeyCell Cell(string property) => _cells[property];

    /// <summary>Thuộc tính hiện đang được đánh dấu ⚠ (không hợp lệ hoặc trùng; test).</summary>
    internal IReadOnlyCollection<string> MarkedProperties => _marked;

    public override void Declare(SettingsPageBuilder page)
    {
        page.Note(() => Tr.SettingsGroupShortcutsHint, aligned: false);
        _warning = page.Warning();
        _resetKeys = page.Buttons(false, () => Tr.SettingsShortcutsResetKeys)[0];
        page.Group(() => Tr.SettingsGroupNavigation, g => AddGrid(g, NavigationRows));
        page.Group(() => Tr.SettingsGroupView, g => AddGrid(g, ViewRows));
        page.Group(() => Tr.SettingsGroupActions, g =>
        {
            AddGrid(g, ActionRows);
            g.Note(() => Tr.SettingsEditActionProfilesHint, aligned: false);
            _editActions = g.Buttons(false, () => Tr.SettingsEditActionProfiles)[0];
        });
    }

    private void AddGrid(SettingsPageBuilder group, IReadOnlyList<ShortcutRow> rows)
    {
        var specs = rows.Select(row => new SettingsKeyCellSpec(() => LabelOf(row), row.ClearTip is not null)).ToArray();
        var cells = group.KeyGrid(specs);
        for (var i = 0; i < rows.Count; i++)
        {
            _cells[rows[i].Property] = cells[i];
        }
    }

    private string LabelOf(ShortcutRow row) => (_marked.Contains(row.Property) ? Marker : string.Empty) + row.Label();

    public override void Attach(SettingsPageContext context)
    {
        base.Attach(context);
        foreach (var row in AllRows)
        {
            if (row.ClearTip is { } tip && _cells[row.Property].Clear != 0)
            {
                context.SetTooltip(_cells[row.Property].Clear, tip());
            }
        }
    }

    public override void Load(SettingsFormState state)
    {
        foreach (var (property, cell) in _cells)
        {
            Context.SetText(cell.Box, state.ShortcutTexts.GetValueOrDefault(property, string.Empty));
        }

        Refresh();
    }

    public override void Collect(SettingsFormState state)
    {
        foreach (var (property, cell) in _cells)
        {
            state.ShortcutTexts[property] = Context.Text(cell.Box);
        }
    }

    public override bool OnCommand(int id, int notification)
    {
        if (notification == EnChange && IsBox(id))
        {
            Refresh();
            return true;
        }

        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        if (id == _resetKeys)
        {
            ResetKeys();
            return true;
        }

        if (id == _editActions)
        {
            EditActions();
            return true;
        }

        foreach (var cell in _cells.Values)
        {
            if (cell.Clear == id)
            {
                Context.SetText(cell.Box, string.Empty); // EN_CHANGE of the box refreshes the warning
                return true;
            }
        }

        return false;
    }

    private bool IsBox(int id) => _cells.Values.Any(cell => cell.Box == id);

    /// <summary>"Khôi phục phím mặc định": chỉ 27 ô phím (action và các trang khác giữ nguyên); chưa lưu tới khi Save/Apply.</summary>
    private void ResetKeys()
    {
        var defaults = SettingsFieldMap.Shortcuts;
        var mapping = ShortcutMappings.Default();
        foreach (var accessor in defaults)
        {
            if (_cells.TryGetValue(accessor.Property, out var cell))
            {
                Context.SetText(cell.Box, accessor.Get(mapping));
            }
        }

        Refresh();
    }

    /// <summary>Nút "Edit action profiles": trình sửa modal lồng; Apply ghi JSON dùng chung, Cancel không đổi gì.</summary>
    private void EditActions()
    {
        IReadOnlyList<ReviewAction>? edited;
        try
        {
            edited = Context.EditActionProfiles(SettingsPresenter.ActionsForEditor(Context.CollectActionsJson()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Context.ShowWarning(Tr.DialogOpenEditorFailedTitle, Tr.DialogOpenEditorFailedMessage);
            return;
        }

        if (edited is null)
        {
            return;
        }

        Context.CommitActionsJson(SettingsPresenter.ActionsToText(edited), this);
        Refresh();
    }

    public override void OnLanguageChanged()
    {
        _warningText = string.Empty;
        Refresh();
    }

    /// <summary>Cảnh báo trùng phím trực tiếp + dấu ⚠ trên nhãn; chạy khi mọi ô phím đổi (mỗi lần bấm phím).</summary>
    private void Refresh()
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (property, cell) in _cells)
        {
            texts[property] = Context.Text(cell.Box);
        }

        var message = Context.Presenter.DuplicateShortcutWarning(texts, Context.ActionsJson) ?? string.Empty;
        if (!string.Equals(message, _warningText, StringComparison.Ordinal))
        {
            _warningText = message;
            Context.SetText(_warning, message);
            Context.Relayout();
        }

        UpdateMarkers(texts);
    }

    /// <summary>
    /// Dấu ⚠ "theo trường": ô phím bắt buộc rỗng/không hợp lệ, ô tuỳ chọn không rỗng mà không hợp lệ, hoặc hai ô cùng một phím
    /// (so theo tên chuẩn hoá, không phân biệt hoa thường, như phép kiểm của Save).
    /// </summary>
    internal static HashSet<string> FindProblems(IReadOnlyDictionary<string, string> texts)
    {
        var problems = new HashSet<string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, raw) in texts)
        {
            var canonical = ShortcutKeyCanonical.Canonicalize(raw);
            var optional = ShortcutMappings.IsOptional(property);
            if (canonical.Length == 0)
            {
                if (!optional)
                {
                    problems.Add(property);
                }

                continue;
            }

            if (!ShortcutKeyName.TryParse(canonical, out _))
            {
                problems.Add(property);
                continue;
            }

            if (!byKey.TryGetValue(canonical, out var owners))
            {
                byKey[canonical] = owners = [];
            }

            owners.Add(property);
        }

        foreach (var owners in byKey.Values.Where(owners => owners.Count > 1))
        {
            problems.UnionWith(owners);
        }

        return problems;
    }

    private void UpdateMarkers(IReadOnlyDictionary<string, string> texts)
    {
        var problems = FindProblems(texts);
        if (problems.SetEquals(_marked))
        {
            return;
        }

        var changed = problems.Union(_marked).ToArray();
        _marked.Clear();
        _marked.UnionWith(problems);
        var rows = AllRows.ToDictionary(row => row.Property, StringComparer.Ordinal);
        foreach (var property in changed)
        {
            if (rows.TryGetValue(property, out var row) && _cells.TryGetValue(property, out var cell))
            {
                Context.SetText(cell.Label, LabelOf(row));
            }
        }

        Context.Relayout();
    }
}
