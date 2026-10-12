using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Updates;


namespace PhotoReview.App;

/// <summary>
/// The Settings dialog, WPF view only (WP-35 G0): it copies its controls to a <see cref="SettingsFormState"/>, hands that to
/// <see cref="SettingsPresenter"/> (load, validate, save, defaults, import/export, update check, Explorer integration,
/// language) and shows what comes back. No settings logic lives here.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsPresenter _presenter;

    /// <summary>The presenter (test seam).</summary>
    internal SettingsPresenter Presenter => _presenter;

    /// <summary>The working copy of the settings: after <c>DialogResult == true</c> this is what was saved.</summary>
    public AppSettings Settings => _presenter.Settings;

    /// <summary>Test seam: receives the invalid-destination warning of Save instead of a MessageBox.</summary>
    internal Action<string>? InvalidSettingsWarning { get; set; }
    internal Action<string>? ImportRepairsNotice { get; set; } // test seam for the "some imported values were reset" info message

    private Dictionary<string, ScrollViewer>? _pages;

    public SettingsWindow(SettingsStore store, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null, IUpdateChecker? updateChecker = null)
        : this(store.Current, store, decoderFactory, localization, updateChecker)
    {
    }

    public SettingsWindow(AppSettings current, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null, IUpdateChecker? updateChecker = null)
        : this(current, store: null, decoderFactory, localization, updateChecker)
    {
    }

    private SettingsWindow(AppSettings current, SettingsStore? store, IImageDecoderFactory? decoderFactory, LocalizationService? localization, IUpdateChecker? updateChecker)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        _presenter = new SettingsPresenter(current, store, localization, updateChecker);
        VersionText.Text = BuildInfo.Describe(typeof(SettingsWindow).Assembly);

        InitializePages();
        RefreshShellStatus();

        // Single source of truth for the shortcut boxes: input capture, duplicate warning and the "must parse" checks in
        // the presenter all derive from the ShortcutMappings property names, so a box missing here cannot silently miss one.
        ShortcutBoxes = BuildShortcutBoxes();
        foreach (var (_, textBox) in ShortcutBoxes)
        {
            textBox.PreviewKeyDown += ShortcutText_PreviewKeyDown;
            textBox.TextChanged += Shortcut_TextChanged;
        }

        LoadFields();
        ApplyDecoderAvailability(decoderFactory);
        LoadLanguages();
        Localizer.CurrentChanged += OnLanguageChanged;
    }

    /// <summary>Every shortcut text box with the <see cref="ShortcutMappings"/> property it edits (test seam).</summary>
    internal IReadOnlyList<(string Property, System.Windows.Controls.TextBox Box)> ShortcutBoxes { get; }

    private (string Property, System.Windows.Controls.TextBox Box)[] BuildShortcutBoxes() =>
    [
        (nameof(ShortcutMappings.Next), NextText), (nameof(ShortcutMappings.Previous), PreviousText),
        (nameof(ShortcutMappings.FirstImage), FirstImageText), (nameof(ShortcutMappings.LastImage), LastImageText),
        (nameof(ShortcutMappings.NextFolder), NextFolderText), (nameof(ShortcutMappings.PreviousFolder), PreviousFolderText),
        (nameof(ShortcutMappings.ZoomIn), ZoomInText), (nameof(ShortcutMappings.ZoomOut), ZoomOutText),
        (nameof(ShortcutMappings.ZoomActualSize), ZoomActualSizeText), (nameof(ShortcutMappings.ToggleFit), ToggleFitText),
        (nameof(ShortcutMappings.Fullscreen), FullscreenText), (nameof(ShortcutMappings.ToggleInfoOverlay), ToggleInfoOverlayText),
        (nameof(ShortcutMappings.Skip), SkipText), (nameof(ShortcutMappings.Undo), UndoText), (nameof(ShortcutMappings.Compare), CompareText),
        (nameof(ShortcutMappings.MoveToFolder), MoveToFolderText), (nameof(ShortcutMappings.CopyToFolder), CopyToFolderText),
        (nameof(ShortcutMappings.SendToRecycleBin), RecycleText), (nameof(ShortcutMappings.ClickZoom), ClickZoomText),
        (nameof(ShortcutMappings.FitWidth), FitWidthText), (nameof(ShortcutMappings.FitHeight), FitHeightText),
        (nameof(ShortcutMappings.ToggleKeepZoom), ToggleKeepZoomText), (nameof(ShortcutMappings.OpenFolder), OpenFolderText),
        (nameof(ShortcutMappings.CustomZoom), CustomZoomText), (nameof(ShortcutMappings.ToggleCaptureMember), ToggleCaptureMemberText),
        (nameof(ShortcutMappings.FitWidth2), FitWidth2Text), (nameof(ShortcutMappings.Refresh), RefreshText),
    ];

    // ---- Left navigation: page list, remembers the last page for this process only (DR02) ----

    private void InitializePages()
    {
        _pages = new Dictionary<string, ScrollViewer>(StringComparer.Ordinal)
        {
            ["General"] = GeneralScrollViewer,
            ["Display"] = DisplayScrollViewer,
            ["Mouse"] = MouseScrollViewer,
            ["Performance"] = PerformanceScrollViewer,
            ["Shortcuts"] = ShortcutsScrollViewer,
            ["Files"] = FilesScrollViewer,
            ["Diagnostics"] = DiagnosticsScrollViewer,
        };
        var target = SettingsPresenter.InitialPageKey;
        foreach (ListBoxItem item in NavList.Items)
        {
            if (Equals(item.Tag, target)) { NavList.SelectedItem = item; break; }
        }
        NavList.SelectedItem ??= NavList.Items[0];
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages is null || NavList.SelectedItem is not ListBoxItem { Tag: string key } || !_pages.TryGetValue(key, out var page)) return;
        SettingsPresenter.RememberPage(key);
        foreach (var (pageKey, scroll) in _pages)
            scroll.Visibility = pageKey == key ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageTitle();
        page.ScrollToHome();
    }

    private void UpdatePageTitle()
    {
        if (_pages is null || NavList.SelectedItem is not ListBoxItem { Tag: string key } || !_pages.ContainsKey(key)) return;
        PageTitleText.Text = SettingsPresenter.PageTitle(key);
    }

    /// <summary>"Reload translations" / a language switch: only {loc:Tr} XAML texts follow on their own, so re-render the texts set in code.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshLocalizedTexts();
        else _ = Dispatcher.BeginInvoke(RefreshLocalizedTexts);
    }

    private void RefreshLocalizedTexts()
    {
        UpdatePageTitle();
        ToolbarOpacityHint.Text = SettingsPresenter.ToolbarOpacityHint();
        KeyboardZoomStepPercentHint.Text = SettingsPresenter.KeyboardZoomStepPercentHint();
        UpdateToolbarOpacityValueText();
        UpdateRamCacheRange();
        UpdateRamCacheValueText();
        UpdatePreloadWindowHint();
        UpdateZoomShortcutSummary();
        UpdateWebpHeicCodecStatus();
    }

    /// <summary>Q-FMT-WEBP-HEIC: which Windows codecs this PC has (probed once per process; cheap after the first call).</summary>
    private void UpdateWebpHeicCodecStatus() =>
        WebpHeicCodecStatusText.Text = SettingsPresenter.FormatWebpHeicCodecStatus(PhotoReview.Imaging.Decoding.Wic.WicCodecAvailability.Current);

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab cycles pages regardless of which control has focus.</summary>
    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        NavList.SelectedIndex = SettingsPresenter.NextPageIndex(NavList.SelectedIndex, NavList.Items.Count, (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    private void ApplyDecoderAvailability(IImageDecoderFactory? decoderFactory)
    {
        if (!SettingsPresenter.IsTurboJpegAvailable(decoderFactory))
        {
            TurboJpegOption.IsEnabled = false;
            TurboJpegOption.ToolTip = Tr.SettingsDecoderBackendTurboJpegUnavailable;
        }
    }

    // ---- I18N L08: language picker, languages folder, reload, export ----

    /// <summary>Fills the picker from the shipped and user folders and selects <see cref="AppSettings.UiLanguage"/>.</summary>
    private void LoadLanguages()
    {
        var loaded = _presenter.LoadLanguages((LanguageCombo.SelectedItem as LanguageOption)?.Code);
        if (loaded is null)
        {
            // No LocalizationService (a window built without DI): there is nothing to switch, so hide the group.
            LanguageGroup.Visibility = Visibility.Collapsed;
            return;
        }
        LanguageCombo.ItemsSource = loaded.Value.Options;
        LanguageCombo.SelectedIndex = loaded.Value.SelectedIndex;
    }

    private void OpenLanguagesFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_presenter.HasLocalization) return;
        RunExplorer(_presenter.PrepareLanguagesFolder());
    }

    private void ReloadTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter.ReloadTranslations() is not { } reloaded) return;
        LoadLanguages(); // a translator may have added a language file
        System.Windows.MessageBox.Show(this, reloaded.Message, Tr.DialogReloadTranslationsTitle, MessageBoxButton.OK,
            reloaded.HasProblems ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ExportTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (!_presenter.HasLocalization) return;
        var (request, error) = _presenter.ExportTranslation(LanguageCombo.SelectedItem as LanguageOption);
        if (error is not null)
        {
            System.Windows.MessageBox.Show(this, error, Tr.DialogExportTranslationFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (request is not null) RunExplorer(request);
    }

    private void OpenLogLocation_Click(object sender, RoutedEventArgs e) => RunExplorer(SettingsPresenter.PrepareLogLocation());

    private void RunExplorer(SettingsExplorerRequest request)
    {
        if (request.ErrorMessage is not null) { ShowOpenFailed(request.ErrorMessage); return; }
        if (request.Arguments is null) return;
        if (SettingsPresenter.TryStartExplorer(request.Arguments) is { } error) ShowOpenFailed(error);
    }

    private void ShowOpenFailed(string message) =>
        System.Windows.MessageBox.Show(this, message, Tr.DialogSettingsOpenFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- Manual update check: the only network use in the app, only on click (no auto-check/download/install) ----

    /// <summary>Test seam: opens the download page; default is the system browser via ShellExecute.</summary>
    internal Action<string> OpenUrl { get; set; } = static url => { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };

    /// <summary>The last started update check (test seam: lets a test await completion).</summary>
    internal Task UpdateCheckTask { get; private set; } = Task.CompletedTask;

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CheckUpdateButton.IsEnabled) return;
        UpdateCheckTask = RunUpdateCheckAsync();
    }

    private async Task RunUpdateCheckAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        OpenUpdatePageButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = Tr.UpdateCheckChecking;
        var display = await _presenter.CheckForUpdateAsync(BuildInfo.GetVersion(typeof(SettingsWindow).Assembly));
        if (display is null) return; // window closed while checking
        UpdateStatusText.Text = display.StatusText;
        OpenUpdatePageButton.Visibility = display.ShowOpenPage ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = true;
    }

    private void OpenUpdatePage_Click(object sender, RoutedEventArgs e)
    {
        if (_presenter.OpenUpdatePage(OpenUrl) is { } failure) UpdateStatusText.Text = failure;
    }

    protected override void OnClosed(EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        _presenter.CancelUpdateCheck();
        base.OnClosed(e);
    }

    /// <summary>Set before showing: opens on the Files page and focuses the External Editor path field (menu item with no editor configured).</summary>
    internal bool FocusExternalEditorOnLoad { get; set; }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (FocusExternalEditorOnLoad)
        {
            var rememberedPage = SettingsPresenter.RememberedPageKey; // a one-off jump must not change the page the next plain Settings open shows
            foreach (ListBoxItem item in NavList.Items)
            {
                if (Equals(item.Tag, "Files")) { NavList.SelectedItem = item; break; }
            }
            SettingsPresenter.RememberPage(rememberedPage);
            ExternalEditorPathText.BringIntoView();
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => ExternalEditorPathText.Focus());
            return;
        }
        if (NavList.SelectedItem is ListBoxItem { Tag: string key } && _pages is not null && _pages.TryGetValue(key, out var page))
            page.ScrollToHome();
        FocusManager.SetFocusedElement(this, null);
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            Keyboard.ClearFocus();
        });
    }

    // ---- Explorer integration: "Browse with PhotoReview" on folders (HKCU, no administrator rights; applies immediately) ----

    /// <summary>The registry seam (test seam: a fake replaces it).</summary>
    internal IShellIntegration ShellIntegration
    {
        get => _presenter.ShellIntegration;
        set => _presenter.ShellIntegration = value;
    }

    /// <summary>The executable the command should launch; null/dotnet host = unavailable (test seam).</summary>
    internal string? ExecutablePath
    {
        get => _presenter.ExecutablePath;
        set => _presenter.ExecutablePath = value;
    }

    /// <summary>Shows the current registration state and enables the buttons that make sense for it.</summary>
    internal void RefreshShellStatus() => ShowShellStatus(_presenter.RefreshShellStatus());

    private void ShowShellStatus(ShellIntegrationStatus status)
    {
        ShellStatusText.Text = status.Text;
        if (status.AddLabel is not null) ShellAddButton.Content = status.AddLabel;
        ShellAddButton.IsEnabled = status.AddEnabled;
        ShellRemoveButton.IsEnabled = status.RemoveEnabled;
    }

    private void ShellAdd_Click(object sender, RoutedEventArgs e) => ShowShellStatus(_presenter.ChangeShellRegistration(register: true));

    private void ShellRemove_Click(object sender, RoutedEventArgs e) => ShowShellStatus(_presenter.ChangeShellRegistration(register: false));

    private readonly Dictionary<ContextMenuItemId, CheckBox> _contextMenuChecks = [];

    /// <summary>The right-click menu tick boxes by item id (test seam).</summary>
    internal IReadOnlyDictionary<ContextMenuItemId, CheckBox> ContextMenuChecks => _contextMenuChecks;

    /// <summary>Right-click menu page: one tick box per item, built once from the presenter's item rows.</summary>
    private void EnsureContextMenuChecks()
    {
        if (_contextMenuChecks.Count != 0) return;
        foreach (var row in SettingsPresenter.ContextMenuRows())
        {
            var check = new CheckBox
            {
                Content = row.Label,
                Tag = row.Id,
                Margin = new Thickness(row.IsChild ? 24 : 0, 2, 0, 2),
            };
            AutomationProperties.SetName(check, row.AutomationName);
            if (row.Locked)
            {
                // Settings can never be hidden: the user could not get back to this window.
                check.IsEnabled = false;
                check.ToolTip = row.LockedTooltip;
                ToolTipService.SetShowOnDisabled(check, true);
            }
            _contextMenuChecks[row.Id] = check;
            ContextMenuItemsHost.Children.Add(check);
        }
    }

    // ---- Controls <-> SettingsFormState (the presenter owns every rule; these two methods only copy values) ----

    private IReadOnlyList<(CheckBox Box, ExifInfoFields Flag)> ExifFieldMap =>
    [
        (ExifFieldFileNameCheck, ExifInfoFields.FileName), (ExifFieldDateTakenCheck, ExifInfoFields.DateTaken),
        (ExifFieldModifiedDateCheck, ExifInfoFields.ModifiedDate), (ExifFieldDimensionsCheck, ExifInfoFields.Dimensions),
        (ExifFieldCameraCheck, ExifInfoFields.Camera), (ExifFieldLensCheck, ExifInfoFields.Lens),
        (ExifFieldIsoCheck, ExifInfoFields.Iso), (ExifFieldFocalLengthCheck, ExifInfoFields.FocalLength),
        (ExifFieldApertureCheck, ExifInfoFields.Aperture), (ExifFieldShutterSpeedCheck, ExifInfoFields.ShutterSpeed),
    ];

    private IReadOnlyList<(CheckBox Box, TitleBarFields Flag)> TitleBarFieldMap =>
    [
        (TitleBarFieldFolderNameCheck, TitleBarFields.FolderName), (TitleBarFieldFolderPathCheck, TitleBarFields.FolderPath),
        (TitleBarFieldIndexCountCheck, TitleBarFields.IndexCount), (TitleBarFieldFileNameCheck, TitleBarFields.FileName),
        (TitleBarFieldFileSizeCheck, TitleBarFields.FileSize), (TitleBarFieldDimensionsCheck, TitleBarFields.Dimensions),
        (TitleBarFieldModifiedDateCheck, TitleBarFields.ModifiedDate), (TitleBarFieldDateTakenCheck, TitleBarFields.DateTaken),
        (TitleBarFieldCameraCheck, TitleBarFields.Camera), (TitleBarFieldLensCheck, TitleBarFields.Lens),
        (TitleBarFieldIsoCheck, TitleBarFields.Iso), (TitleBarFieldFocalLengthCheck, TitleBarFields.FocalLength),
        (TitleBarFieldApertureCheck, TitleBarFields.Aperture), (TitleBarFieldShutterSpeedCheck, TitleBarFields.ShutterSpeed),
    ];

    private void LoadFields()
    {
        var f = _presenter.LoadFields();
        foreach (var (property, box) in ShortcutBoxes) box.Text = f.ShortcutTexts.GetValueOrDefault(property, string.Empty);
        ActionsText.Text = f.ActionsJson;
        ViewModeCombo.SelectedIndex = f.ViewModeIndex;
        FitWidthAnchorCombo.SelectedIndex = f.FitWidthAnchorIndex;
        FitWidthAnchor2Combo.SelectedIndex = f.FitWidthAnchor2Index;
        MiddleClickActionCombo.SelectedIndex = f.MiddleClickActionIndex;
        KeepZoomAcrossImagesCheck.IsChecked = f.KeepZoomAcrossImages;
        LoadingModeCombo.SelectedIndex = f.LoadingModeIndex;
        SortModeCombo.SelectedIndex = Math.Max(0, SortModeCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => Equals(item.Tag, f.SortModeTag)));
        ScalingQualityCombo.SelectedIndex = f.ScalingQualityIndex;
        DecoderBackendCombo.SelectedIndex = f.DecoderBackendIndex;
        InstanceModeCombo.SelectedIndex = f.InstanceModeIndex;
        CompareHashCheck.IsChecked = f.CompareHashEnabled;
        CompareSizeCheck.IsChecked = f.CompareSizeEnabled;
        RawSupportEnabledCheck.IsChecked = f.RawSupportEnabled;
        WebpHeicSupportEnabledCheck.IsChecked = f.WebpHeicSupportEnabled;
        UpdateWebpHeicCodecStatus();
        RawFullDecodeCombo.SelectedIndex = f.RawFullDecodeIndex;
        RawPairModeCombo.SelectedIndex = f.RawPairModeIndex;
        LoggingCheck.IsChecked = f.LoggingEnabled;
        AllowPermanentDeleteCheck.IsChecked = f.AllowPermanentDelete;
        ConfirmBeforeDeleteCheck.IsChecked = f.ConfirmBeforeDelete;
        ExternalEditorPathText.Text = f.ExternalEditorPath;
        JournalSafeRadio.IsChecked = f.JournalSafe;
        JournalFastRadio.IsChecked = !f.JournalSafe;
        ShowInfoOverlayCheck.IsChecked = f.ShowInfoOverlay;
        ShowFileInfoCheck.IsChecked = f.ShowFileInfo;
        ShowFolderInfoCheck.IsChecked = f.ShowFolderInfo;
        InfoOverlayFontSizeBox.Text = f.InfoOverlayFontSizeText;
        ShowZoomIndicatorCheck.IsChecked = f.ShowZoomIndicator;
        ToolbarAutoHideCheck.IsChecked = f.ToolbarAutoHide;
        ToolbarAutoHideDelayBox.Text = f.ToolbarAutoHideDelayText;
        InfoOverlayAutoHideCheck.IsChecked = f.InfoOverlayAutoHide;
        InfoOverlayAutoHideDelayBox.Text = f.InfoOverlayAutoHideDelayText;
        ToolbarOpacitySlider.Minimum = AppSettings.MinToolbarOpacityPercent;
        ToolbarOpacitySlider.Maximum = AppSettings.MaxToolbarOpacityPercent;
        ToolbarOpacitySlider.Value = f.ToolbarOpacityValue;
        ToolbarOpacityHint.Text = SettingsPresenter.ToolbarOpacityHint();
        KeyboardZoomStepPercentHint.Text = SettingsPresenter.KeyboardZoomStepPercentHint();
        UpdateToolbarOpacityValueText();
        MouseWheelActionCombo.SelectedIndex = f.MouseWheelActionIndex;
        ClickToZoomCheck.IsChecked = f.ClickToZoomEnabled;
        ClickZoomPercentBox.Text = f.ClickZoomPercentText;
        PreloadForwardBox.Text = f.PreloadForwardText;
        PreloadBackwardBox.Text = f.PreloadBackwardText;
        KineticPanCheck.IsChecked = f.KineticPanEnabled;
        KineticGlideSmoothingCombo.SelectedIndex = f.KineticGlideSmoothingIndex;
        ArrowKeyNavigatesAtZoomEdgeCheck.IsChecked = f.ArrowKeyNavigatesAtZoomEdge;
        EnsureContextMenuChecks();
        foreach (var (id, check) in _contextMenuChecks) check.IsChecked = f.ContextMenuShown.Contains(id);
        ClickZoomKeyTogglesFitCheck.IsChecked = f.ClickZoomKeyTogglesFit;
        ArrowPanStepBox.Text = f.ArrowPanStepText;
        KeyboardZoomStepPercentBox.Text = f.KeyboardZoomStepPercentText;
        KeyboardZoomAnchorCombo.SelectedIndex = f.KeyboardZoomAnchorIndex;
        ImageTransitionCombo.SelectedIndex = f.ImageTransitionIndex;
        ImageTransitionMsBox.Text = f.ImageTransitionMsText;
        TouchpadSwipeCheck.IsChecked = f.TouchpadSwipeEnabled;
        TouchpadSwipeDistanceBox.Text = f.TouchpadSwipeDistanceText;
        UpdateImageTransitionMsEnabled();
        MoveCopyReuseLastFolderCheck.IsChecked = f.MoveCopyReuseLastFolder;
        ShowExifInfoCheck.IsChecked = f.ShowExifInfo;
        foreach (var (box, flag) in ExifFieldMap) box.IsChecked = f.ExifInfoFields.HasFlag(flag);
        foreach (var (box, flag) in TitleBarFieldMap) box.IsChecked = f.TitleBarFields.HasFlag(flag);
        UpdateZoomShortcutSummary();
        UpdateShowInfoSubOptionsEnabled();
        UpdateExifFieldsEnabled();
        UpdateToolbarAutoHideEnabled();
        UpdateInfoOverlayAutoHideEnabled();
        UpdateRamCacheRange();
        RamCacheSlider.Value = f.RamCachePercent;
        UpdateRamCacheValueText();
        UpdatePreloadWindowHint();
        UpdateDuplicateWarning();
    }

    private SettingsFormState ReadFields()
    {
        var f = new SettingsFormState();
        foreach (var (property, box) in ShortcutBoxes) f.ShortcutTexts[property] = box.Text;
        f.ActionsJson = ActionsText.Text;
        f.ViewModeIndex = ViewModeCombo.SelectedIndex;
        f.FitWidthAnchorIndex = FitWidthAnchorCombo.SelectedIndex;
        f.FitWidthAnchor2Index = FitWidthAnchor2Combo.SelectedIndex;
        f.MiddleClickActionIndex = MiddleClickActionCombo.SelectedIndex;
        f.KeepZoomAcrossImages = KeepZoomAcrossImagesCheck.IsChecked == true;
        f.SortModeTag = SortModeCombo.SelectedItem is ComboBoxItem { Tag: string sortTag } ? sortTag : null;
        f.ScalingQualityIndex = ScalingQualityCombo.SelectedIndex;
        f.DecoderBackendIndex = DecoderBackendCombo.SelectedIndex;
        f.LoadingModeIndex = LoadingModeCombo.SelectedIndex;
        f.InstanceModeIndex = InstanceModeCombo.SelectedIndex;
        f.CompareHashEnabled = CompareHashCheck.IsChecked == true;
        f.CompareSizeEnabled = CompareSizeCheck.IsChecked == true;
        f.RawSupportEnabled = RawSupportEnabledCheck.IsChecked == true;
        f.WebpHeicSupportEnabled = WebpHeicSupportEnabledCheck.IsChecked == true;
        f.RawFullDecodeIndex = RawFullDecodeCombo.SelectedIndex;
        f.RawPairModeIndex = RawPairModeCombo.SelectedIndex;
        f.LoggingEnabled = LoggingCheck.IsChecked == true;
        f.AllowPermanentDelete = AllowPermanentDeleteCheck.IsChecked == true;
        f.ConfirmBeforeDelete = ConfirmBeforeDeleteCheck.IsChecked == true;
        f.ExternalEditorPath = ExternalEditorPathText.Text;
        f.JournalSafe = JournalSafeRadio.IsChecked == true;
        f.RamCachePercent = RamCacheSlider.Value;
        f.ShowInfoOverlay = ShowInfoOverlayCheck.IsChecked == true;
        f.ShowFileInfo = ShowFileInfoCheck.IsChecked == true;
        f.ShowFolderInfo = ShowFolderInfoCheck.IsChecked == true;
        f.MouseWheelActionIndex = MouseWheelActionCombo.SelectedIndex;
        f.ClickToZoomEnabled = ClickToZoomCheck.IsChecked == true;
        f.KineticPanEnabled = KineticPanCheck.IsChecked == true;
        f.KineticGlideSmoothingIndex = KineticGlideSmoothingCombo.SelectedIndex;
        f.ArrowKeyNavigatesAtZoomEdge = ArrowKeyNavigatesAtZoomEdgeCheck.IsChecked == true;
        foreach (var (id, check) in _contextMenuChecks)
            if (check.IsChecked == true) f.ContextMenuShown.Add(id);
        f.ClickZoomKeyTogglesFit = ClickZoomKeyTogglesFitCheck.IsChecked == true;
        f.KeyboardZoomAnchorIndex = KeyboardZoomAnchorCombo.SelectedIndex;
        f.MoveCopyReuseLastFolder = MoveCopyReuseLastFolderCheck.IsChecked == true;
        f.ShowExifInfo = ShowExifInfoCheck.IsChecked == true;
        foreach (var (box, flag) in ExifFieldMap)
            if (box.IsChecked == true) f.ExifInfoFields |= flag;
        foreach (var (box, flag) in TitleBarFieldMap)
            if (box.IsChecked == true) f.TitleBarFields |= flag;
        f.ClickZoomPercentText = ClickZoomPercentBox.Text;
        f.ArrowPanStepText = ArrowPanStepBox.Text;
        f.KeyboardZoomStepPercentText = KeyboardZoomStepPercentBox.Text;
        f.ImageTransitionIndex = ImageTransitionCombo.SelectedIndex;
        f.ImageTransitionMsText = ImageTransitionMsBox.Text;
        f.TouchpadSwipeEnabled = TouchpadSwipeCheck.IsChecked == true;
        f.TouchpadSwipeDistanceText = TouchpadSwipeDistanceBox.Text;
        f.PreloadForwardText = PreloadForwardBox.Text;
        f.PreloadBackwardText = PreloadBackwardBox.Text;
        f.ToolbarAutoHide = ToolbarAutoHideCheck.IsChecked == true;
        f.ToolbarAutoHideDelayText = ToolbarAutoHideDelayBox.Text;
        f.InfoOverlayAutoHide = InfoOverlayAutoHideCheck.IsChecked == true;
        f.InfoOverlayAutoHideDelayText = InfoOverlayAutoHideDelayBox.Text;
        f.ToolbarOpacityValue = ToolbarOpacitySlider.Value;
        f.InfoOverlayFontSizeText = InfoOverlayFontSizeBox.Text;
        f.ShowZoomIndicator = ShowZoomIndicatorCheck.IsChecked == true;
        f.SelectedLanguage = LanguageCombo.SelectedItem as LanguageOption;
        return f;
    }

    private Dictionary<string, string> CurrentShortcutTexts() =>
        ShortcutBoxes.ToDictionary(pair => pair.Property, pair => pair.Box.Text, StringComparer.Ordinal);

    // ---- Dependent enable/disable and value labels ----

    private void UpdateZoomShortcutSummary()
    {
        if (ZoomShortcutsSummary is null || ShortcutBoxes is null) return; // can fire while InitializeComponent is still building the tree
        ZoomShortcutsSummary.Text = SettingsPresenter.ZoomShortcutSummary(CurrentShortcutTexts());
    }

    private void ToolbarAutoHideCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateToolbarAutoHideEnabled();

    private void UpdateToolbarAutoHideEnabled() => ToolbarAutoHideDelayBox.IsEnabled = ToolbarAutoHideCheck.IsChecked == true;

    private void InfoOverlayAutoHideCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateInfoOverlayAutoHideEnabled();

    private void UpdateInfoOverlayAutoHideEnabled() => InfoOverlayAutoHideDelayBox.IsEnabled = InfoOverlayAutoHideCheck.IsChecked == true;

    private void ImageTransitionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImageTransitionMsEnabled();

    private void UpdateImageTransitionMsEnabled()
    {
        if (ImageTransitionMsBox is null) return; // SelectionChanged can fire while InitializeComponent is still building the tree
        ImageTransitionMsBox.IsEnabled = ImageTransitionCombo.SelectedIndex == 1;
    }

    private void ToolbarOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateToolbarOpacityValueText();

    private void UpdateToolbarOpacityValueText()
    {
        if (ToolbarOpacityValueText is null) return; // ValueChanged can fire while InitializeComponent is still building the tree
        ToolbarOpacityValueText.Text = SettingsPresenter.ToolbarOpacityValueText(ToolbarOpacitySlider.Value);
    }

    private void ShowInfoOverlayCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateShowInfoSubOptionsEnabled();

    private void UpdateShowInfoSubOptionsEnabled()
    {
        var enabled = ShowInfoOverlayCheck.IsChecked == true;
        ShowFileInfoCheck.IsEnabled = enabled;
        ShowFolderInfoCheck.IsEnabled = enabled;
    }

    private void ShowExifInfoCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateExifFieldsEnabled();

    /// <summary>The per-field checkboxes only matter when ShowExifInfo is on (and, at runtime, when ShowInfoOverlay is too).</summary>
    private void UpdateExifFieldsEnabled()
    {
        var enabled = ShowExifInfoCheck.IsChecked == true;
        foreach (var (box, _) in ExifFieldMap) box.IsEnabled = enabled;
    }

    // ---- Optional shortcuts (ShortcutMappings.OptionalNames): a small "clear" button empties the box. ----

    private void ClearLastImage_Click(object sender, RoutedEventArgs e) => ClearShortcut(LastImageText);
    private void ClearZoomActualSize_Click(object sender, RoutedEventArgs e) => ClearShortcut(ZoomActualSizeText);
    private void ClearToggleInfoOverlay_Click(object sender, RoutedEventArgs e) => ClearShortcut(ToggleInfoOverlayText);
    private void ClearMoveToFolder_Click(object sender, RoutedEventArgs e) => ClearShortcut(MoveToFolderText);
    private void ClearCopyToFolder_Click(object sender, RoutedEventArgs e) => ClearShortcut(CopyToFolderText);
    private void ClearClickZoom_Click(object sender, RoutedEventArgs e) => ClearShortcut(ClickZoomText);
    private void ClearFitWidth_Click(object sender, RoutedEventArgs e) => ClearShortcut(FitWidthText);
    private void ClearFitWidth2_Click(object sender, RoutedEventArgs e) => ClearShortcut(FitWidth2Text);
    private void ClearRefresh_Click(object sender, RoutedEventArgs e) => ClearShortcut(RefreshText);
    private void ClearFitHeight_Click(object sender, RoutedEventArgs e) => ClearShortcut(FitHeightText);
    private void ClearToggleKeepZoom_Click(object sender, RoutedEventArgs e) => ClearShortcut(ToggleKeepZoomText);
    private void ClearOpenFolder_Click(object sender, RoutedEventArgs e) => ClearShortcut(OpenFolderText);
    private void ClearCustomZoom_Click(object sender, RoutedEventArgs e) => ClearShortcut(CustomZoomText);
    private void ClearToggleCaptureMember_Click(object sender, RoutedEventArgs e) => ClearShortcut(ToggleCaptureMemberText);

    private static void ClearShortcut(System.Windows.Controls.TextBox textBox) => textBox.Text = string.Empty;

    // ---- Live duplicate-shortcut warning (the presenter reuses SettingsValidator, the same check Save runs). ----

    private void Shortcut_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDuplicateWarning();
        UpdateZoomShortcutSummary();
    }

    private void UpdateDuplicateWarning()
    {
        if (ShortcutDuplicateWarningText is null || ShortcutBoxes is null) return; // can fire while InitializeComponent is still building the tree
        var message = _presenter.DuplicateShortcutWarning(CurrentShortcutTexts(), ActionsText.Text);
        ShortcutDuplicateWarningText.Text = message ?? string.Empty;
        ShortcutDuplicateWarningText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- RAM%: in-memory cache share of physical RAM ----

    private void RamCacheSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRamCacheValueText();

    /// <summary>
    /// The RAM slider's minimum must hold the preload window the user is currently editing, not just the saved one -- so it
    /// is recomputed whenever the preload boxes change, not only on load.
    /// </summary>
    private void PreloadWindow_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRamCacheRange();
        UpdatePreloadWindowHint();
    }

    private void UpdateRamCacheRange()
    {
        if (RamCacheSlider is null) return; // can fire while InitializeComponent is still building the tree
        var (min, max) = _presenter.RamCacheRange(PreloadForwardBox?.Text, PreloadBackwardBox?.Text);
        RamCacheSlider.Minimum = min;
        RamCacheSlider.Maximum = max;
        if (RamCacheSlider.Value < RamCacheSlider.Minimum) RamCacheSlider.Value = RamCacheSlider.Minimum;
        RamCacheHint.Text = _presenter.RamCacheHintText((int)RamCacheSlider.Minimum, (int)RamCacheSlider.Maximum);
    }

    private void UpdatePreloadWindowHint()
    {
        if (PreloadWindowHint is null) return; // can fire while InitializeComponent is still building the tree
        PreloadWindowHint.Text = SettingsPresenter.PreloadWindowHint();
    }

    private void UpdateRamCacheValueText()
    {
        if (RamCacheValueText is null) return; // ValueChanged can fire while InitializeComponent is still building the tree
        RamCacheValueText.Text = _presenter.RamCacheValueText((int)Math.Round(RamCacheSlider.Value));
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        _presenter.ApplyDefaults();
        LoadFields();
    }

    /// <summary>
    /// Every "this input is invalid, Save was refused" message goes through here: <see cref="InvalidSettingsWarning"/>
    /// (a test seam -- also used by callers that want to react to the warning) when set, a real MessageBox otherwise.
    /// </summary>
    private void ShowInvalid(string message)
    {
        if (InvalidSettingsWarning is { } warn) warn(message);
        else System.Windows.MessageBox.Show(this, message, Tr.DialogSettingsInvalidTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var result = _presenter.TrySave(ReadFields());
        switch (result.Outcome)
        {
            case SettingsSaveOutcome.Invalid:
                ShowInvalid(result.Message!);
                FocusField(result.Focus);
                return;
            case SettingsSaveOutcome.SaveFailed:
                System.Windows.MessageBox.Show(this, result.Message!, Tr.DialogSettingsSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
        }
        DialogResult = true;
    }

    private void FocusField(SettingsFocusField field)
    {
        switch (field)
        {
            case SettingsFocusField.ArrowPanStep: ArrowPanStepBox.Focus(); break;
            case SettingsFocusField.KeyboardZoomStep: KeyboardZoomStepPercentBox.Focus(); break;
            case SettingsFocusField.ImageTransitionMs: ImageTransitionMsBox.Focus(); break;
            case SettingsFocusField.TouchpadSwipeDistance: TouchpadSwipeDistanceBox.Focus(); break;
            case SettingsFocusField.PreloadForward: PreloadForwardBox.Focus(); break;
            case SettingsFocusField.PreloadBackward: PreloadBackwardBox.Focus(); break;
            case SettingsFocusField.InfoOverlayAutoHideDelay: InfoOverlayAutoHideDelayBox.Focus(); break;
        }
    }

    private static void ShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // An active IME (Vietnamese) reports ImeProcessed; the real key is in ImeProcessedKey.
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        // Tab/Escape/modifiers and anything else that can never be a shortcut must not be swallowed (focus trap, Esc cancel).
        if (SettingsPresenter.CaptureShortcut((PhotoReview.App.Input.KeyId)(int)key, key.ToString()) is not { } text) return;
        textBox.Text = text;
        textBox.SelectAll();
        e.Handled = true;
    }

    private void EditActions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var editor = new ActionProfilesWindow(SettingsPresenter.ActionsForEditor(ActionsText.Text)) { Owner = this };
            if (editor.ShowDialog() == true) ActionsText.Text = SettingsPresenter.ActionsToText(editor.Actions);
        }
        catch { System.Windows.MessageBox.Show(this, Tr.DialogOpenEditorFailedMessage, Tr.DialogOpenEditorFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- Q-R48: external editor executable path ----

    private void BrowseExternalEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Tr.SettingsExternalEditorPathLabel, Filter = Tr.DialogFileFilterExecutable };
        if (!string.IsNullOrWhiteSpace(ExternalEditorPathText.Text) && File.Exists(ExternalEditorPathText.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(ExternalEditorPathText.Text);
        if (dialog.ShowDialog(this) == true) ExternalEditorPathText.Text = dialog.FileName;
    }

    // ---- Q-R50: export/import the whole settings file, through the same JSON contract config.json uses ----

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Tr.DialogExportSettingsTitle, Filter = Tr.DialogFileFilterJson,
            InitialDirectory = SettingsPresenter.SettingsFileDialogDirectory, FileName = "PhotoReview-settings.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportSettingsTo(dialog.FileName);
    }

    /// <summary>The body of Export once the user picked a file (a test seam: the file dialog cannot be driven in a test).</summary>
    internal void ExportSettingsTo(string path)
    {
        if (_presenter.ExportSettingsTo(path) is { } error) ShowInvalid(error);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Tr.DialogImportSettingsTitle, Filter = Tr.DialogFileFilterJson, InitialDirectory = SettingsPresenter.SettingsFileDialogDirectory,
        };
        if (dialog.ShowDialog(this) != true) return;
        ImportSettingsFrom(dialog.FileName);
    }

    /// <summary>The body of Import once the user picked a file (a test seam: the file dialog cannot be driven in a test).</summary>
    internal void ImportSettingsFrom(string path)
    {
        var result = _presenter.ImportSettingsFrom(path, LoadFields);
        if (result.Error is not null) ShowInvalid(result.Error);
        else if (result.RepairsNotice is not null) ShowImportRepairs(result.RepairsNotice);
    }

    /// <summary>Fills the window from imported text (test seam); see <see cref="SettingsPresenter.TryApplyImportedJson"/>.</summary>
    internal bool TryApplyImportedJson(string json, out string? message) => _presenter.TryApplyImportedJson(json, LoadFields, out message);

    /// <summary>Tells the user which imported values were reset; the import itself succeeded (the window is filled, nothing saved yet).</summary>
    private void ShowImportRepairs(string message)
    {
        if (ImportRepairsNotice is { } notice) notice(message);
        else System.Windows.MessageBox.Show(this, message, Tr.DialogImportSettingsTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
