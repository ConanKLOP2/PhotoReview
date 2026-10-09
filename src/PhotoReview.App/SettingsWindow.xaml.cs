using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Updates;


namespace PhotoReview.App;

public partial class SettingsWindow : Window
{
    // Relaxed escaping: the JSON is only shown in a local text box, and the default encoder turns every
    // non-ASCII character into an escape sequence (Vietnamese action names became unreadable).
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly SettingsStore? _store;
    private readonly LocalizationService? _localization;
    private readonly IUpdateChecker? _updateChecker;
    private Action? _cancelUpdateCheck;
    private string? _updateUrl;
    public AppSettings Settings { get; private set; }

    /// <summary>Test seam: receives the invalid-destination warning of Save instead of a MessageBox.</summary>
    internal Action<string>? InvalidSettingsWarning { get; set; }
    internal Action<string>? ImportRepairsNotice { get; set; } // test seam for the "some imported values were reset" info message

    /// <summary>Session-only "remember the last opened page" (DR02): resets on process restart, not persisted to disk.</summary>
    private static string s_lastPageKey = "General";

    private Dictionary<string, (ScrollViewer Scroll, string TitleKey)>? _pages;

    public SettingsWindow(SettingsStore store, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null, IUpdateChecker? updateChecker = null)
        : this(store.Current, decoderFactory, localization, updateChecker)
    {
        _store = store;
    }

    public SettingsWindow(AppSettings current, IImageDecoderFactory? decoderFactory = null, LocalizationService? localization = null, IUpdateChecker? updateChecker = null)
    {
        InitializeComponent();
        DarkTitleBarChrome.Apply(this);
        _localization = localization;
        _updateChecker = updateChecker;
        VersionText.Text = BuildInfo.Describe(typeof(SettingsWindow).Assembly);
        // Structural fix: clone through the same JSON contract used to persist config.json, so every AppSettings
        // property survives round-tripping through this window -- including ones this window has no control for
        // yet -- instead of a hand-written field list that silently drops whatever it forgets (the bug this replaces).
        Settings = AppSettings.Clone(current);

        InitializePages();

        // Single source of truth for the shortcut boxes: input capture, duplicate warning and the "must parse" checks in
        // Save_Click all derive from this list, so a box missing here cannot silently miss one of them.
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
        _pages = new Dictionary<string, (ScrollViewer, string)>(StringComparer.Ordinal)
        {
            ["General"] = (GeneralScrollViewer, TrKeys.SettingsNavGeneral),
            ["Display"] = (DisplayScrollViewer, TrKeys.SettingsNavDisplay),
            ["Mouse"] = (MouseScrollViewer, TrKeys.SettingsNavMouse),
            ["Performance"] = (PerformanceScrollViewer, TrKeys.SettingsNavPerformance),
            ["Shortcuts"] = (ShortcutsScrollViewer, TrKeys.SettingsNavShortcuts),
            ["Files"] = (FilesScrollViewer, TrKeys.SettingsNavFiles),
            ["Diagnostics"] = (DiagnosticsScrollViewer, TrKeys.SettingsNavDiagnostics),
        };
        var target = _pages.ContainsKey(s_lastPageKey) ? s_lastPageKey : "General";
        foreach (ListBoxItem item in NavList.Items)
        {
            if (Equals(item.Tag, target)) { NavList.SelectedItem = item; break; }
        }
        NavList.SelectedItem ??= NavList.Items[0];
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages is null || NavList.SelectedItem is not ListBoxItem { Tag: string key } || !_pages.TryGetValue(key, out var page)) return;
        s_lastPageKey = key;
        foreach (var (pageKey, entry) in _pages)
            entry.Scroll.Visibility = pageKey == key ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageTitle();
        page.Scroll.ScrollToHome();
    }

    private void UpdatePageTitle()
    {
        if (_pages is null || NavList.SelectedItem is not ListBoxItem { Tag: string key } || !_pages.TryGetValue(key, out var page)) return;
        PageTitleText.Text = Localizer.Current.Get(page.TitleKey);
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
        ToolbarOpacityHint.Text = Tr.SettingsToolbarOpacityHint(AppSettings.MinToolbarOpacityPercent);
        KeyboardZoomStepPercentHint.Text = Tr.SettingsKeyboardZoomStepPercentHint(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent);
        UpdateToolbarOpacityValueText();
        UpdateRamCacheRange();
        UpdateRamCacheValueText();
        UpdatePreloadWindowHint();
        UpdateZoomShortcutSummary();
        UpdateWebpHeicCodecStatus();
    }

    /// <summary>Q-FMT-WEBP-HEIC: which Windows codecs this PC has (probed once per process; cheap after the first call).</summary>
    private void UpdateWebpHeicCodecStatus() => WebpHeicCodecStatusText.Text = FormatWebpHeicCodecStatus(PhotoReview.Imaging.Decoding.Wic.WicCodecAvailability.Current);

    /// <summary>The localized codec status line: WebP available/missing; HEIC available, missing, or missing only the HEVC decoder.</summary>
    internal static string FormatWebpHeicCodecStatus(PhotoReview.Imaging.Decoding.Wic.WicCodecSupport codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        var webp = codecs.WebP ? Tr.SettingsWebpHeicSupportEnabledCodecAvailable : Tr.SettingsWebpHeicSupportEnabledWebpMissing;
        var heif = codecs.Heif ? Tr.SettingsWebpHeicSupportEnabledCodecAvailable
            : codecs.HeifContainer ? Tr.SettingsWebpHeicSupportEnabledHevcMissing
            : Tr.SettingsWebpHeicSupportEnabledHeifMissing;
        return Tr.SettingsWebpHeicSupportEnabledStatus(webp, heif);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab cycles pages regardless of which control has focus.</summary>
    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var count = NavList.Items.Count;
        var index = NavList.SelectedIndex;
        var forward = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
        index = ((index + (forward ? 1 : -1)) % count + count) % count;
        NavList.SelectedIndex = index;
        e.Handled = true;
    }

    private void ApplyDecoderAvailability(IImageDecoderFactory? decoderFactory)
    {
        if (decoderFactory is not null && !decoderFactory.IsRegistered(DecoderBackend.TurboJpeg))
        {
            TurboJpegOption.IsEnabled = false;
            TurboJpegOption.ToolTip = Tr.SettingsDecoderBackendTurboJpegUnavailable;
        }
    }

    // ---- I18N L08: language picker, languages folder, reload, export ----

    /// <summary>Fills the picker from the shipped and user folders and selects <see cref="AppSettings.UiLanguage"/>.</summary>
    private void LoadLanguages()
    {
        if (_localization is null)
        {
            // No LocalizationService (a window built without DI): there is nothing to switch, so hide the group.
            LanguageGroup.Visibility = Visibility.Collapsed;
            return;
        }
        var selected = LanguageCombo.SelectedItem is LanguageOption option ? option.Code : Settings.UiLanguage;
        var options = LanguageOptions.Build(_localization.DiscoverLanguages());
        LanguageCombo.ItemsSource = options;
        LanguageCombo.SelectedIndex = LanguageOptions.IndexOf(options, selected);
    }

    private void OpenLanguagesFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        try { Directory.CreateDirectory(_localization.UserLanguagesDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowOpenFailed(ex);
            return;
        }
        StartExplorer($"\"{_localization.UserLanguagesDir}\"");
    }

    private void ReloadTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        var problems = _localization.Reload();
        LoadLanguages(); // a translator may have added a language file
        AppLog.Info("Translations reloaded");
        System.Windows.MessageBox.Show(this, TranslationProblems.Message(problems), Tr.DialogReloadTranslationsTitle, MessageBoxButton.OK,
            TranslationProblems.HasProblems(problems) ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ExportTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (_localization is null) return;
        var language = LanguageOptions.ToSetting(LanguageCombo.SelectedItem as LanguageOption, Settings.UiLanguage);
        string path;
        try
        {
            path = _localization.ExportTodo(language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogExportTranslationFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppLog.Info("Translation export written");
        StartExplorer($"/select,\"{path}\"");
    }

    private void OpenLogLocation_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(AppLog.FilePath)!;
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowOpenFailed(ex);
            return;
        }
        StartExplorer($"/select,\"{AppLog.FilePath}\"");
    }

    private void StartExplorer(string arguments)
    {
        try { using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowOpenFailed(ex); }
    }

    private void ShowOpenFailed(Exception ex) =>
        System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogSettingsOpenFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

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
        _updateUrl = null;
        UpdateStatusText.Text = Tr.UpdateCheckChecking;
        using var cts = new CancellationTokenSource();
        _cancelUpdateCheck = () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }; // the check may already be finished and disposed
        var version = BuildInfo.GetVersion(typeof(SettingsWindow).Assembly);
        UpdateCheckResult result;
        try
        {
            var checker = _updateChecker ?? new UpdateChecker();
            result = await checker.CheckAsync(version, cts.Token);
        }
        catch (OperationCanceledException) { return; } // window closed while checking
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = UpdateCheckResult.Failed(UpdateFailure.BadResponse);
        }
        if (cts.IsCancellationRequested) return;

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                UpdateStatusText.Text = Tr.UpdateStatusUpToDate(result.LatestVersion ?? string.Empty);
                break;
            case UpdateCheckStatus.UpdateAvailable:
                _updateUrl = UpdateUrlPolicy.Validate(result.DownloadUrl);
                var current = AppVersion.TryParse(version, out var parsed) ? parsed.ToString() : version ?? string.Empty;
                UpdateStatusText.Text = Tr.UpdateStatusAvailable(result.LatestVersion ?? string.Empty, current);
                OpenUpdatePageButton.Visibility = _updateUrl is null ? Visibility.Collapsed : Visibility.Visible;
                break;
            default:
                UpdateStatusText.Text = result.Failure switch
                {
                    UpdateFailure.Offline => Tr.UpdateStatusFailedOffline,
                    UpdateFailure.Timeout => Tr.UpdateStatusFailedTimeout,
                    UpdateFailure.RateLimited => Tr.UpdateStatusFailedRateLimited,
                    UpdateFailure.InvalidVersion => Tr.UpdateStatusFailedInvalidVersion,
                    _ => Tr.UpdateStatusFailedBadResponse,
                };
                break;
        }
        CheckUpdateButton.IsEnabled = true;
    }

    private void OpenUpdatePage_Click(object sender, RoutedEventArgs e)
    {
        var url = UpdateUrlPolicy.Validate(_updateUrl);
        if (url is null) return;
        try { OpenUrl(url); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateStatusText.Text = Tr.UpdateOpenPageFailed;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Localizer.CurrentChanged -= OnLanguageChanged;
        _cancelUpdateCheck?.Invoke();
        base.OnClosed(e);
    }

    /// <summary>Set before showing: opens on the Files page and focuses the External Editor path field (menu item with no editor configured).</summary>
    internal bool FocusExternalEditorOnLoad { get; set; }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (FocusExternalEditorOnLoad)
        {
            var rememberedPage = s_lastPageKey; // a one-off jump must not change the page the next plain Settings open shows
            foreach (ListBoxItem item in NavList.Items)
            {
                if (Equals(item.Tag, "Files")) { NavList.SelectedItem = item; break; }
            }
            s_lastPageKey = rememberedPage;
            ExternalEditorPathText.BringIntoView();
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => ExternalEditorPathText.Focus());
            return;
        }
        if (NavList.SelectedItem is ListBoxItem { Tag: string key } && _pages is not null && _pages.TryGetValue(key, out var page))
            page.Scroll.ScrollToHome();
        FocusManager.SetFocusedElement(this, null);
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            Keyboard.ClearFocus();
        });
    }

    private void LoadFields()
    {
        NextText.Text = Settings.Shortcuts.Next; PreviousText.Text = Settings.Shortcuts.Previous;
        RecycleText.Text = Settings.Shortcuts.SendToRecycleBin;
        CompareText.Text = Settings.Shortcuts.Compare; NextFolderText.Text = Settings.Shortcuts.NextFolder; PreviousFolderText.Text = Settings.Shortcuts.PreviousFolder;
        FirstImageText.Text = Settings.Shortcuts.FirstImage; ZoomInText.Text = Settings.Shortcuts.ZoomIn; ZoomOutText.Text = Settings.Shortcuts.ZoomOut; ToggleFitText.Text = Settings.Shortcuts.ToggleFit; SkipText.Text = Settings.Shortcuts.Skip; UndoText.Text = Settings.Shortcuts.Undo; FullscreenText.Text = Settings.Shortcuts.Fullscreen;
        LastImageText.Text = Settings.Shortcuts.LastImage; ZoomActualSizeText.Text = Settings.Shortcuts.ZoomActualSize; ToggleInfoOverlayText.Text = Settings.Shortcuts.ToggleInfoOverlay;
        MoveToFolderText.Text = Settings.Shortcuts.MoveToFolder; CopyToFolderText.Text = Settings.Shortcuts.CopyToFolder;
        ClickZoomText.Text = Settings.Shortcuts.ClickZoom;
        FitWidthText.Text = Settings.Shortcuts.FitWidth; FitHeightText.Text = Settings.Shortcuts.FitHeight; ToggleKeepZoomText.Text = Settings.Shortcuts.ToggleKeepZoom;
        OpenFolderText.Text = Settings.Shortcuts.OpenFolder; CustomZoomText.Text = Settings.Shortcuts.CustomZoom;
        ToggleCaptureMemberText.Text = Settings.Shortcuts.ToggleCaptureMember; FitWidth2Text.Text = Settings.Shortcuts.FitWidth2;
        RefreshText.Text = Settings.Shortcuts.Refresh;
        ActionsText.Text = JsonSerializer.Serialize(Settings.Actions, JsonOptions);
        // PR-B: Percent400 was removed from the combo; SettingsNormalizer migrates a loaded value to Percent200 before
        // this window ever sees it, but a stray Percent400 (e.g. this window built directly on an unnormalized
        // AppSettings in a test) still lands on the 200% item rather than falling through to Fit.
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch
        {
            InitialViewMode.FitWidth => 1,
            InitialViewMode.FitHeight => 2,
            InitialViewMode.ClickZoomLevel => 3,
            InitialViewMode.Percent100 => 4,
            InitialViewMode.Percent200 or InitialViewMode.Percent400 => 5,
            _ => 0
        };
        FitWidthAnchorCombo.SelectedIndex = AnchorToIndex(Settings.FitWidthAnchor, FitWidthAnchor.Centre);
        FitWidthAnchor2Combo.SelectedIndex = AnchorToIndex(Settings.FitWidthAnchor2, FitWidthAnchor.BottomThird);
        MiddleClickActionCombo.SelectedIndex = Enum.IsDefined(Settings.MiddleClickAction) ? (int)Settings.MiddleClickAction : (int)MiddleClickAction.ActualSize;
        KeepZoomAcrossImagesCheck.IsChecked = Settings.KeepZoomAcrossImages;
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode switch { LoadingMode.Preview => 1, LoadingMode.Original => 2, _ => 0 };
        SortModeCombo.SelectedIndex = Math.Max(0, SortModeCombo.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => Equals(item.Tag, Settings.ImageSortMode.ToString())));
        ScalingQualityCombo.SelectedIndex = Settings.ScalingQuality == ScalingQuality.Linear ? 1 : 0;
        DecoderBackendCombo.SelectedIndex = Settings.DecoderBackend switch { DecoderBackend.WicDirect => 1, DecoderBackend.TurboJpeg => 2, _ => 0 };
        InstanceModeCombo.SelectedIndex = Settings.InstanceMode == InstanceMode.PerFolder ? 1 : 0;
        CompareHashCheck.IsChecked = Settings.CompareHashEnabled;
        CompareSizeCheck.IsChecked = Settings.CompareSizeEnabled;
        RawSupportEnabledCheck.IsChecked = Settings.RawSupportEnabled;
        WebpHeicSupportEnabledCheck.IsChecked = Settings.WebpHeicSupportEnabled;
        UpdateWebpHeicCodecStatus();
        RawFullDecodeCombo.SelectedIndex = Settings.RawFullDecode == RawFullDecode.OnZoom ? 1 : 0;
        RawPairModeCombo.SelectedIndex = Settings.RawPairMode switch
        {
            RawPairMode.PreferJpeg => 1,
            RawPairMode.PreferRaw => 2,
            _ => 0
        };
        LoggingCheck.IsChecked = Settings.LoggingEnabled;
        AllowPermanentDeleteCheck.IsChecked = Settings.AllowPermanentDeleteWithoutRecycleBin;
        ConfirmBeforeDeleteCheck.IsChecked = Settings.ConfirmBeforeDelete;
        ExternalEditorPathText.Text = Settings.ExternalEditorPath;
        JournalSafeRadio.IsChecked = Settings.JournalDurability == JournalDurability.PowerLossSafe;
        JournalFastRadio.IsChecked = !JournalSafeRadio.IsChecked;
        ShowInfoOverlayCheck.IsChecked = Settings.ShowInfoOverlay;
        ShowFileInfoCheck.IsChecked = Settings.ShowFileInfo;
        ShowFolderInfoCheck.IsChecked = Settings.ShowFolderInfo;
        InfoOverlayFontSizeBox.Text = Settings.InfoOverlayFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowZoomIndicatorCheck.IsChecked = Settings.ShowZoomIndicator;
        ToolbarAutoHideCheck.IsChecked = Settings.ToolbarAutoHide;
        ToolbarAutoHideDelayBox.Text = Settings.ToolbarAutoHideDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InfoOverlayAutoHideCheck.IsChecked = Settings.InfoOverlayAutoHide;
        InfoOverlayAutoHideDelayBox.Text = Settings.InfoOverlayAutoHideDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ToolbarOpacitySlider.Minimum = AppSettings.MinToolbarOpacityPercent;
        ToolbarOpacitySlider.Maximum = AppSettings.MaxToolbarOpacityPercent;
        ToolbarOpacitySlider.Value = Math.Clamp(Settings.ToolbarOpacityPercent, AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
        ToolbarOpacityHint.Text = Tr.SettingsToolbarOpacityHint(AppSettings.MinToolbarOpacityPercent);
        KeyboardZoomStepPercentHint.Text = Tr.SettingsKeyboardZoomStepPercentHint(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent);
        UpdateToolbarOpacityValueText();
        MouseWheelActionCombo.SelectedIndex = Settings.MouseWheelAction == MouseWheelAction.Navigate ? 1 : 0;
        ClickToZoomCheck.IsChecked = Settings.ClickToZoomEnabled;
        ClickZoomPercentBox.Text = Settings.ClickZoomPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PreloadForwardBox.Text = Settings.PreloadForwardCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PreloadBackwardBox.Text = Settings.PreloadBackwardCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KineticPanCheck.IsChecked = Settings.KineticPanEnabled;
        KineticGlideSmoothingCombo.SelectedIndex = Settings.KineticGlideSmoothing == KineticGlideSmoothing.Predict ? 1 : 0;
        ArrowKeyNavigatesAtZoomEdgeCheck.IsChecked = Settings.ArrowKeyNavigatesAtZoomEdge;
        ClickZoomKeyTogglesFitCheck.IsChecked = Settings.ClickZoomKeyTogglesFit;
        ShowFolderMenuItemsCheck.IsChecked = Settings.ShowFolderMenuItems;
        ShowZoomMenuItemsCheck.IsChecked = Settings.ShowZoomMenuItems;
        ShowRecycleMenuItemCheck.IsChecked = Settings.ShowRecycleMenuItem;
        ArrowPanStepBox.Text = Settings.ArrowPanStepPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeyboardZoomStepPercentBox.Text = Settings.KeyboardZoomStepPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KeyboardZoomAnchorCombo.SelectedIndex = Settings.KeyboardZoomAnchor == KeyboardZoomAnchor.ViewportCentre ? 1 : 0;
        ImageTransitionCombo.SelectedIndex = Settings.ImageTransition == ImageTransition.Fade ? 1 : 0;
        ImageTransitionMsBox.Text = Settings.ImageTransitionMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TouchpadSwipeCheck.IsChecked = Settings.TouchpadSwipeEnabled;
        TouchpadSwipeDistanceBox.Text = Settings.TouchpadSwipeDistancePerImage.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UpdateImageTransitionMsEnabled();
        MoveCopyReuseLastFolderCheck.IsChecked = Settings.MoveCopyReuseLastFolder;
        ShowExifInfoCheck.IsChecked = Settings.ShowExifInfo;
        ExifFieldFileNameCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.FileName);
        ExifFieldDateTakenCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.DateTaken);
        ExifFieldModifiedDateCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.ModifiedDate);
        ExifFieldDimensionsCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.Dimensions);
        ExifFieldCameraCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.Camera);
        ExifFieldLensCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.Lens);
        ExifFieldIsoCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.Iso);
        ExifFieldFocalLengthCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.FocalLength);
        ExifFieldApertureCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.Aperture);
        ExifFieldShutterSpeedCheck.IsChecked = Settings.ExifInfoFields.HasFlag(ExifInfoFields.ShutterSpeed);
        TitleBarFieldFolderNameCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.FolderName);
        TitleBarFieldFolderPathCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.FolderPath);
        TitleBarFieldIndexCountCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.IndexCount);
        TitleBarFieldFileNameCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.FileName);
        TitleBarFieldFileSizeCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.FileSize);
        TitleBarFieldDimensionsCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.Dimensions);
        TitleBarFieldModifiedDateCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.ModifiedDate);
        TitleBarFieldDateTakenCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.DateTaken);
        TitleBarFieldCameraCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.Camera);
        TitleBarFieldLensCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.Lens);
        TitleBarFieldIsoCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.Iso);
        TitleBarFieldFocalLengthCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.FocalLength);
        TitleBarFieldApertureCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.Aperture);
        TitleBarFieldShutterSpeedCheck.IsChecked = Settings.TitleBarFields.HasFlag(TitleBarFields.ShutterSpeed);
        UpdateZoomShortcutSummary();
        UpdateShowInfoSubOptionsEnabled();
        UpdateExifFieldsEnabled();
        UpdateToolbarAutoHideEnabled();
        UpdateInfoOverlayAutoHideEnabled();
        LoadRamCache();
        UpdateDuplicateWarning();
    }

    /// <summary>
    /// The zoom card lists the zoom shortcuts as currently typed on the Shortcuts page (read-only here: they are edited
    /// on that page), so the user sees which keys drive the zoom level without switching pages.
    /// </summary>
    // The Fit width combos list the FitWidthAnchor members in enum order (Centre, Top third, Bottom third): index == value.
    private static int AnchorToIndex(FitWidthAnchor anchor, FitWidthAnchor fallback) => (int)(Enum.IsDefined(anchor) ? anchor : fallback);

    private static FitWidthAnchor IndexToAnchor(int index, FitWidthAnchor fallback) => index >= 0 && Enum.IsDefined((FitWidthAnchor)index) ? (FitWidthAnchor)index : fallback;

    private void UpdateZoomShortcutSummary()
    {
        if (ZoomShortcutsSummary is null) return; // can fire while InitializeComponent is still building the tree
        string Key(System.Windows.Controls.TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? Tr.SettingsZoomShortcutsNone : box.Text.Trim();
        ZoomShortcutsSummary.Text = string.Join(Environment.NewLine,
            $"{Tr.SettingsShortcutClickZoom}: {Key(ClickZoomText)}",
            $"{Tr.SettingsShortcutToggleFit}: {Key(ToggleFitText)}",
            $"{Tr.SettingsShortcutZoomActualSize}: {Key(ZoomActualSizeText)}",
            $"{Tr.SettingsShortcutZoomIn}: {Key(ZoomInText)}",
            $"{Tr.SettingsShortcutZoomOut}: {Key(ZoomOutText)}",
            $"{Tr.SettingsShortcutFitWidth}: {Key(FitWidthText)}",
            $"{Tr.SettingsShortcutFitWidth2}: {Key(FitWidth2Text)}",
            $"{Tr.SettingsShortcutFitHeight}: {Key(FitHeightText)}");
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
        ToolbarOpacityValueText.Text = string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{(int)Math.Round(ToolbarOpacitySlider.Value)} %");
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
        foreach (var check in ExifFieldChecks) check.IsEnabled = enabled;
    }

    private IEnumerable<System.Windows.Controls.CheckBox> ExifFieldChecks =>
    [
        ExifFieldFileNameCheck, ExifFieldDateTakenCheck, ExifFieldModifiedDateCheck, ExifFieldDimensionsCheck, ExifFieldCameraCheck, ExifFieldLensCheck,
        ExifFieldIsoCheck, ExifFieldFocalLengthCheck, ExifFieldApertureCheck, ExifFieldShutterSpeedCheck,
    ];

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

    // ---- Live duplicate-shortcut warning (reuses SettingsValidator, the same check Save runs). ----

    private void Shortcut_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateDuplicateWarning();
        UpdateZoomShortcutSummary();
    }

    private void UpdateDuplicateWarning()
    {
        if (ShortcutDuplicateWarningText is null) return; // can fire while InitializeComponent is still building the tree
        var probe = BuildProbeSettings();
        var message = _store is not null ? _store.ValidateShortcuts(probe) : new SettingsValidator(new WpfKeyNameValidator()).ValidateShortcuts(probe);
        ShortcutDuplicateWarningText.Text = message ?? string.Empty;
        ShortcutDuplicateWarningText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Cache for <see cref="GetParsedActionsForProbe"/>: the ActionsText the cached parse came from.</summary>
    private string? _cachedProbeActionsJson;

    /// <summary>Cache for <see cref="GetParsedActionsForProbe"/>: the parse result for <see cref="_cachedProbeActionsJson"/>.</summary>
    private List<ReviewAction> _cachedProbeActions = [];

    /// <summary>
    /// Shortcut_TextChanged fires <see cref="BuildProbeSettings"/> on every keystroke in any of the 25 shortcut
    /// boxes, which used to re-parse the (unrelated, unchanged) Actions JSON every time too. ActionsText only
    /// actually changes when the user types there or the Action Profiles editor rewrites it, so cache the parse
    /// keyed on the exact text and skip re-parsing while it's unchanged.
    /// </summary>
    private List<ReviewAction> GetParsedActionsForProbe()
    {
        var text = ActionsText.Text;
        if (_cachedProbeActionsJson == text) return _cachedProbeActions;
        try { _cachedProbeActions = JsonSerializer.Deserialize<List<ReviewAction>>(text) ?? []; }
        catch (JsonException) { _cachedProbeActions = []; } // invalid JSON while typing: Save's own check reports that separately
        _cachedProbeActionsJson = text;
        return _cachedProbeActions;
    }

    /// <summary>
    /// Reads all 25 shortcut text boxes into a <see cref="ShortcutMappings"/>, exactly as typed (not canonicalized).
    /// The single place both <see cref="Save_Click"/> and <see cref="BuildProbeSettings"/> read the shortcut
    /// controls from, so the field list only has to be kept in sync with the XAML controls once, not twice.
    /// </summary>
    private ShortcutMappings ReadShortcutsFromUi() => new()
    {
        Next = NextText.Text, Previous = PreviousText.Text, FirstImage = FirstImageText.Text, LastImage = LastImageText.Text,
        NextFolder = NextFolderText.Text, PreviousFolder = PreviousFolderText.Text,
        ZoomIn = ZoomInText.Text, ZoomOut = ZoomOutText.Text, ZoomActualSize = ZoomActualSizeText.Text, ToggleFit = ToggleFitText.Text,
        Fullscreen = FullscreenText.Text, ToggleInfoOverlay = ToggleInfoOverlayText.Text,
        Skip = SkipText.Text, Undo = UndoText.Text, Compare = CompareText.Text,
        MoveToFolder = MoveToFolderText.Text, CopyToFolder = CopyToFolderText.Text, SendToRecycleBin = RecycleText.Text,
        ClickZoom = ClickZoomText.Text,
        FitWidth = FitWidthText.Text, FitHeight = FitHeightText.Text, ToggleKeepZoom = ToggleKeepZoomText.Text,
        OpenFolder = OpenFolderText.Text, CustomZoom = CustomZoomText.Text, ToggleCaptureMember = ToggleCaptureMemberText.Text,
        FitWidth2 = FitWidth2Text.Text, Refresh = RefreshText.Text,
    };

    /// <summary>
    /// Same field list as <see cref="ReadShortcutsFromUi"/>, with every value run through <see cref="ShortcutKeyCanonical.Canonicalize"/>
    /// (what Save persists). <paramref name="existing"/> is the settings' current <see cref="ShortcutMappings"/>
    /// (before this Save): <see cref="ShortcutMappings.MoveToFolder2"/> is a legacy alias with no UI control (owned
    /// by <c>ReviewAction</c> instead -- see <see cref="SettingsValidator.ValidateShortcuts"/>), so it must be
    /// carried over from there rather than silently reset to <see cref="ShortcutMappings"/>'s own default "Enter".
    /// </summary>
    private static ShortcutMappings CanonicalizeShortcuts(ShortcutMappings raw, ShortcutMappings existing) => new()
    {
        Next = ShortcutKeyCanonical.Canonicalize(raw.Next), Previous = ShortcutKeyCanonical.Canonicalize(raw.Previous),
        FirstImage = ShortcutKeyCanonical.Canonicalize(raw.FirstImage), LastImage = ShortcutKeyCanonical.Canonicalize(raw.LastImage),
        NextFolder = ShortcutKeyCanonical.Canonicalize(raw.NextFolder), PreviousFolder = ShortcutKeyCanonical.Canonicalize(raw.PreviousFolder),
        ZoomIn = ShortcutKeyCanonical.Canonicalize(raw.ZoomIn), ZoomOut = ShortcutKeyCanonical.Canonicalize(raw.ZoomOut),
        ZoomActualSize = ShortcutKeyCanonical.Canonicalize(raw.ZoomActualSize), ToggleFit = ShortcutKeyCanonical.Canonicalize(raw.ToggleFit),
        Fullscreen = ShortcutKeyCanonical.Canonicalize(raw.Fullscreen), ToggleInfoOverlay = ShortcutKeyCanonical.Canonicalize(raw.ToggleInfoOverlay),
        Skip = ShortcutKeyCanonical.Canonicalize(raw.Skip), Undo = ShortcutKeyCanonical.Canonicalize(raw.Undo), Compare = ShortcutKeyCanonical.Canonicalize(raw.Compare),
        MoveToFolder = ShortcutKeyCanonical.Canonicalize(raw.MoveToFolder), CopyToFolder = ShortcutKeyCanonical.Canonicalize(raw.CopyToFolder),
        SendToRecycleBin = ShortcutKeyCanonical.Canonicalize(raw.SendToRecycleBin), ClickZoom = ShortcutKeyCanonical.Canonicalize(raw.ClickZoom),
        FitWidth = ShortcutKeyCanonical.Canonicalize(raw.FitWidth), FitHeight = ShortcutKeyCanonical.Canonicalize(raw.FitHeight),
        ToggleKeepZoom = ShortcutKeyCanonical.Canonicalize(raw.ToggleKeepZoom), OpenFolder = ShortcutKeyCanonical.Canonicalize(raw.OpenFolder),
        CustomZoom = ShortcutKeyCanonical.Canonicalize(raw.CustomZoom), ToggleCaptureMember = ShortcutKeyCanonical.Canonicalize(raw.ToggleCaptureMember),
        FitWidth2 = ShortcutKeyCanonical.Canonicalize(raw.FitWidth2), Refresh = ShortcutKeyCanonical.Canonicalize(raw.Refresh),
        MoveToFolder2 = existing.MoveToFolder2,
    };

    /// <summary>A throwaway settings snapshot from the current text boxes, used only to preview validation live.</summary>
    private AppSettings BuildProbeSettings()
    {
        var probe = new AppSettings { Shortcuts = ReadShortcutsFromUi(), Actions = GetParsedActionsForProbe() };
        return probe;
    }

    // ---- RAM%: in-memory cache share of physical RAM ----

    private readonly long _physicalMemoryBytes = RamBudgetPolicy.GetPhysicalMemoryBytes();

    /// <summary>Slider range = [window-dependent minimum, 90] for this device; the value is the saved percent clamped into it.</summary>
    private void LoadRamCache()
    {
        UpdateRamCacheRange();
        RamCacheSlider.Value = Math.Clamp(Settings.ImageCacheRamPercent, (int)RamCacheSlider.Minimum, (int)RamCacheSlider.Maximum);
        UpdateRamCacheValueText();
        UpdatePreloadWindowHint();
    }

    private void RamCacheSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRamCacheValueText();

    /// <summary>
    /// feat/preload-window-setting: the RAM slider's minimum must hold the preload window the user is currently
    /// editing, not just the saved one -- so it is recomputed whenever <see cref="PreloadForwardBox"/>/
    /// <see cref="PreloadBackwardBox"/> change, not only on load. An unparsable/out-of-range box falls back to
    /// its saved value for this preview only; Save itself still validates and refuses those inputs.
    /// </summary>
    private void PreloadWindow_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRamCacheRange();
        UpdatePreloadWindowHint();
    }

    private void UpdateRamCacheRange()
    {
        if (RamCacheSlider is null) return; // can fire while InitializeComponent is still building the tree
        RamCacheSlider.Minimum = RamBudgetPolicy.MinimumCachePercent(_physicalMemoryBytes, CurrentPreloadWindowForPreview());
        RamCacheSlider.Maximum = PerformanceOptions.MaxImageCacheRamPercent;
        if (RamCacheSlider.Value < RamCacheSlider.Minimum) RamCacheSlider.Value = RamCacheSlider.Minimum;
        RamCacheHint.Text = Tr.SettingsRamCacheHint(FormatGb(_physicalMemoryBytes), (int)RamCacheSlider.Minimum, (int)RamCacheSlider.Maximum);
    }

    private void UpdatePreloadWindowHint()
    {
        if (PreloadWindowHint is null) return; // can fire while InitializeComponent is still building the tree
        PreloadWindowHint.Text = Tr.SettingsPreloadWindowHint(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
    }

    /// <summary>Best-effort preload window from the current text boxes, for the RAM-floor preview only (Save does the real validation).</summary>
    private PreloadWindow CurrentPreloadWindowForPreview()
    {
        var forward = ParseOrFallback(PreloadForwardBox?.Text, Settings.PreloadForwardCount,
            PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount);
        var backward = ParseOrFallback(PreloadBackwardBox?.Text, Settings.PreloadBackwardCount,
            PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
        return new PreloadWindow(forward, backward);
    }

    private static int ParseOrFallback(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            return Math.Clamp(fallback, min, max);
        return value;
    }

    private int SelectedRamCachePercent => (int)Math.Round(RamCacheSlider.Value);

    private void UpdateRamCacheValueText()
    {
        if (RamCacheValueText is null) return; // ValueChanged can fire while InitializeComponent is still building the tree
        var percent = SelectedRamCachePercent;
        RamCacheValueText.Text = Tr.SettingsRamCacheValue(percent, FormatGb(RamBudgetPolicy.BytesForPercent(percent, _physicalMemoryBytes)));
    }

    /// <summary>UI text in the user's locale (GiB shown as "GB", as Windows does); "?" when RAM is unknown.</summary>
    private static string FormatGb(long bytes) =>
        bytes > 0 ? (bytes / (1024d * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) : "?";

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        Settings.LoggingEnabled = false;
        Settings.JournalDurability = JournalDurability.Fast;
        Settings.AllowPermanentDeleteWithoutRecycleBin = false;
        Settings.ConfirmBeforeDelete = false;
        Settings.ShowZoomIndicator = false;
        Settings.ExternalEditorPath = string.Empty;
        Settings.ImageCacheCapacityBytes = PerformanceOptions.ImageCacheCapacityBytes;
        Settings.ImageCacheRamPercent = PerformanceOptions.ImageCacheRamPercent;
        Settings.MemoryReserveBytes = PerformanceOptions.MemoryReserveBytes;
        Settings.PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount;
        Settings.PreloadMemoryLoadLimit = PerformanceOptions.PreloadMemoryLoadLimit;
        Settings.PreloadForwardCount = PerformanceOptions.PreloadForwardCount;
        Settings.PreloadBackwardCount = PerformanceOptions.PreloadBackwardCount;
        Settings.PreviewDiskCacheCapacityBytes = PerformanceOptions.PreviewDiskCacheCapacityBytes;
        Settings.UseSourceBytesCache = PerformanceOptions.UseSourceBytesCache;
        Settings.SourceBytesCapacityBytes = PerformanceOptions.SourceBytesCapacityBytes;
        Settings.InitialViewMode = InitialViewMode.Fit; Settings.LoadingMode = LoadingMode.Preview; Settings.ImageSortMode = new AppSettings().ImageSortMode; Settings.ScalingQuality = ScalingQuality.HighQuality; Settings.DecoderBackend = new AppSettings().DecoderBackend; Settings.CompareHashEnabled = true; Settings.CompareSizeEnabled = true; Settings.Shortcuts = ShortcutMappings.Default();
        Settings.FitWidthAnchor = FitWidthAnchor.Centre; Settings.KeepZoomAcrossImages = false;
        Settings.FitWidthAnchor2 = new AppSettings().FitWidthAnchor2; Settings.MiddleClickAction = new AppSettings().MiddleClickAction;
        Settings.InstanceMode = InstanceMode.SingleWindow;
        Settings.ShowInfoOverlay = true; Settings.ShowFileInfo = true; Settings.ShowFolderInfo = false;
        Settings.MouseWheelAction = MouseWheelAction.Zoom; Settings.ClickToZoomEnabled = false; Settings.ClickZoomPercent = AppSettings.DefaultClickZoomPercent; Settings.KineticPanEnabled = new AppSettings().KineticPanEnabled; Settings.KineticGlideSmoothing = new AppSettings().KineticGlideSmoothing; Settings.ArrowKeyNavigatesAtZoomEdge = new AppSettings().ArrowKeyNavigatesAtZoomEdge; Settings.ClickZoomKeyTogglesFit = new AppSettings().ClickZoomKeyTogglesFit; Settings.ArrowPanStepPercent = AppSettings.DefaultArrowPanStepPercent; Settings.KeyboardZoomStepPercent = AppSettings.DefaultKeyboardZoomStepPercent; Settings.KeyboardZoomAnchor = new AppSettings().KeyboardZoomAnchor;
        Settings.SetZoomAlsoSetsClickLevel = new AppSettings().SetZoomAlsoSetsClickLevel; Settings.ShowFolderMenuItems = new AppSettings().ShowFolderMenuItems; Settings.ShowZoomMenuItems = new AppSettings().ShowZoomMenuItems; Settings.ShowRecycleMenuItem = new AppSettings().ShowRecycleMenuItem;
        Settings.MoveCopyReuseLastFolder = false;
        Settings.ImageTransition = new AppSettings().ImageTransition; Settings.ImageTransitionMs = AppSettings.DefaultImageTransitionMs;
        Settings.TouchpadSwipeEnabled = new AppSettings().TouchpadSwipeEnabled; Settings.TouchpadSwipeDistancePerImage = AppSettings.DefaultTouchpadSwipeDistancePerImage;
        Settings.ShowExifInfo = new AppSettings().ShowExifInfo; Settings.ExifInfoFields = ExifInfoFields.Default;
        Settings.ToolbarAutoHide = new AppSettings().ToolbarAutoHide; Settings.ToolbarAutoHideDelayMs = AppSettings.DefaultToolbarAutoHideDelayMs; Settings.InfoOverlayAutoHide = new AppSettings().InfoOverlayAutoHide; Settings.InfoOverlayAutoHideDelayMs = AppSettings.DefaultInfoOverlayAutoHideDelayMs; Settings.ToolbarOpacityPercent = AppSettings.DefaultToolbarOpacityPercent;
        Settings.InfoOverlayFontSize = AppSettings.DefaultInfoOverlayFontSize;
        Settings.TitleBarFields = TitleBarFields.Default;
        Settings.RawSupportEnabled = new AppSettings().RawSupportEnabled;
        Settings.WebpHeicSupportEnabled = new AppSettings().WebpHeicSupportEnabled;
        Settings.RawFullDecode = new AppSettings().RawFullDecode;
        Settings.RawPairMode = new AppSettings().RawPairMode;
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
        AppLog.Info("Settings save requested");
        var values = ShortcutBoxes.Where(b => !ShortcutMappings.IsOptional(b.Property)).Select(b => b.Box.Text).ToArray();
        if (values.Any(v => !ShortcutKeyName.TryParse(v, out _)) || values.Select(ShortcutKeyCanonical.Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            ShowInvalid(Tr.DialogSettingsInvalidShortcuts); return;
        }
        // Optional shortcuts (ShortcutMappings.OptionalNames) may be empty (= feature disabled); non-empty ones still
        // have to be a real key name. Cross-duplicate checking against everything else happens in SettingsValidator below.
        var optionalValues = ShortcutBoxes.Where(b => ShortcutMappings.IsOptional(b.Property)).Select(b => b.Box.Text);
        if (optionalValues.Any(v => !string.IsNullOrWhiteSpace(v) && !ShortcutKeyName.TryParse(v, out _)))
        {
            ShowInvalid(Tr.DialogSettingsInvalidShortcuts); return;
        }
        Settings.InitialViewMode = ViewModeCombo.SelectedIndex switch
        {
            1 => InitialViewMode.FitWidth,
            2 => InitialViewMode.FitHeight,
            3 => InitialViewMode.ClickZoomLevel,
            4 => InitialViewMode.Percent100,
            5 => InitialViewMode.Percent200,
            _ => InitialViewMode.Fit
        };
        Settings.FitWidthAnchor = IndexToAnchor(FitWidthAnchorCombo.SelectedIndex, FitWidthAnchor.Centre);
        Settings.FitWidthAnchor2 = IndexToAnchor(FitWidthAnchor2Combo.SelectedIndex, FitWidthAnchor.BottomThird);
        Settings.MiddleClickAction = MiddleClickActionCombo.SelectedIndex is >= 0 && Enum.IsDefined((MiddleClickAction)MiddleClickActionCombo.SelectedIndex)
            ? (MiddleClickAction)MiddleClickActionCombo.SelectedIndex : MiddleClickAction.ActualSize;
        Settings.KeepZoomAcrossImages = KeepZoomAcrossImagesCheck.IsChecked == true;
        Settings.ImageSortMode = SortModeCombo.SelectedItem is ComboBoxItem { Tag: string sortTag } && Enum.TryParse<ImageSortMode>(sortTag, out var chosenSort) ? chosenSort : ImageSortMode.Name;
        Settings.ScalingQuality = ScalingQualityCombo.SelectedIndex == 1 ? ScalingQuality.Linear : ScalingQuality.HighQuality;
        Settings.DecoderBackend = DecoderBackendCombo.SelectedIndex switch { 1 => DecoderBackend.WicDirect, 2 => DecoderBackend.TurboJpeg, _ => DecoderBackend.Wpf };
        Settings.LoadingMode = LoadingModeCombo.SelectedIndex switch { 1 => LoadingMode.Preview, 2 => LoadingMode.Original, _ => LoadingMode.Fast };
        Settings.InstanceMode = InstanceModeCombo.SelectedIndex == 1 ? InstanceMode.PerFolder : InstanceMode.SingleWindow;
        Settings.CompareHashEnabled = CompareHashCheck.IsChecked == true;
        Settings.CompareSizeEnabled = CompareSizeCheck.IsChecked == true;
        Settings.RawSupportEnabled = RawSupportEnabledCheck.IsChecked == true;
        Settings.WebpHeicSupportEnabled = WebpHeicSupportEnabledCheck.IsChecked == true;
        Settings.RawFullDecode = RawFullDecodeCombo.SelectedIndex == 1 ? RawFullDecode.OnZoom : RawFullDecode.Never;
        Settings.RawPairMode = RawPairModeCombo.SelectedIndex switch
        {
            1 => RawPairMode.PreferJpeg,
            2 => RawPairMode.PreferRaw,
            _ => RawPairMode.Separate
        };
        Settings.LoggingEnabled = LoggingCheck.IsChecked == true;
        Settings.AllowPermanentDeleteWithoutRecycleBin = AllowPermanentDeleteCheck.IsChecked == true;
        Settings.ConfirmBeforeDelete = ConfirmBeforeDeleteCheck.IsChecked == true;
        Settings.ExternalEditorPath = ExternalEditorPathText.Text.Trim();
        Settings.JournalDurability = JournalSafeRadio.IsChecked == true ? JournalDurability.PowerLossSafe : JournalDurability.Fast;
        Settings.ImageCacheRamPercent = SelectedRamCachePercent;
        Settings.ShowInfoOverlay = ShowInfoOverlayCheck.IsChecked == true;
        Settings.ShowFileInfo = ShowFileInfoCheck.IsChecked == true;
        Settings.ShowFolderInfo = ShowFolderInfoCheck.IsChecked == true;
        Settings.MouseWheelAction = MouseWheelActionCombo.SelectedIndex == 1 ? MouseWheelAction.Navigate : MouseWheelAction.Zoom;
        Settings.ClickToZoomEnabled = ClickToZoomCheck.IsChecked == true;
        Settings.KineticPanEnabled = KineticPanCheck.IsChecked == true;
        Settings.KineticGlideSmoothing = KineticGlideSmoothingCombo.SelectedIndex == 1 ? KineticGlideSmoothing.Predict : KineticGlideSmoothing.Off;
        Settings.ArrowKeyNavigatesAtZoomEdge = ArrowKeyNavigatesAtZoomEdgeCheck.IsChecked == true;
        Settings.ClickZoomKeyTogglesFit = ClickZoomKeyTogglesFitCheck.IsChecked == true;
        Settings.ShowFolderMenuItems = ShowFolderMenuItemsCheck.IsChecked == true;
        Settings.ShowZoomMenuItems = ShowZoomMenuItemsCheck.IsChecked == true;
        Settings.ShowRecycleMenuItem = ShowRecycleMenuItemCheck.IsChecked == true;
        Settings.KeyboardZoomAnchor = KeyboardZoomAnchorCombo.SelectedIndex == 1 ? KeyboardZoomAnchor.ViewportCentre : KeyboardZoomAnchor.Pointer;
        Settings.MoveCopyReuseLastFolder = MoveCopyReuseLastFolderCheck.IsChecked == true;
        Settings.ShowExifInfo = ShowExifInfoCheck.IsChecked == true;
        Settings.ExifInfoFields =
            (ExifFieldFileNameCheck.IsChecked == true ? ExifInfoFields.FileName : ExifInfoFields.None) |
            (ExifFieldDateTakenCheck.IsChecked == true ? ExifInfoFields.DateTaken : ExifInfoFields.None) |
            (ExifFieldModifiedDateCheck.IsChecked == true ? ExifInfoFields.ModifiedDate : ExifInfoFields.None) |
            (ExifFieldDimensionsCheck.IsChecked == true ? ExifInfoFields.Dimensions : ExifInfoFields.None) |
            (ExifFieldCameraCheck.IsChecked == true ? ExifInfoFields.Camera : ExifInfoFields.None) |
            (ExifFieldLensCheck.IsChecked == true ? ExifInfoFields.Lens : ExifInfoFields.None) |
            (ExifFieldIsoCheck.IsChecked == true ? ExifInfoFields.Iso : ExifInfoFields.None) |
            (ExifFieldFocalLengthCheck.IsChecked == true ? ExifInfoFields.FocalLength : ExifInfoFields.None) |
            (ExifFieldApertureCheck.IsChecked == true ? ExifInfoFields.Aperture : ExifInfoFields.None) |
            (ExifFieldShutterSpeedCheck.IsChecked == true ? ExifInfoFields.ShutterSpeed : ExifInfoFields.None);
        Settings.TitleBarFields =
            (TitleBarFieldFolderNameCheck.IsChecked == true ? TitleBarFields.FolderName : TitleBarFields.None) |
            (TitleBarFieldFolderPathCheck.IsChecked == true ? TitleBarFields.FolderPath : TitleBarFields.None) |
            (TitleBarFieldIndexCountCheck.IsChecked == true ? TitleBarFields.IndexCount : TitleBarFields.None) |
            (TitleBarFieldFileNameCheck.IsChecked == true ? TitleBarFields.FileName : TitleBarFields.None) |
            (TitleBarFieldFileSizeCheck.IsChecked == true ? TitleBarFields.FileSize : TitleBarFields.None) |
            (TitleBarFieldDimensionsCheck.IsChecked == true ? TitleBarFields.Dimensions : TitleBarFields.None) |
            (TitleBarFieldModifiedDateCheck.IsChecked == true ? TitleBarFields.ModifiedDate : TitleBarFields.None) |
            (TitleBarFieldDateTakenCheck.IsChecked == true ? TitleBarFields.DateTaken : TitleBarFields.None) |
            (TitleBarFieldCameraCheck.IsChecked == true ? TitleBarFields.Camera : TitleBarFields.None) |
            (TitleBarFieldLensCheck.IsChecked == true ? TitleBarFields.Lens : TitleBarFields.None) |
            (TitleBarFieldIsoCheck.IsChecked == true ? TitleBarFields.Iso : TitleBarFields.None) |
            (TitleBarFieldFocalLengthCheck.IsChecked == true ? TitleBarFields.FocalLength : TitleBarFields.None) |
            (TitleBarFieldApertureCheck.IsChecked == true ? TitleBarFields.Aperture : TitleBarFields.None) |
            (TitleBarFieldShutterSpeedCheck.IsChecked == true ? TitleBarFields.ShutterSpeed : TitleBarFields.None);
        if (!int.TryParse(ClickZoomPercentBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var clickZoomPercent)
            || clickZoomPercent < AppSettings.MinClickZoomPercent || clickZoomPercent > AppSettings.MaxClickZoomPercent)
        {
            ShowInvalid(Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent));
            return;
        }
        Settings.ClickZoomPercent = clickZoomPercent;
        if (!int.TryParse(ArrowPanStepBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var arrowPanStep)
            || arrowPanStep < AppSettings.MinArrowPanStepPercent || arrowPanStep > AppSettings.MaxArrowPanStepPercent)
        {
            ShowInvalid(Tr.DialogSettingsInvalidArrowPanStepPercent(AppSettings.MinArrowPanStepPercent, AppSettings.MaxArrowPanStepPercent));
            ArrowPanStepBox.Focus();
            return;
        }
        Settings.ArrowPanStepPercent = arrowPanStep;
        if (!int.TryParse(KeyboardZoomStepPercentBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var keyboardZoomStepPercent)
            || keyboardZoomStepPercent < AppSettings.MinKeyboardZoomStepPercent || keyboardZoomStepPercent > AppSettings.MaxKeyboardZoomStepPercent)
        {
            ShowInvalid(Tr.DialogSettingsInvalidKeyboardZoomStepPercent(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent));
            KeyboardZoomStepPercentBox.Focus();
            return;
        }
        Settings.KeyboardZoomStepPercent = keyboardZoomStepPercent;
        Settings.ImageTransition = ImageTransitionCombo.SelectedIndex == 1 ? ImageTransition.Fade : ImageTransition.None;
        if (!int.TryParse(ImageTransitionMsBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var imageTransitionMs)
            || imageTransitionMs < AppSettings.MinImageTransitionMs || imageTransitionMs > AppSettings.MaxImageTransitionMs)
        {
            ShowInvalid(Tr.DialogSettingsInvalidImageTransitionMs(AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs));
            ImageTransitionMsBox.Focus();
            return;
        }
        Settings.ImageTransitionMs = imageTransitionMs;
        Settings.TouchpadSwipeEnabled = TouchpadSwipeCheck.IsChecked == true;
        if (!int.TryParse(TouchpadSwipeDistanceBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var touchpadSwipeDistance)
            || touchpadSwipeDistance < AppSettings.MinTouchpadSwipeDistancePerImage || touchpadSwipeDistance > AppSettings.MaxTouchpadSwipeDistancePerImage)
        {
            ShowInvalid(Tr.DialogSettingsInvalidTouchpadSwipeDistance(AppSettings.MinTouchpadSwipeDistancePerImage, AppSettings.MaxTouchpadSwipeDistancePerImage));
            TouchpadSwipeDistanceBox.Focus();
            return;
        }
        Settings.TouchpadSwipeDistancePerImage = touchpadSwipeDistance;
        // feat/preload-window-setting: same pattern as ClickZoomPercent above -- an unparsable or out-of-range
        // value keeps the dialog open and saves nothing (a hand-edited config.json is still clamped by
        // SettingsNormalizer, that path is unaffected).
        if (!int.TryParse(PreloadForwardBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var preloadForward)
            || preloadForward < PerformanceOptions.MinPreloadForwardCount || preloadForward > PerformanceOptions.MaxPreloadCount)
        {
            ShowInvalid(Tr.DialogSettingsInvalidPreloadForward(PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount));
            PreloadForwardBox.Focus();
            return;
        }
        if (!int.TryParse(PreloadBackwardBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var preloadBackward)
            || preloadBackward < PerformanceOptions.MinPreloadBackwardCount || preloadBackward > PerformanceOptions.MaxPreloadCount)
        {
            ShowInvalid(Tr.DialogSettingsInvalidPreloadBackward(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount));
            PreloadBackwardBox.Focus();
            return;
        }
        Settings.PreloadForwardCount = preloadForward;
        Settings.PreloadBackwardCount = preloadBackward;
        if (!int.TryParse(ToolbarAutoHideDelayBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var toolbarAutoHideDelayMs)
            || toolbarAutoHideDelayMs < AppSettings.MinToolbarAutoHideDelayMs || toolbarAutoHideDelayMs > AppSettings.MaxToolbarAutoHideDelayMs)
        {
            ShowInvalid(Tr.DialogSettingsInvalidToolbarAutoHideDelay(AppSettings.MinToolbarAutoHideDelayMs, AppSettings.MaxToolbarAutoHideDelayMs));
            return;
        }
        if (!int.TryParse(InfoOverlayAutoHideDelayBox.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var infoOverlayAutoHideDelayMs)
            || infoOverlayAutoHideDelayMs < AppSettings.MinInfoOverlayAutoHideDelayMs || infoOverlayAutoHideDelayMs > AppSettings.MaxInfoOverlayAutoHideDelayMs)
        {
            ShowInvalid(Tr.DialogSettingsInvalidInfoOverlayAutoHideDelay(AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs));
            InfoOverlayAutoHideDelayBox.Focus();
            return;
        }
        if (!double.TryParse(InfoOverlayFontSizeBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var infoOverlayFontSize)
            || !double.IsFinite(infoOverlayFontSize) || infoOverlayFontSize < AppSettings.MinInfoOverlayFontSize || infoOverlayFontSize > AppSettings.MaxInfoOverlayFontSize)
        {
            ShowInvalid(Tr.DialogSettingsInvalidInfoOverlayFontSize(AppSettings.MinInfoOverlayFontSize, AppSettings.MaxInfoOverlayFontSize));
            return;
        }
        Settings.ToolbarAutoHide = ToolbarAutoHideCheck.IsChecked == true;
        Settings.ToolbarAutoHideDelayMs = toolbarAutoHideDelayMs;
        Settings.InfoOverlayAutoHide = InfoOverlayAutoHideCheck.IsChecked == true;
        Settings.InfoOverlayAutoHideDelayMs = infoOverlayAutoHideDelayMs;
        Settings.ToolbarOpacityPercent = Math.Clamp((int)Math.Round(ToolbarOpacitySlider.Value), AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
        Settings.InfoOverlayFontSize = infoOverlayFontSize;
        Settings.ShowZoomIndicator = ShowZoomIndicatorCheck.IsChecked == true;
        Settings.Shortcuts = CanonicalizeShortcuts(ReadShortcutsFromUi(), Settings.Shortcuts);
        try
        {
            Settings.Actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? [];
            if (Settings.Actions.Any(action => string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Shortcut) ||
                !ShortcutKeyName.TryParse(action.Shortcut, out _) ||
                !Enum.IsDefined(action.Operation)))
                throw new JsonException("An action has no name, an invalid shortcut or an invalid operation."); // never shown (caught below)
            if (Settings.Actions.GroupBy(action => ShortcutKeyCanonical.Canonicalize(action.Shortcut), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new JsonException("Two actions use the same shortcut."); // never shown (caught below)
        }
        catch { ShowInvalid(Tr.DialogSettingsInvalidActionsJson); return; }
        var shortcutError = _store is not null
            ? _store.ValidateShortcuts(Settings)
            : new SettingsValidator(new PhotoReview.App.Services.WpfKeyNameValidator()).ValidateShortcuts(Settings);
        if (shortcutError is not null) { ShowInvalid(shortcutError); return; }
        // R7-8: destinations typed in the raw actions JSON get the same check as the Action Profiles editor (CORE-03 / Q-R2).
        if (ActionProfilesWindow.FindDestinationProblem(Settings.Actions) is { } destinationProblem)
        {
            ShowInvalid(destinationProblem);
            return;
        }
        if (_localization is not null)
            Settings.UiLanguage = LanguageOptions.ToSetting(LanguageCombo.SelectedItem as LanguageOption, Settings.UiLanguage);
        try
        {
            // AR11a: the old fallback here was AppSettings.Save(Settings), a static Saver hook nothing ever assigned
            // (a silent no-op) -- removed along with the rest of the dead static persistence API. A window built
            // without a store (test-only: see the AppSettings-taking constructor) has nothing to persist to; Settings
            // (the in-memory clone above) already reflects everything Save just validated and assigned.
            _store?.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Keep the dialog open so the edits are not lost; the user sees why nothing was saved.
            AppLog.Error("Settings save failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogSettingsSaveFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AppLog.Info("Settings saved");
        // Q-L8: the new language applies live (UI thread, ADR 0005); every {loc:Tr} text follows the switch.
        if (_localization is not null && !string.Equals(_localization.RequestedLanguage, Settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
            _localization.Switch(Settings.UiLanguage);
        DialogResult = true;
    }

    private static void ShortcutText_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // An active IME (Vietnamese) reports ImeProcessed; the real key is in ImeProcessedKey.
        if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
        // Tab/Escape/modifiers and anything else that can never be a shortcut must not be swallowed (focus trap, Esc cancel).
        if (ShortcutKeyName.IsReserved(key)) return;
        textBox.Text = ShortcutKeyCanonical.Canonicalize(key.ToString());
        textBox.SelectAll();
        e.Handled = true;
    }

    private void EditActions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? ReviewAction.Defaults();
            var editor = new ActionProfilesWindow(actions) { Owner = this };
            if (editor.ShowDialog() == true) ActionsText.Text = JsonSerializer.Serialize(editor.Actions, JsonOptions);
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
            InitialDirectory = AppContext.BaseDirectory, FileName = "PhotoReview-settings.json",
        };
        if (dialog.ShowDialog(this) != true) return;
        ExportSettingsTo(dialog.FileName);
    }

    /// <summary>The body of Export once the user picked a file (a test seam: the file dialog cannot be driven in a test).</summary>
    internal void ExportSettingsTo(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInvalid(Tr.DialogExportSettingsFailed(ex.Message));
        }
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Tr.DialogImportSettingsTitle, Filter = Tr.DialogFileFilterJson, InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != true) return;
        ImportSettingsFrom(dialog.FileName);
    }

    /// <summary>The body of Import once the user picked a file (a test seam: the file dialog cannot be driven in a test).</summary>
    internal void ImportSettingsFrom(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInvalid(Tr.DialogImportSettingsFailed(ex.Message));
            return;
        }
        if (!TryApplyImportedJson(json, out var message)) ShowInvalid(message!);
        else if (message is not null) ShowImportRepairs(message);
    }

    /// <summary>
    /// Fills the window from imported text through the same pipeline as start-up (<see cref="SettingsStore.ParseText"/>: per-property
    /// salvage, migration of an unversioned file, normalization, optional-shortcut conflicts). True on success with
    /// <paramref name="message"/> = what was reset (null when nothing was); false with the error text when the file is unusable,
    /// in which case the window keeps the settings it had.
    /// </summary>
    internal bool TryApplyImportedJson(string json, out string? message)
    {
        var previous = Settings;
        try
        {
            var parsed = SettingsStore.ParseText(json);
            if (parsed.IsJsonNull) throw new JsonException("The file does not contain a settings object.");
            var repairs = parsed.Repairs;
            // A file that parses but holds values the clone or the field loader cannot take (null members) must be reported
            // like any unreadable file, and the window keeps the settings it had.
            Settings = AppSettings.Clone(parsed.Settings);
            LoadFields();
            message = repairs.Count > 0 ? SettingsLoadRepairText.Build(repairs, forImport: true) : null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Settings import: the file could not be applied", ex);
            Settings = previous;
            try { LoadFields(); }
            catch (Exception reloadEx) when (reloadEx is not OutOfMemoryException) { AppLog.Error("Settings import: could not restore the previous fields", reloadEx); }
            message = Tr.DialogImportSettingsFailed(ex.Message);
            return false;
        }
    }

    /// <summary>Tells the user which imported values were reset; the import itself succeeded (the window is filled, nothing saved yet).</summary>
    private void ShowImportRepairs(string message)
    {
        if (ImportRepairsNotice is { } notice) notice(message);
        else System.Windows.MessageBox.Show(this, message, Tr.DialogImportSettingsTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
