using System.Diagnostics;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsUi.Pages;

/// <summary>
/// Trang "General" (WPF: <c>GeneralPage</c> trong SettingsWindow.xaml): ngôn ngữ giao diện (+ mở thư mục / nạp lại / xuất bản dịch), thứ tự
/// ảnh, zoom ban đầu, chế độ cửa sổ, kiểm tra cập nhật thủ công, tích hợp menu Explorer. Thuộc tính: UiLanguage (qua
/// <c>SelectedLanguage</c>), ImageSortMode, InitialViewMode, InstanceMode.
/// </summary>
internal sealed class GeneralSettingsPage : SettingsPageBase
{
    /// <summary>Thứ tự mục của combobox thứ tự ảnh = thứ tự khai báo (khoá <c>SortModeTag</c> của <see cref="SettingsFormState"/>).</summary>
    internal static readonly string[] SortTags = ["Name", "NameAscending", "NameDescending", "SizeDescending", "SizeAscending", "Default"];

    private int _languageGroup;
    private int _language;
    private int _openFolder;
    private int _reload;
    private int _export;
    private int _sortMode;
    private int _viewMode;
    private int _instanceMode;
    private int _checkUpdate;
    private int _openUpdatePage;
    private int _updateStatus;
    private int _shellStatus;
    private int _shellAdd;
    private int _shellRemove;
    private IReadOnlyList<LanguageOption> _languages = [];
    private string _updateStatusText = string.Empty;
    private ShellIntegrationStatus? _shell;

    public override string Key => "General";

    /// <summary>Id điều khiển (test).</summary>
    internal (int Language, int SortMode, int ViewMode, int InstanceMode, int CheckUpdate, int OpenUpdatePage, int UpdateStatus, int ShellStatus, int ShellAdd,
        int ShellRemove, int LanguageGroup, int OpenFolder, int Reload, int Export) Ids
        => (_language, _sortMode, _viewMode, _instanceMode, _checkUpdate, _openUpdatePage, _updateStatus, _shellStatus, _shellAdd, _shellRemove,
            _languageGroup, _openFolder, _reload, _export);

    /// <summary>Kiểm tra cập nhật đang chạy hoặc vừa xong (test: chờ hoàn tất).</summary>
    internal Task UpdateCheckTask { get; private set; } = Task.CompletedTask;

    /// <summary>Các mục của combobox ngôn ngữ (test).</summary>
    internal IReadOnlyList<LanguageOption> Languages => _languages;

    public override void Declare(SettingsPageBuilder page)
    {
        _languageGroup = page.Group(() => Tr.SettingsGroupLanguage, g =>
        {
            _language = g.Combo(() => Tr.SettingsLanguageLabel).Control;
            var buttons = g.Buttons(true, () => Tr.SettingsLanguageOpenFolder, () => Tr.SettingsLanguageReload, () => Tr.SettingsLanguageExport);
            _openFolder = buttons[0];
            _reload = buttons[1];
            _export = buttons[2];
            g.Note(() => Tr.SettingsLanguageHint);
        });

        page.Group(() => Tr.SettingsNavGeneral, g =>
        {
            _sortMode = g.Combo(() => Tr.SettingsSortModeLabel,
            [
                () => Tr.EnumImageSortModeName,
                () => Tr.EnumImageSortModeNameAscending,
                () => Tr.EnumImageSortModeNameDescending,
                () => Tr.EnumImageSortModeSizeDescending,
                () => Tr.EnumImageSortModeSizeAscending,
                () => Tr.EnumImageSortModeDefault,
            ]).Control;
            g.Note(() => Tr.SettingsSortModeHint);
            _viewMode = g.Combo(() => Tr.SettingsViewModeLabel,
            [
                () => Tr.EnumInitialViewModeFit,
                () => Tr.EnumInitialViewModeFitWidth,
                () => Tr.EnumInitialViewModeFitHeight,
                () => Tr.EnumInitialViewModeClickZoomLevel,
                () => "100%",
                () => "200%",
            ]).Control;
            _instanceMode = g.Combo(() => Tr.SettingsInstanceModeLabel,
            [
                () => Tr.EnumInstanceModeSingleWindow,
                () => Tr.EnumInstanceModePerFolder,
            ]).Control;
            g.Note(() => Tr.SettingsInstanceModeHint);
        });

        page.Group(() => Tr.UpdateSectionTitle, g =>
        {
            var buttons = g.Buttons(false, () => Tr.UpdateCheckButton, () => Tr.UpdateOpenPageButton);
            _checkUpdate = buttons[0];
            _openUpdatePage = buttons[1];
            _updateStatus = g.Text();
            g.Note(() => Tr.UpdateHint, aligned: false);
        });

        // Explorer integration: applies immediately (HKCU), not on Save.
        page.Group(() => Tr.SettingsShellGroup, g =>
        {
            _shellStatus = g.Text();
            var buttons = g.Buttons(false, () => Tr.SettingsShellAdd, () => Tr.SettingsShellRemove);
            _shellAdd = buttons[0];
            _shellRemove = buttons[1];
            g.Note(() => Tr.SettingsShellHint, aligned: false);
        });
    }

    public override void Attach(SettingsPageContext context)
    {
        base.Attach(context);
        Context.SetVisible(_openUpdatePage, false);
        LoadLanguages(null);
        RefreshShellStatus();
    }

    // ---- Form <-> controls ----

    public override void Load(SettingsFormState state)
    {
        Context.SetComboIndex(_viewMode, state.ViewModeIndex);
        Context.SetComboIndex(_instanceMode, state.InstanceModeIndex);
        Context.SetComboIndex(_sortMode, Math.Max(0, Array.IndexOf(SortTags, state.SortModeTag)));
    }

    public override void Collect(SettingsFormState state)
    {
        state.ViewModeIndex = Context.ComboIndex(_viewMode);
        state.InstanceModeIndex = Context.ComboIndex(_instanceMode);
        var sort = Context.ComboIndex(_sortMode);
        state.SortModeTag = sort >= 0 && sort < SortTags.Length ? SortTags[sort] : null;
        var language = Context.ComboIndex(_language);
        state.SelectedLanguage = language >= 0 && language < _languages.Count ? _languages[language] : null;
    }

    // ---- Language picker ----

    private void LoadLanguages(string? currentCode)
    {
        var loaded = Context.Presenter.LoadLanguages(currentCode);
        if (loaded is null)
        {
            // No LocalizationService (a dialog built without DI): there is nothing to switch, so hide the group.
            Context.SetVisible(_languageGroup, false);
            return;
        }

        _languages = loaded.Value.Options;
        Context.SetComboItems(_language, _languages.Select(option => option.DisplayName).ToArray(), loaded.Value.SelectedIndex);
    }

    private LanguageOption? SelectedLanguage()
    {
        var index = Context.ComboIndex(_language);
        return index >= 0 && index < _languages.Count ? _languages[index] : null;
    }

    public override bool OnCommand(int id, int notification)
    {
        if (notification != DialogConstants.BnClicked)
        {
            return false;
        }

        if (id == _openFolder)
        {
            if (Context.Presenter.HasLocalization)
            {
                Context.RunExplorer(Context.Presenter.PrepareLanguagesFolder());
            }
        }
        else if (id == _reload)
        {
            ReloadTranslations();
        }
        else if (id == _export)
        {
            ExportTranslation();
        }
        else if (id == _checkUpdate)
        {
            if (Context.IsEnabled(_checkUpdate))
            {
                UpdateCheckTask = RunUpdateCheckAsync();
            }
        }
        else if (id == _openUpdatePage)
        {
            var failure = Context.Presenter.OpenUpdatePage(Context.Options.OpenUrl ?? DefaultOpenUrl);
            if (failure is not null)
            {
                SetUpdateStatus(failure);
            }
        }
        else if (id == _shellAdd)
        {
            ShowShellStatus(Context.Presenter.ChangeShellRegistration(register: true));
        }
        else if (id == _shellRemove)
        {
            ShowShellStatus(Context.Presenter.ChangeShellRegistration(register: false));
        }
        else
        {
            return false;
        }

        return true;
    }

    private void ReloadTranslations()
    {
        if (Context.Presenter.ReloadTranslations() is not { } reloaded)
        {
            return;
        }

        LoadLanguages(SelectedLanguage()?.Code); // a translator may have added a language file
        Context.Warn(Tr.DialogReloadTranslationsTitle, reloaded.Message);
    }

    private void ExportTranslation()
    {
        if (!Context.Presenter.HasLocalization)
        {
            return;
        }

        var (request, error) = Context.Presenter.ExportTranslation(SelectedLanguage());
        if (error is not null)
        {
            Context.Warn(Tr.DialogExportTranslationFailedTitle, error);
            return;
        }

        if (request is not null)
        {
            Context.RunExplorer(request);
        }
    }

    // ---- Manual update check: the only network use in the app, only on click ----

    private static void DefaultOpenUrl(string url)
    {
        using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task RunUpdateCheckAsync()
    {
        Context.SetEnabled(_checkUpdate, false);
        Context.SetVisible(_openUpdatePage, false);
        SetUpdateStatus(Tr.UpdateCheckChecking);
        var display = await Context.Presenter.CheckForUpdateAsync(Context.Version).ConfigureAwait(false);
        Context.PostToUi(() =>
        {
            if (display is null)
            {
                return; // dialog closed while checking
            }

            SetUpdateStatus(display.StatusText);
            Context.SetVisible(_openUpdatePage, display.ShowOpenPage);
            Context.SetEnabled(_checkUpdate, true);
        });
    }

    private void SetUpdateStatus(string text)
    {
        _updateStatusText = text;
        Context.SetText(_updateStatus, text);
        Context.Relayout();
    }

    // ---- Explorer integration ----

    private void RefreshShellStatus() => ShowShellStatus(Context.Presenter.RefreshShellStatus());

    private void ShowShellStatus(ShellIntegrationStatus status)
    {
        _shell = status;
        Context.SetText(_shellStatus, status.Text);
        if (status.AddLabel is not null)
        {
            Context.SetText(_shellAdd, status.AddLabel);
        }

        Context.SetEnabled(_shellAdd, status.AddEnabled);
        Context.SetEnabled(_shellRemove, status.RemoveEnabled);
        Context.Relayout();
    }

    public override void OnLanguageChanged()
    {
        // The status texts come from the presenter in the language of the moment: ask again (the update line keeps its result).
        Context.SetText(_updateStatus, _updateStatusText);
        if (_shell is not null)
        {
            RefreshShellStatus();
        }
    }
}
