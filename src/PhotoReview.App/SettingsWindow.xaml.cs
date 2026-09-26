using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.App.Services;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Updates;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Preload;

using PhotoReview.Core.Settings;

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
    public AppSettings Settings { get; }

    /// <summary>Test seam: receives the invalid-destination warning of Save instead of a MessageBox.</summary>
    internal Action<string>? InvalidSettingsWarning { get; set; }

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

        var allShortcutBoxes = new[]
        {
            NextText, PreviousText, FirstImageText, LastImageText, NextFolderText, PreviousFolderText,
            ZoomInText, ZoomOutText, ZoomActualSizeText, ToggleFitText, FullscreenText, ToggleInfoOverlayText,
            SkipText, UndoText, CompareText, MoveToFolderText, CopyToFolderText, RecycleText, ClickZoomText,
        };
        foreach (var textBox in allShortcutBoxes)
        {
            textBox.PreviewKeyDown += ShortcutText_PreviewKeyDown;
            textBox.TextChanged += Shortcut_TextChanged;
        }

        LoadFields();
        ApplyDecoderAvailability(decoderFactory);
        LoadLanguages();
    }

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
        PageTitleText.Text = Localizer.Current.Get(page.TitleKey);
        page.Scroll.ScrollToHome();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        try { Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { ShowOpenFailed(ex); }
    }

    private void ShowOpenFailed(Exception ex) =>
        System.Windows.MessageBox.Show(this, ex.Message, Tr.DialogSettingsOpenFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- Manual update check: the only network use in the app, only on click (no auto-check/download/install) ----

    /// <summary>Test seam: opens the download page; default is the system browser via ShellExecute.</summary>
    internal Action<string> OpenUrl { get; set; } = static url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
        _cancelUpdateCheck?.Invoke();
        base.OnClosed(e);
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (NavList.SelectedItem is ListBoxItem { Tag: string key } && _pages is not null && _pages.TryGetValue(key, out var page))
            page.Scroll.ScrollToHome();
        FocusManager.SetFocusedElement(this, null);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
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
        ActionsText.Text = JsonSerializer.Serialize(Settings.Actions, JsonOptions);
        ViewModeCombo.SelectedIndex = Settings.InitialViewMode switch { InitialViewMode.Percent100 => 1, InitialViewMode.Percent200 => 2, InitialViewMode.Percent400 => 3, _ => 0 };
        LoadingModeCombo.SelectedIndex = Settings.LoadingMode switch { LoadingMode.Preview => 1, LoadingMode.Original => 2, _ => 0 };
        SortModeCombo.SelectedIndex = Settings.ImageSortMode switch { ImageSortMode.SizeAscending => 1, ImageSortMode.SizeDescending => 2, _ => 0 };
        ScalingQualityCombo.SelectedIndex = Settings.ScalingQuality == ScalingQuality.Linear ? 1 : 0;
        DecoderBackendCombo.SelectedIndex = Settings.DecoderBackend switch { DecoderBackend.WicDirect => 1, DecoderBackend.TurboJpeg => 2, _ => 0 };
        InstanceModeCombo.SelectedIndex = Settings.InstanceMode == InstanceMode.PerFolder ? 1 : 0;
        CompareHashCheck.IsChecked = Settings.CompareHashEnabled;
        CompareSizeCheck.IsChecked = Settings.CompareSizeEnabled;
        LoggingCheck.IsChecked = Settings.LoggingEnabled;
        AllowPermanentDeleteCheck.IsChecked = Settings.AllowPermanentDeleteWithoutRecycleBin;
        JournalSafeRadio.IsChecked = Settings.JournalDurability == JournalDurability.PowerLossSafe;
        JournalFastRadio.IsChecked = !JournalSafeRadio.IsChecked;
        ShowInfoOverlayCheck.IsChecked = Settings.ShowInfoOverlay;
        ShowFileInfoCheck.IsChecked = Settings.ShowFileInfo;
        ShowFolderInfoCheck.IsChecked = Settings.ShowFolderInfo;
        InfoOverlayFontSizeBox.Text = Settings.InfoOverlayFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ToolbarAutoHideCheck.IsChecked = Settings.ToolbarAutoHide;
        ToolbarAutoHideDelayBox.Text = Settings.ToolbarAutoHideDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MouseWheelActionCombo.SelectedIndex = Settings.MouseWheelAction == MouseWheelAction.Navigate ? 1 : 0;
        ClickToZoomCheck.IsChecked = Settings.ClickToZoomEnabled;
        ClickZoomPercentBox.Text = Settings.ClickZoomPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PreloadForwardBox.Text = Settings.PreloadForwardCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PreloadBackwardBox.Text = Settings.PreloadBackwardCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        KineticPanCheck.IsChecked = Settings.KineticPanEnabled;
        KineticGlideSmoothingCombo.SelectedIndex = Settings.KineticGlideSmoothing == KineticGlideSmoothing.Predict ? 1 : 0;
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
        UpdateClickZoomEnabled();
        UpdateShowInfoSubOptionsEnabled();
        UpdateExifFieldsEnabled();
        UpdateToolbarAutoHideEnabled();
        LoadRamCache();
        UpdateDuplicateWarning();
    }

    private void ClickToZoomCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateClickZoomEnabled();

    private void UpdateClickZoomEnabled() => ClickZoomPercentBox.IsEnabled = ClickToZoomCheck.IsChecked == true;

    private void ToolbarAutoHideCheck_CheckedChanged(object sender, RoutedEventArgs e) => UpdateToolbarAutoHideEnabled();

    private void UpdateToolbarAutoHideEnabled() => ToolbarAutoHideDelayBox.IsEnabled = ToolbarAutoHideCheck.IsChecked == true;

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

    private static void ClearShortcut(System.Windows.Controls.TextBox textBox) => textBox.Text = string.Empty;

    // ---- Live duplicate-shortcut warning (reuses SettingsValidator, the same check Save runs). ----

    private void Shortcut_TextChanged(object sender, TextChangedEventArgs e) => UpdateDuplicateWarning();

    private void UpdateDuplicateWarning()
    {
        if (ShortcutDuplicateWarningText is null) return; // can fire while InitializeComponent is still building the tree
        var probe = BuildProbeSettings();
        var message = _store is not null ? _store.ValidateShortcuts(probe) : new SettingsValidator(new WpfKeyNameValidator()).ValidateShortcuts(probe);
        ShortcutDuplicateWarningText.Text = message ?? string.Empty;
        ShortcutDuplicateWarningText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A throwaway settings snapshot from the current text boxes, used only to preview validation live.</summary>
    private AppSettings BuildProbeSettings()
    {
        var probe = new AppSettings
        {
            Shortcuts = new ShortcutMappings
            {
                Next = NextText.Text, Previous = PreviousText.Text, FirstImage = FirstImageText.Text, LastImage = LastImageText.Text,
                NextFolder = NextFolderText.Text, PreviousFolder = PreviousFolderText.Text,
                ZoomIn = ZoomInText.Text, ZoomOut = ZoomOutText.Text, ZoomActualSize = ZoomActualSizeText.Text, ToggleFit = ToggleFitText.Text,
                Fullscreen = FullscreenText.Text, ToggleInfoOverlay = ToggleInfoOverlayText.Text,
                Skip = SkipText.Text, Undo = UndoText.Text, Compare = CompareText.Text,
                MoveToFolder = MoveToFolderText.Text, CopyToFolder = CopyToFolderText.Text, SendToRecycleBin = RecycleText.Text,
                ClickZoom = ClickZoomText.Text,
            },
        };
        try { probe.Actions = JsonSerializer.Deserialize<List<ReviewAction>>(ActionsText.Text) ?? []; }
        catch (JsonException) { probe.Actions = []; } // invalid JSON while typing: Save's own check reports that separately
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
        Settings.InitialViewMode = InitialViewMode.Fit; Settings.LoadingMode = LoadingMode.Preview; Settings.ImageSortMode = ImageSortMode.Name; Settings.ScalingQuality = ScalingQuality.HighQuality; Settings.DecoderBackend = new AppSettings().DecoderBackend; Settings.CompareHashEnabled = true; Settings.CompareSizeEnabled = true; Settings.Shortcuts = ShortcutMappings.Default();
        Settings.InstanceMode = InstanceMode.SingleWindow;
        Settings.ShowInfoOverlay = true; Settings.ShowFileInfo = true; Settings.ShowFolderInfo = false;
        Settings.MouseWheelAction = MouseWheelAction.Zoom; Settings.ClickToZoomEnabled = false; Settings.ClickZoomPercent = AppSettings.DefaultClickZoomPercent; Settings.KineticPanEnabled = true; Settings.KineticGlideSmoothing = new AppSettings().KineticGlideSmoothing;
        Settings.MoveCopyReuseLastFolder = false;
        Settings.ShowExifInfo = new AppSettings().ShowExifInfo; Settings.ExifInfoFields = ExifInfoFields.Default;
        Settings.ToolbarAutoHide = new AppSettings().ToolbarAutoHide; Settings.ToolbarAutoHideDelayMs = AppSettings.DefaultToolbarAutoHideDelayMs;
        Settings.InfoOverlayFontSize = AppSettings.DefaultInfoOverlayFontSize;
        Settings.TitleBarFields = TitleBarFields.Default;
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
        var values = new[] { NextText.Text, PreviousText.Text, RecycleText.Text, CompareText.Text, NextFolderText.Text, PreviousFolderText.Text, FirstImageText.Text, ZoomInText.Text, ZoomOutText.Text, ToggleFitText.Text, SkipText.Text, UndoText.Text, FullscreenText.Text };
        if (values.Any(v => !ShortcutKeyName.TryParse(v, out _)) || values.Select(ShortcutKeyCanonical.Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            ShowInvalid(Tr.DialogSettingsInvalidShortcuts); return;
        }
        // Optional shortcuts (ShortcutMappings.OptionalNames) may be empty (= feature disabled); non-empty ones still
        // have to be a real key name. Cross-duplicate checking against everything else happens in SettingsValidator below.
        var optionalValues = new[] { LastImageText.Text, ZoomActualSizeText.Text, ToggleInfoOverlayText.Text, MoveToFolderText.Text, CopyToFolderText.Text, ClickZoomText.Text };
        if (optionalValues.Any(v => !string.IsNullOrWhiteSpace(v) && !ShortcutKeyName.TryParse(v, out _)))
        {
            ShowInvalid(Tr.DialogSettingsInvalidShortcuts); return;
        }
        Settings.InitialViewMode = ViewModeCombo.SelectedIndex switch { 1 => InitialViewMode.Percent100, 2 => InitialViewMode.Percent200, 3 => InitialViewMode.Percent400, _ => InitialViewMode.Fit };
        Settings.ImageSortMode = SortModeCombo.SelectedIndex switch { 1 => ImageSortMode.SizeAscending, 2 => ImageSortMode.SizeDescending, _ => ImageSortMode.Name };
        Settings.ScalingQuality = ScalingQualityCombo.SelectedIndex == 1 ? ScalingQuality.Linear : ScalingQuality.HighQuality;
        Settings.DecoderBackend = DecoderBackendCombo.SelectedIndex switch { 1 => DecoderBackend.WicDirect, 2 => DecoderBackend.TurboJpeg, _ => DecoderBackend.Wpf };
        Settings.LoadingMode = LoadingModeCombo.SelectedIndex switch { 1 => LoadingMode.Preview, 2 => LoadingMode.Original, _ => LoadingMode.Fast };
        Settings.InstanceMode = InstanceModeCombo.SelectedIndex == 1 ? InstanceMode.PerFolder : InstanceMode.SingleWindow;
        Settings.CompareHashEnabled = CompareHashCheck.IsChecked == true;
        Settings.CompareSizeEnabled = CompareSizeCheck.IsChecked == true;
        Settings.LoggingEnabled = LoggingCheck.IsChecked == true;
        Settings.AllowPermanentDeleteWithoutRecycleBin = AllowPermanentDeleteCheck.IsChecked == true;
        Settings.JournalDurability = JournalSafeRadio.IsChecked == true ? JournalDurability.PowerLossSafe : JournalDurability.Fast;
        Settings.ImageCacheRamPercent = SelectedRamCachePercent;
        Settings.ShowInfoOverlay = ShowInfoOverlayCheck.IsChecked == true;
        Settings.ShowFileInfo = ShowFileInfoCheck.IsChecked == true;
        Settings.ShowFolderInfo = ShowFolderInfoCheck.IsChecked == true;
        Settings.MouseWheelAction = MouseWheelActionCombo.SelectedIndex == 1 ? MouseWheelAction.Navigate : MouseWheelAction.Zoom;
        Settings.ClickToZoomEnabled = ClickToZoomCheck.IsChecked == true;
        Settings.KineticPanEnabled = KineticPanCheck.IsChecked == true;
        Settings.KineticGlideSmoothing = KineticGlideSmoothingCombo.SelectedIndex == 1 ? KineticGlideSmoothing.Predict : KineticGlideSmoothing.Off;
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
        if (!double.TryParse(InfoOverlayFontSizeBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var infoOverlayFontSize)
            || infoOverlayFontSize < AppSettings.MinInfoOverlayFontSize || infoOverlayFontSize > AppSettings.MaxInfoOverlayFontSize)
        {
            ShowInvalid(Tr.DialogSettingsInvalidInfoOverlayFontSize(AppSettings.MinInfoOverlayFontSize, AppSettings.MaxInfoOverlayFontSize));
            return;
        }
        Settings.ToolbarAutoHide = ToolbarAutoHideCheck.IsChecked == true;
        Settings.ToolbarAutoHideDelayMs = toolbarAutoHideDelayMs;
        Settings.InfoOverlayFontSize = infoOverlayFontSize;
        Settings.Shortcuts.Next = ShortcutKeyCanonical.Canonicalize(NextText.Text); Settings.Shortcuts.Previous = ShortcutKeyCanonical.Canonicalize(PreviousText.Text);
        Settings.Shortcuts.SendToRecycleBin = ShortcutKeyCanonical.Canonicalize(RecycleText.Text);
        Settings.Shortcuts.Compare = ShortcutKeyCanonical.Canonicalize(CompareText.Text); Settings.Shortcuts.NextFolder = ShortcutKeyCanonical.Canonicalize(NextFolderText.Text); Settings.Shortcuts.PreviousFolder = ShortcutKeyCanonical.Canonicalize(PreviousFolderText.Text);
        Settings.Shortcuts.FirstImage = ShortcutKeyCanonical.Canonicalize(FirstImageText.Text); Settings.Shortcuts.ZoomIn = ShortcutKeyCanonical.Canonicalize(ZoomInText.Text); Settings.Shortcuts.ZoomOut = ShortcutKeyCanonical.Canonicalize(ZoomOutText.Text); Settings.Shortcuts.ToggleFit = ShortcutKeyCanonical.Canonicalize(ToggleFitText.Text); Settings.Shortcuts.Skip = ShortcutKeyCanonical.Canonicalize(SkipText.Text); Settings.Shortcuts.Undo = ShortcutKeyCanonical.Canonicalize(UndoText.Text); Settings.Shortcuts.Fullscreen = ShortcutKeyCanonical.Canonicalize(FullscreenText.Text);
        Settings.Shortcuts.LastImage = ShortcutKeyCanonical.Canonicalize(LastImageText.Text); Settings.Shortcuts.ZoomActualSize = ShortcutKeyCanonical.Canonicalize(ZoomActualSizeText.Text); Settings.Shortcuts.ToggleInfoOverlay = ShortcutKeyCanonical.Canonicalize(ToggleInfoOverlayText.Text);
        Settings.Shortcuts.MoveToFolder = ShortcutKeyCanonical.Canonicalize(MoveToFolderText.Text); Settings.Shortcuts.CopyToFolder = ShortcutKeyCanonical.Canonicalize(CopyToFolderText.Text);
        Settings.Shortcuts.ClickZoom = ShortcutKeyCanonical.Canonicalize(ClickZoomText.Text);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
}
