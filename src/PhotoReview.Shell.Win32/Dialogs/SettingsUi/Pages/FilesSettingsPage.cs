using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs.Native;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// Trang "Files &amp; safety" (WPF: <c>FilesPage</c> trong SettingsWindow.xaml): so sánh bản trùng, Camera RAW, WebP/HEIC (+ trạng thái codec
/// của máy), xoá (xác nhận / cho phép xoá vĩnh viễn khi ổ không có thùng rác), trình soạn thảo ngoài (+ Browse), nhật ký thao tác (2 radio),
/// "dùng lại thư mục lần trước" của Move/Copy. Thuộc tính: CompareHashEnabled, CompareSizeEnabled, RawSupportEnabled, RawFullDecode,
/// RawPairMode, WebpHeicSupportEnabled, AllowPermanentDeleteWithoutRecycleBin, ConfirmBeforeDelete, ExternalEditorPath, JournalDurability,
/// MoveCopyReuseLastFolder. KHÔNG có ActionsJson: ô JSON đó thuộc trang Shortcuts (nút Action profiles). Mọi mặc định, kiểm tra và ánh xạ
/// nằm ở <c>SettingsFieldMap</c>; trang chỉ chép giá trị (xoá vĩnh viễn: đúng chữ cảnh báo + mặc định TẮT như bản WPF).
/// </summary>
internal sealed class FilesSettingsPage : SettingsPageBase
{
    private readonly Func<WicCodecSupport> _codecs;

    private int _compareHash;
    private int _compareSize;
    private int _rawSupport;
    private int _rawFullDecode;
    private int _rawPairMode;
    private int _webpHeic;
    private int _codecStatus;
    private int _allowPermanentDelete;
    private int _confirmBeforeDelete;
    private int _editorPath;
    private int _browse;
    private int _manage;
    private int _sendRaw;
    private int _editorSummary;
    private List<ExternalEditor> _editors = [];
    private string _defaultEditorId = string.Empty;
    private int _journal;
    private IReadOnlyList<int> _journalRadios = [];
    private int _reuseLastFolder;

    /// <param name="codecs">Mã nguồn trạng thái codec WIC (test thay bản giả; mặc định: dò một lần cho cả tiến trình).</param>
    public FilesSettingsPage(Func<WicCodecSupport>? codecs = null) => _codecs = codecs ?? (() => WicCodecAvailability.Current);

    public override string Key => "Files";

    /// <summary>Id điều khiển (test).</summary>
    internal (int CompareHash, int CompareSize, int RawSupport, int RawFullDecode, int RawPairMode, int WebpHeic, int CodecStatus,
        int AllowPermanentDelete, int ConfirmBeforeDelete, int EditorPath, int Browse, int Journal, int ReuseLastFolder) Ids
        => (_compareHash, _compareSize, _rawSupport, _rawFullDecode, _rawPairMode, _webpHeic, _codecStatus, _allowPermanentDelete,
            _confirmBeforeDelete, _editorPath, _browse, _journal, _reuseLastFolder);

    /// <summary>FEAT-EXTERNAL-EDITORS: id của nút "Manage editors...", ô "send RAW" và dòng tóm tắt (test).</summary>
    internal (int Manage, int SendRaw, int Summary) EditorIds => (_manage, _sendRaw, _editorSummary);

    /// <summary>Hai radio của nhật ký thao tác: chỉ số 0 = Fast (mặc định), 1 = Power-loss safe (test).</summary>
    internal IReadOnlyList<int> JournalRadios => _journalRadios;

    public override void Declare(SettingsPageBuilder page)
    {
        page.Group(() => Tr.SettingsGroupCompareOptions, g =>
        {
            _compareHash = g.Check(() => Tr.SettingsCompareHashLabel);
            _compareSize = g.Check(() => Tr.SettingsCompareSizeLabel);
        });

        page.Group(() => Tr.SettingsGroupRaw, g =>
        {
            _rawSupport = g.Check(() => Tr.SettingsRawSupportEnabledLabel);
            g.Note(() => Tr.SettingsRawSupportEnabledHint);
            // Item order is the contract with SettingsFormState.RawFullDecodeIndex / RawPairModeIndex (enum order of the WPF page).
            _rawFullDecode = g.Combo(() => Tr.SettingsRawFullDecodeLabel,
            [
                () => Tr.EnumRawFullDecodeNever,
                () => Tr.EnumRawFullDecodeOnZoom,
            ]).Control;
            g.Note(() => Tr.SettingsRawFullDecodeHint);
            _rawPairMode = g.Combo(() => Tr.SettingsRawPairModeLabel,
            [
                () => Tr.EnumRawPairModeSeparate,
                () => Tr.EnumRawPairModePreferJpeg,
                () => Tr.EnumRawPairModePreferRaw,
            ]).Control;
            g.Note(() => Tr.SettingsRawPairModeHint);
            g.Note(() => Tr.SettingsRawPairModeShortcutHint); // a tooltip in the WPF page; a note here (no per-control tooltips)
        });

        page.Group(() => Tr.SettingsGroupWebpHeic, g =>
        {
            _webpHeic = g.Check(() => Tr.SettingsWebpHeicSupportEnabledLabel);
            g.Note(() => Tr.SettingsWebpHeicSupportEnabledHint, aligned: false);
            _codecStatus = g.Note(null, aligned: false);
        });

        // The permanent-delete switch keeps the WPF wording and default (off): see SettingsFieldMap / SettingsPresenter.ApplyDefaults.
        _allowPermanentDelete = page.Check(() => Tr.SettingsAllowPermanentDeleteLabel);
        page.Note(() => Tr.SettingsAllowPermanentDeleteHint, aligned: false);
        _confirmBeforeDelete = page.Check(() => Tr.SettingsConfirmBeforeDeleteLabel);
        page.Note(() => Tr.SettingsConfirmBeforeDeleteHint, aligned: false);

        page.Group(() => Tr.SettingsGroupExternalEditor, g =>
        {
            _editorPath = g.Edit(() => Tr.SettingsExternalEditorPathLabel, widthDlu: 260).Control;
            _browse = g.Buttons(true, () => Tr.SettingsExternalEditorPathBrowse)[0];
            g.Note(() => Tr.SettingsExternalEditorPathHint, aligned: false);
            // FEAT-EXTERNAL-EDITORS: the list of editors (right-click submenu) is managed in a nested dialog; the path above is the default one.
            _manage = g.Buttons(true, () => Tr.SettingsExternalEditorsManage)[0];
            _editorSummary = g.Note(null, aligned: false);
            _sendRaw = g.Check(() => Tr.SettingsExternalEditorsSendRaw);
            g.Note(() => Tr.SettingsExternalEditorsSendRawHint, aligned: false);
        });

        page.Group(() => Tr.SettingsGroupJournalDurability, g =>
        {
            _journal = g.Text(() => Tr.SettingsJournalDurabilityLabel);
            // Index 0 = Fast (the default), 1 = Power-loss safe (SettingsFormState.JournalSafe).
            _journalRadios = g.Radio(
                [() => Tr.SettingsJournalDurabilityFast, () => Tr.SettingsJournalDurabilitySafe],
                [() => Tr.SettingsJournalDurabilityFastHint, () => Tr.SettingsJournalDurabilitySafeHint]);
        });

        page.Group(() => Tr.SettingsGroupActions, g =>
        {
            _reuseLastFolder = g.Check(() => Tr.SettingsMoveCopyReuseLastFolderLabel);
            g.Note(() => Tr.SettingsMoveCopyReuseLastFolderHint, aligned: false);
        });
    }

    public override void Attach(SettingsPageContext context)
    {
        base.Attach(context);
        ShowCodecStatus();
    }

    // ---- Form <-> controls ----

    public override void Load(SettingsFormState state)
    {
        Context.SetChecked(_compareHash, state.CompareHashEnabled);
        Context.SetChecked(_compareSize, state.CompareSizeEnabled);
        Context.SetChecked(_rawSupport, state.RawSupportEnabled);
        Context.SetComboIndex(_rawFullDecode, state.RawFullDecodeIndex);
        Context.SetComboIndex(_rawPairMode, state.RawPairModeIndex);
        Context.SetChecked(_webpHeic, state.WebpHeicSupportEnabled);
        Context.SetChecked(_allowPermanentDelete, state.AllowPermanentDelete);
        Context.SetChecked(_confirmBeforeDelete, state.ConfirmBeforeDelete);
        Context.SetText(_editorPath, state.ExternalEditorPath);
        _editors = [.. state.ExternalEditors.Select(editor => editor.Clone())];
        _defaultEditorId = state.DefaultExternalEditorId;
        Context.SetChecked(_sendRaw, state.ExternalEditorSendsRawOriginal);
        ShowEditorSummary();
        Context.SetRadioIndex(_journalRadios, state.JournalSafe ? 1 : 0);
        Context.SetChecked(_reuseLastFolder, state.MoveCopyReuseLastFolder);
    }

    public override void Collect(SettingsFormState state)
    {
        state.CompareHashEnabled = Context.IsChecked(_compareHash);
        state.CompareSizeEnabled = Context.IsChecked(_compareSize);
        state.RawSupportEnabled = Context.IsChecked(_rawSupport);
        state.RawFullDecodeIndex = Context.ComboIndex(_rawFullDecode);
        state.RawPairModeIndex = Context.ComboIndex(_rawPairMode);
        state.WebpHeicSupportEnabled = Context.IsChecked(_webpHeic);
        state.AllowPermanentDelete = Context.IsChecked(_allowPermanentDelete);
        state.ConfirmBeforeDelete = Context.IsChecked(_confirmBeforeDelete);
        state.ExternalEditorPath = Context.Text(_editorPath);
        // The path box edits the DEFAULT editor: the list carries the same path so a typed change is never read as a second editor.
        state.ExternalEditors = [.. _editors.Select(editor => editor.Clone())];
        state.DefaultExternalEditorId = _defaultEditorId;
        state.ExternalEditorSendsRawOriginal = Context.IsChecked(_sendRaw);
        state.JournalSafe = Context.RadioIndex(_journalRadios) == 1;
        state.MoveCopyReuseLastFolder = Context.IsChecked(_reuseLastFolder);
    }

    // ---- Commands ----

    public override bool OnCommand(int id, int notification)
    {
        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        if (id == _manage)
        {
            ManageEditors();
            return true;
        }

        if (id != _browse)
        {
            return false;
        }

        BrowseForEditor();
        return true;
    }

    /// <summary>"Manage editors...": the nested list dialog; on Apply the list, the default and the default-path box follow it (Save persists).</summary>
    private void ManageEditors()
    {
        // The path box is the default editor's path: fold a typed value into the list first, so the dialog shows what is on screen.
        var working = new AppSettings { ExternalEditors = [.. _editors.Select(editor => editor.Clone())], DefaultExternalEditorId = _defaultEditorId };
        ExternalEditors.ApplyLegacyPath(working, Context.Text(_editorPath));
        var edit = Context.EditExternalEditors(working.ExternalEditors, working.DefaultExternalEditorId);
        if (edit is null)
        {
            return;
        }

        _editors = [.. edit.Editors.Select(editor => editor.Clone())];
        _defaultEditorId = edit.DefaultId;
        var current = _editors.FirstOrDefault(editor => string.Equals(editor.Id, _defaultEditorId, StringComparison.Ordinal)) ?? _editors.FirstOrDefault();
        Context.SetText(_editorPath, current?.Path ?? string.Empty);
        ShowEditorSummary();
    }

    private void ShowEditorSummary()
    {
        var current = _editors.FirstOrDefault(editor => string.Equals(editor.Id, _defaultEditorId, StringComparison.Ordinal)) ?? _editors.FirstOrDefault();
        Context.SetText(_editorSummary, _editors.Count == 0
            ? Tr.SettingsExternalEditorsSummaryNone
            : Tr.SettingsExternalEditorsSummary(_editors.Count, current?.DisplayName ?? string.Empty));
        Context.Relayout();
    }

    /// <summary>"Browse...": chọn một .exe/tất cả tệp; huỷ giữ nguyên đường dẫn; chọn xong thì ghi vào ô (Save mới lưu).</summary>
    private void BrowseForEditor()
    {
        if (Context.Files is not { } files)
        {
            return;
        }

        string? picked;
        try
        {
            picked = files.PickOpenFile(Context.Owner, Tr.SettingsExternalEditorPathLabel, FileFilterText.Parse(Tr.DialogFileFilterExecutable),
                InitialFolder(Context.Text(_editorPath)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Context.ShowWarning(Tr.DialogSettingsOpenFailedTitle, ex.Message);
            return;
        }

        if (!string.IsNullOrEmpty(picked))
        {
            Context.SetText(_editorPath, picked);
        }
    }

    /// <summary>As the WPF page: the folder of the current path when that file exists, otherwise the system default.</summary>
    internal static string? InitialFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? Path.GetDirectoryName(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    public override bool FocusExternalEditor()
    {
        Context.Focus(_editorPath);
        return true;
    }

    public override void OnLanguageChanged()
    {
        ShowCodecStatus(); // the status line is composed from translated parts
        ShowEditorSummary();
    }

    private void ShowCodecStatus()
    {
        Context.SetText(_codecStatus, SettingsPresenter.FormatWebpHeicCodecStatus(_codecs()));
        Context.Relayout();
    }
}
