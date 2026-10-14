using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PhotoReview.App.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Updates;

namespace PhotoReview.App.Presenters;

/// <summary>The result of an Explorer-launching action: the arguments to start, or the reason it cannot be done.</summary>
internal sealed record SettingsExplorerRequest(string? Arguments, string? ErrorMessage);

/// <summary>What the Explorer-integration status line and its two buttons should show.</summary>
/// <param name="AddLabel">The Add button caption, or null to leave it as it is (the unavailable/error rows never change it).</param>
internal sealed record ShellIntegrationStatus(string Text, bool AddEnabled, bool RemoveEnabled, string? AddLabel);

/// <summary>What the update-check line should show once a check finished.</summary>
/// <param name="DownloadUrl">The validated download page, or null when no page can be offered.</param>
internal sealed record UpdateCheckDisplay(string StatusText, string? DownloadUrl)
{
    public bool ShowOpenPage => DownloadUrl is not null;
}

/// <summary>The outcome of the "reload translations" button.</summary>
internal sealed record TranslationReloadResult(string Message, bool HasProblems);

/// <summary>The outcome of an import: a refusal, or the (optional) "these values were reset" notice.</summary>
internal sealed record SettingsImportResult(string? Error, string? RepairsNotice);

/// <summary>A row of the right-click menu page: the tick box a view has to create.</summary>
internal sealed record ContextMenuRow(ContextMenuItemId Id, string Label, string AutomationName, bool IsChild, bool Locked, string? LockedTooltip);

/// <summary>One page of the dialog (left navigation).</summary>
internal sealed record SettingsPageInfo(string Key, string TitleKey);

/// <summary>
/// WP-35 G0: everything the Settings dialog does that is not drawing -- loading the fields, validating and saving, the
/// defaults, import/export, the live shortcut-conflict preview, the RAM-cache slider arithmetic, the manual update check,
/// the Explorer integration and the language picker. No WPF types: a view (today <c>SettingsWindow</c>, later a native
/// Win32 dialog) copies its controls to a <see cref="SettingsFormState"/>, calls this class and shows the result.
/// </summary>
internal sealed class SettingsPresenter
{
    private readonly SettingsStore? _store;
    private readonly LocalizationService? _localization;
    private readonly IUpdateChecker? _updateChecker;
    private readonly long _physicalMemoryBytes;
    private CancellationTokenSource? _updateCts;
    private string? _updateUrl;
    private SettingsValidator? _fallbackValidator;

    /// <summary>Session-only "remember the last opened page" (DR02): resets on process restart, not persisted to disk.</summary>
    private static string s_lastPageKey = "General";

    /// <summary>The seven pages, in navigation order; the key is what the view tags its navigation items with.</summary>
    public static IReadOnlyList<SettingsPageInfo> Pages { get; } =
    [
        new("General", TrKeys.SettingsNavGeneral),
        new("Display", TrKeys.SettingsNavDisplay),
        new("Mouse", TrKeys.SettingsNavMouse),
        new("Performance", TrKeys.SettingsNavPerformance),
        new("Shortcuts", TrKeys.SettingsNavShortcuts),
        new("Files", TrKeys.SettingsNavFiles),
        new("Diagnostics", TrKeys.SettingsNavDiagnostics),
    ];

    public SettingsPresenter(AppSettings current, SettingsStore? store = null, LocalizationService? localization = null,
        IUpdateChecker? updateChecker = null, long? physicalMemoryBytes = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _store = store;
        _localization = localization;
        _updateChecker = updateChecker;
        _physicalMemoryBytes = physicalMemoryBytes ?? RamBudgetPolicy.GetPhysicalMemoryBytes();
        // Structural fix: clone through the same JSON contract used to persist config.json, so every AppSettings
        // property survives round-tripping through the dialog -- including ones it has no control for.
        Settings = AppSettings.Clone(current);
    }

    /// <summary>The working copy the dialog edits (the caller reads it after a successful Save).</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>The registry seam (test seam: a fake replaces it).</summary>
    public IShellIntegration ShellIntegration { get; set; } = new PhotoReview.Platform.Windows.WindowsShellIntegration();

    /// <summary>The executable the Explorer command should launch; null/dotnet host = unavailable (test seam).</summary>
    public string? ExecutablePath { get; set; } = Environment.ProcessPath;

    /// <summary>True when a <see cref="LocalizationService"/> was supplied (otherwise the language group is hidden).</summary>
    public bool HasLocalization => _localization is not null;

    // ---- Pages ----

    /// <summary>The page the dialog opens on: the last one used in this process, else General.</summary>
    public static string InitialPageKey => Pages.Any(page => page.Key == s_lastPageKey) ? s_lastPageKey : "General";

    /// <summary>Remembers the page the user switched to (session only).</summary>
    public static void RememberPage(string key) => s_lastPageKey = key;

    /// <summary>The page key the view should restore as "remembered" after a one-off jump (the external-editor shortcut).</summary>
    public static string RememberedPageKey => s_lastPageKey;

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the page index after <paramref name="current"/>, wrapping.</summary>
    public static int NextPageIndex(int current, int count, bool forward) => ((current + (forward ? 1 : -1)) % count + count) % count;

    /// <summary>The localized page heading for a page key (empty for an unknown key).</summary>
    public static string PageTitle(string key) =>
        Pages.FirstOrDefault(page => page.Key == key) is { } page ? Localizer.Current.Get(page.TitleKey) : string.Empty;

    // ---- Load ----

    /// <summary>Settings -> form values (the old LoadFields minus the control wiring).</summary>
    public SettingsFormState LoadFields()
    {
        var state = new SettingsFormState();
        foreach (var binding in SettingsFieldMap.Bindings) binding.Load(Settings, state);
        var (min, max) = RamCacheRange(state.PreloadForwardText, state.PreloadBackwardText);
        state.RamCachePercent = Math.Clamp(Settings.ImageCacheRamPercent, min, max);
        return state;
    }

    // ---- Save ----

    /// <summary>
    /// Validates <paramref name="state"/>, writes it into <see cref="Settings"/>, persists it through the store and applies the
    /// language switch. A refusal keeps everything as the user typed it; nothing is persisted unless the result is Saved.
    /// </summary>
    public SettingsSaveResult TrySave(SettingsFormState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        AppLog.Info("Settings save requested");
        var required = SettingsFieldMap.Shortcuts.Where(s => !ShortcutMappings.IsOptional(s.Property))
            .Select(s => SettingsFieldMap.ShortcutText(state, s.Property)).ToArray();
        if (required.Any(v => !ShortcutKeyName.TryParse(v, out _)) || required.Select(ShortcutKeyCanonical.Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Length)
            return Invalid(Tr.DialogSettingsInvalidShortcuts);
        // Optional shortcuts (ShortcutMappings.OptionalNames) may be empty (= feature disabled); non-empty ones still
        // have to be a real key name. Cross-duplicate checking against everything else happens in the validator below.
        var optional = SettingsFieldMap.Shortcuts.Where(s => ShortcutMappings.IsOptional(s.Property)).Select(s => SettingsFieldMap.ShortcutText(state, s.Property));
        if (optional.Any(v => !string.IsNullOrWhiteSpace(v) && !ShortcutKeyName.TryParse(v, out _)))
            return Invalid(Tr.DialogSettingsInvalidShortcuts);

        foreach (var binding in SettingsFieldMap.Bindings)
        {
            if (binding.Apply(state, Settings) is { } error) return Invalid(error.Message, error.Focus);
        }

        var shortcutError = ValidateShortcuts(Settings);
        if (shortcutError is not null) return Invalid(shortcutError);
        // R7-8: destinations typed in the raw actions JSON get the same check as the Action Profiles editor (CORE-03 / Q-R2).
        if (ActionProfilesPresenter.FindDestinationProblem(Settings.Actions) is { } destinationProblem) return Invalid(destinationProblem);
        if (_localization is not null)
            Settings.UiLanguage = LanguageOptions.ToSetting(state.SelectedLanguage, Settings.UiLanguage);
        try
        {
            // A dialog built without a store (test-only) has nothing to persist to; Settings already reflects what was validated.
            _store?.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Keep the dialog open so the edits are not lost; the user sees why nothing was saved.
            AppLog.Error("Settings save failed", ex);
            return new SettingsSaveResult(SettingsSaveOutcome.SaveFailed, ex.Message);
        }
        AppLog.Info("Settings saved");
        // Q-L8: the new language applies live (UI thread, ADR 0005); every localized text follows the switch.
        if (_localization is not null && !string.Equals(_localization.RequestedLanguage, Settings.UiLanguage, StringComparison.OrdinalIgnoreCase))
            _localization.Switch(Settings.UiLanguage);
        return SettingsSaveResult.Saved;
    }

    private static SettingsSaveResult Invalid(string message, SettingsFocusField focus = SettingsFocusField.None) =>
        new(SettingsSaveOutcome.Invalid, message, focus);

    private string? ValidateShortcuts(AppSettings settings) =>
        _store is not null
            ? _store.ValidateShortcuts(settings)
            : (_fallbackValidator ??= new SettingsValidator(new WpfKeyNameValidator())).ValidateShortcuts(settings);

    // ---- Live shortcut preview ----

    private string? _cachedProbeActionsJson;
    private List<ReviewAction> _cachedProbeActions = [];

    /// <summary>
    /// The live "two actions use the same key" message for the text currently typed (the same check Save runs), or null.
    /// The actions JSON is re-parsed only when its text changed: this runs on every keystroke in any shortcut box.
    /// </summary>
    public string? DuplicateShortcutWarning(IReadOnlyDictionary<string, string> shortcutTexts, string actionsJson)
    {
        var state = new SettingsFormState();
        foreach (var (property, text) in shortcutTexts) state.ShortcutTexts[property] = text;
        var probe = new AppSettings { Shortcuts = SettingsFieldMap.ReadShortcuts(state), Actions = ParsedActionsForProbe(actionsJson) };
        return ValidateShortcuts(probe);
    }

    private List<ReviewAction> ParsedActionsForProbe(string text)
    {
        if (_cachedProbeActionsJson == text) return _cachedProbeActions;
        try { _cachedProbeActions = ActionListJson.Deserialize(text); }
        catch (JsonException) { _cachedProbeActions = []; } // invalid JSON while typing: Save's own check reports that separately
        _cachedProbeActionsJson = text;
        return _cachedProbeActions;
    }

    /// <summary>The zoom card's read-only list of the zoom shortcuts as currently typed on the Shortcuts page.</summary>
    public static string ZoomShortcutSummary(IReadOnlyDictionary<string, string> shortcutTexts)
    {
        string Key(string property) =>
            !shortcutTexts.TryGetValue(property, out var text) || string.IsNullOrWhiteSpace(text) ? Tr.SettingsZoomShortcutsNone : text.Trim();
        return string.Join(Environment.NewLine,
            $"{Tr.SettingsShortcutClickZoom}: {Key(nameof(ShortcutMappings.ClickZoom))}",
            $"{Tr.SettingsShortcutToggleFit}: {Key(nameof(ShortcutMappings.ToggleFit))}",
            $"{Tr.SettingsShortcutZoomActualSize}: {Key(nameof(ShortcutMappings.ZoomActualSize))}",
            $"{Tr.SettingsShortcutZoomIn}: {Key(nameof(ShortcutMappings.ZoomIn))}",
            $"{Tr.SettingsShortcutZoomOut}: {Key(nameof(ShortcutMappings.ZoomOut))}",
            $"{Tr.SettingsShortcutFitWidth}: {Key(nameof(ShortcutMappings.FitWidth))}",
            $"{Tr.SettingsShortcutFitWidth2}: {Key(nameof(ShortcutMappings.FitWidth2))}",
            $"{Tr.SettingsShortcutFitHeight}: {Key(nameof(ShortcutMappings.FitHeight))}");
    }

    /// <summary>
    /// Shortcut capture box: the canonical text a pressed key should put into the box, or null when the key must not be
    /// swallowed (Tab/Escape/modifiers and anything else that can never be a shortcut keep focus movement and Esc-cancel).
    /// <paramref name="keyName"/> is the platform's name for the key (the real key, after an IME's ImeProcessedKey).
    /// </summary>
    public static string? CaptureShortcut(PhotoReview.App.Input.KeyId key, string keyName) =>
        ShortcutKeyName.IsReserved(key) ? null : ShortcutKeyCanonical.Canonicalize(keyName);

    // ---- RAM cache slider ----

    /// <summary>Slider range for this device: [window-dependent minimum, hard maximum]. A box that is unparsable/out of range uses its saved value.</summary>
    public (int Min, int Max) RamCacheRange(string? forwardText, string? backwardText)
    {
        var forward = ParseOrFallback(forwardText, Settings.PreloadForwardCount, PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount);
        var backward = ParseOrFallback(backwardText, Settings.PreloadBackwardCount, PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);
        PreloadWindow window = new(forward, backward);
        return (RamBudgetPolicy.MinimumCachePercent(_physicalMemoryBytes, window), PerformanceOptions.MaxImageCacheRamPercent);
    }

    private static int ParseOrFallback(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
            return Math.Clamp(fallback, min, max);
        return value;
    }

    public string RamCacheHintText(int min, int max) => Tr.SettingsRamCacheHint(FormatGb(_physicalMemoryBytes), min, max);

    public string RamCacheValueText(int percent) =>
        Tr.SettingsRamCacheValue(percent, FormatGb(RamBudgetPolicy.BytesForPercent(percent, _physicalMemoryBytes)));

    /// <summary>UI text in the user's locale (GiB shown as "GB", as Windows does); "?" when RAM is unknown.</summary>
    private static string FormatGb(long bytes) =>
        bytes > 0 ? (bytes / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.CurrentCulture) : "?";

    public static string PreloadWindowHint() =>
        Tr.SettingsPreloadWindowHint(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount);

    public static string ToolbarOpacityHint() => Tr.SettingsToolbarOpacityHint(AppSettings.MinToolbarOpacityPercent);

    public static string KeyboardZoomStepPercentHint() =>
        Tr.SettingsKeyboardZoomStepPercentHint(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent);

    public static string ToolbarOpacityValueText(double sliderValue) =>
        string.Create(CultureInfo.CurrentCulture, $"{(int)Math.Round(sliderValue)} %");

    /// <summary>Q-FMT-WEBP-HEIC: the localized codec status line: WebP available/missing; HEIC available, missing, or missing only the HEVC decoder.</summary>
    public static string FormatWebpHeicCodecStatus(PhotoReview.Imaging.Decoding.Wic.WicCodecSupport codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        var webp = codecs.WebP ? Tr.SettingsWebpHeicSupportEnabledCodecAvailable : Tr.SettingsWebpHeicSupportEnabledWebpMissing;
        var heif = codecs.Heif ? Tr.SettingsWebpHeicSupportEnabledCodecAvailable
            : codecs.HeifContainer ? Tr.SettingsWebpHeicSupportEnabledHevcMissing
            : Tr.SettingsWebpHeicSupportEnabledHeifMissing;
        return Tr.SettingsWebpHeicSupportEnabledStatus(webp, heif);
    }

    /// <summary>False when the TurboJPEG option must be greyed out (the native library is not registered).</summary>
    public static bool IsTurboJpegAvailable(IImageDecoderFactory? decoderFactory) =>
        decoderFactory is null || decoderFactory.IsRegistered(DecoderBackend.TurboJpeg);

    // ---- Defaults ----

    /// <summary>
    /// The "Defaults" button: resets the listed settings to their built-in values on the working copy (everything not listed,
    /// such as the language and the actions, keeps its loaded value) and returns the form to show.
    /// </summary>
    public SettingsFormState ApplyDefaults()
    {
        var s = Settings;
        s.LoggingEnabled = false;
        s.JournalDurability = JournalDurability.Fast;
        s.AllowPermanentDeleteWithoutRecycleBin = false;
        s.ConfirmBeforeDelete = false;
        s.ShowZoomIndicator = false;
        s.ExternalEditorPath = string.Empty;
        s.ExternalEditors = [];
        s.DefaultExternalEditorId = string.Empty;
        s.ExternalEditorSendsRawOriginal = new AppSettings().ExternalEditorSendsRawOriginal;
        s.ImageCacheCapacityBytes = PerformanceOptions.ImageCacheCapacityBytes;
        s.ImageCacheRamPercent = PerformanceOptions.ImageCacheRamPercent;
        s.MemoryReserveBytes = PerformanceOptions.MemoryReserveBytes;
        s.PreloadWorkerCount = PerformanceOptions.PreloadWorkerCount;
        s.PreloadMemoryLoadLimit = PerformanceOptions.PreloadMemoryLoadLimit;
        s.PreloadForwardCount = PerformanceOptions.PreloadForwardCount;
        s.PreloadBackwardCount = PerformanceOptions.PreloadBackwardCount;
        s.PreviewDiskCacheCapacityBytes = PerformanceOptions.PreviewDiskCacheCapacityBytes;
        s.UseSourceBytesCache = PerformanceOptions.UseSourceBytesCache;
        s.SourceBytesCapacityBytes = PerformanceOptions.SourceBytesCapacityBytes;
        s.InitialViewMode = InitialViewMode.Fit; s.LoadingMode = LoadingMode.Preview; s.ImageSortMode = new AppSettings().ImageSortMode; s.ScalingQuality = ScalingQuality.HighQuality; s.DecoderBackend = new AppSettings().DecoderBackend; s.CompareHashEnabled = true; s.CompareSizeEnabled = true; s.Shortcuts = ShortcutMappings.Default();
        s.FitWidthAnchor = FitWidthAnchor.Centre; s.KeepZoomAcrossImages = false;
        s.FitWidthAnchor2 = new AppSettings().FitWidthAnchor2; s.MiddleClickAction = new AppSettings().MiddleClickAction;
        s.InstanceMode = InstanceMode.SingleWindow;
        s.ShowInfoOverlay = true; s.ShowFileInfo = true; s.ShowFolderInfo = false;
        s.MouseWheelAction = MouseWheelAction.Zoom; s.ClickToZoomEnabled = false; s.ClickZoomPercent = AppSettings.DefaultClickZoomPercent; s.KineticPanEnabled = new AppSettings().KineticPanEnabled; s.KineticGlideSmoothing = new AppSettings().KineticGlideSmoothing; s.ArrowKeyNavigatesAtZoomEdge = new AppSettings().ArrowKeyNavigatesAtZoomEdge; s.ClickZoomKeyTogglesFit = new AppSettings().ClickZoomKeyTogglesFit; s.ArrowPanStepPercent = AppSettings.DefaultArrowPanStepPercent; s.KeyboardZoomStepPercent = AppSettings.DefaultKeyboardZoomStepPercent; s.KeyboardZoomAnchor = new AppSettings().KeyboardZoomAnchor;
        s.SetZoomAlsoSetsClickLevel = new AppSettings().SetZoomAlsoSetsClickLevel; s.ShowFolderMenuItems = new AppSettings().ShowFolderMenuItems; s.ShowZoomMenuItems = new AppSettings().ShowZoomMenuItems; s.ShowRecycleMenuItem = new AppSettings().ShowRecycleMenuItem; s.HiddenContextMenuItems = null; // null = derive from the legacy defaults = the default hidden set
        s.MoveCopyReuseLastFolder = false;
        s.ImageTransition = new AppSettings().ImageTransition; s.ImageTransitionMs = AppSettings.DefaultImageTransitionMs;
        s.TouchpadSwipeEnabled = new AppSettings().TouchpadSwipeEnabled; s.TouchpadSwipeDistancePerImage = AppSettings.DefaultTouchpadSwipeDistancePerImage;
        s.ShowExifInfo = new AppSettings().ShowExifInfo; s.ExifInfoFields = ExifInfoFields.Default;
        s.ToolbarAutoHide = new AppSettings().ToolbarAutoHide; s.ToolbarAutoHideDelayMs = AppSettings.DefaultToolbarAutoHideDelayMs; s.InfoOverlayAutoHide = new AppSettings().InfoOverlayAutoHide; s.InfoOverlayAutoHideDelayMs = AppSettings.DefaultInfoOverlayAutoHideDelayMs; s.ToolbarOpacityPercent = AppSettings.DefaultToolbarOpacityPercent;
        s.InfoOverlayFontSize = AppSettings.DefaultInfoOverlayFontSize;
        s.TitleBarFields = TitleBarFields.Default;
        s.RawSupportEnabled = new AppSettings().RawSupportEnabled;
        s.WebpHeicSupportEnabled = new AppSettings().WebpHeicSupportEnabled;
        s.RawFullDecode = new AppSettings().RawFullDecode;
        s.RawPairMode = new AppSettings().RawPairMode;
        return LoadFields();
    }

    // ---- Import / export of the whole settings file (Q-R50) ----

    /// <summary>Writes the working copy through the config.json contract; the localized reason on failure, null on success.</summary>
    public string? ExportSettingsTo(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(path, json);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Tr.DialogExportSettingsFailed(ex.Message);
        }
    }

    /// <summary>Reads <paramref name="path"/> and applies it; <paramref name="refreshView"/> reloads the controls after the working copy was replaced.</summary>
    public SettingsImportResult ImportSettingsFrom(string path, Action refreshView)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsImportResult(Tr.DialogImportSettingsFailed(ex.Message), null);
        }
        return TryApplyImportedJson(json, refreshView, out var message)
            ? new SettingsImportResult(null, message)
            : new SettingsImportResult(message, null);
    }

    /// <summary>
    /// Replaces the working copy with imported text through the same pipeline as start-up (<see cref="SettingsStore.ParseText"/>:
    /// per-property salvage, migration of an unversioned file, normalization, optional-shortcut conflicts). True on success
    /// with <paramref name="message"/> = what was reset (null when nothing was); false with the error text when the file is
    /// unusable, in which case the working copy is what it was.
    /// </summary>
    public bool TryApplyImportedJson(string json, Action refreshView, out string? message)
    {
        ArgumentNullException.ThrowIfNull(refreshView);
        var previous = Settings;
        try
        {
            var parsed = SettingsStore.ParseText(json);
            if (parsed.IsJsonNull) throw new JsonException("The file does not contain a settings object.");
            var repairs = parsed.Repairs;
            // A file that parses but holds values the clone or the field loader cannot take (null members) must be reported
            // like any unreadable file, and the dialog keeps the settings it had.
            Settings = AppSettings.Clone(parsed.Settings);
            refreshView();
            message = repairs.Count > 0 ? SettingsLoadRepairText.Build(repairs, forImport: true) : null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("Settings import: the file could not be applied", ex);
            Settings = previous;
            try { refreshView(); }
            catch (Exception reloadEx) when (reloadEx is not OutOfMemoryException) { AppLog.Error("Settings import: could not restore the previous fields", reloadEx); }
            message = Tr.DialogImportSettingsFailed(ex.Message);
            return false;
        }
    }

    /// <summary>The initial folder of the import/export file dialogs.</summary>
    public static string SettingsFileDialogDirectory => AppContext.BaseDirectory;

    // ---- Actions editor hand-off ----

    /// <summary>The actions to open the profile editor with: the typed JSON, or the defaults for a literal null. Throws when the JSON is unusable.</summary>
    public static List<ReviewAction> ActionsForEditor(string actionsJson) => ActionListJson.DeserializeOrNull(actionsJson) ?? ReviewAction.Defaults();

    /// <summary>The JSON text the Settings box shows for an edited list.</summary>
    public static string ActionsToText(IEnumerable<ReviewAction> actions) => ActionListJson.Serialize(actions);

    // ---- Context menu page ----

    /// <summary>One tick-box row per right-click menu item, in menu order (built from the single item list).</summary>
    public static IReadOnlyList<ContextMenuRow> ContextMenuRows() =>
        ContextMenuItems.Items.Select(item =>
        {
            var label = ContextMenuItemLabel(item.Id);
            return new ContextMenuRow(item.Id, label, Tr.SettingsContextMenuItemAutomationName(label), item.Parent is not null, item.Locked,
                item.Locked ? Tr.SettingsContextMenuSettingsLockedTooltip : null);
        }).ToList();

    public static string ContextMenuItemLabel(ContextMenuItemId id) => id switch
    {
        ContextMenuItemId.Undo => Tr.SettingsContextMenuItemUndo,
        ContextMenuItemId.MoveToRecycleBin => Tr.SettingsContextMenuItemMoveToRecycleBin,
        ContextMenuItemId.Fit => Tr.SettingsContextMenuItemFit,
        ContextMenuItemId.ZoomToLevel => Tr.SettingsContextMenuItemZoomToLevel,
        ContextMenuItemId.ZoomSubmenu => Tr.SettingsContextMenuItemZoomSubmenu,
        ContextMenuItemId.OpenFolder => Tr.SettingsContextMenuItemOpenFolder,
        ContextMenuItemId.NextFolder => Tr.SettingsContextMenuItemNextFolder,
        ContextMenuItemId.PreviousFolder => Tr.SettingsContextMenuItemPreviousFolder,
        ContextMenuItemId.ExternalEditor => Tr.SettingsContextMenuItemExternalEditor,
        ContextMenuItemId.CopyFileName => Tr.SettingsContextMenuItemCopyFileName,
        ContextMenuItemId.CopyFullPath => Tr.SettingsContextMenuItemCopyFullPath,
        ContextMenuItemId.Settings => Tr.SettingsContextMenuItemSettings,
        ContextMenuItemId.ZoomFitWidth => Tr.SettingsContextMenuItemZoomFitWidth,
        ContextMenuItemId.ZoomFitWidth2 => Tr.SettingsContextMenuItemZoomFitWidth2,
        ContextMenuItemId.ZoomFitHeight => Tr.SettingsContextMenuItemZoomFitHeight,
        ContextMenuItemId.ZoomPresets => Tr.SettingsContextMenuItemZoomPresets,
        ContextMenuItemId.ZoomLevelOptions => Tr.SettingsContextMenuItemZoomLevelOptions,
        ContextMenuItemId.Refresh => Tr.SettingsContextMenuItemRefresh,
        _ => id.ToString(),
    };

    // ---- Language picker (I18N L08) ----

    /// <summary>
    /// The picker entries and the index to select (the entry for <paramref name="currentCode"/>, else the saved language), or
    /// null when no localization service exists (the view hides the group).
    /// </summary>
    public (IReadOnlyList<LanguageOption> Options, int SelectedIndex)? LoadLanguages(string? currentCode)
    {
        if (_localization is null) return null;
        var selected = currentCode ?? Settings.UiLanguage;
        var options = LanguageOptions.Build(_localization.DiscoverLanguages());
        return (options, LanguageOptions.IndexOf(options, selected));
    }

    /// <summary>The "open languages folder" action: creates the folder and returns the Explorer arguments, or the failure reason.</summary>
    public SettingsExplorerRequest PrepareLanguagesFolder()
    {
        if (_localization is null) return new(null, null);
        try { Directory.CreateDirectory(_localization.UserLanguagesDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, ex.Message);
        }
        return new($"\"{_localization.UserLanguagesDir}\"", null);
    }

    /// <summary>The "reload translations" action; null when there is no localization service.</summary>
    public TranslationReloadResult? ReloadTranslations()
    {
        if (_localization is null) return null;
        var problems = _localization.Reload();
        AppLog.Info("Translations reloaded");
        return new TranslationReloadResult(TranslationProblems.Message(problems), TranslationProblems.HasProblems(problems));
    }

    /// <summary>The "export translation template" action: Explorer arguments (select the file) or the reason it failed (Error).</summary>
    public (SettingsExplorerRequest? Request, string? Error) ExportTranslation(LanguageOption? selected)
    {
        if (_localization is null) return (null, null);
        var language = LanguageOptions.ToSetting(selected, Settings.UiLanguage);
        string path;
        try
        {
            path = _localization.ExportTodo(language);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, ex.Message);
        }
        AppLog.Info("Translation export written");
        return (new SettingsExplorerRequest($"/select,\"{path}\"", null), null);
    }

    /// <summary>The "show log file" action.</summary>
    public static SettingsExplorerRequest PrepareLogLocation()
    {
        var directory = Path.GetDirectoryName(AppLog.FilePath)!;
        try { Directory.CreateDirectory(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, ex.Message);
        }
        return new($"/select,\"{AppLog.FilePath}\"", null);
    }

    /// <summary>Starts Explorer with <paramref name="arguments"/>; the failure reason, or null on success.</summary>
    public static string? TryStartExplorer(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    // ---- Explorer integration: "Browse with PhotoReview" on folders (HKCU, no administrator rights; applies immediately) ----

    /// <summary>The current registration state and which buttons make sense for it.</summary>
    public ShellIntegrationStatus RefreshShellStatus()
    {
        var exe = ExecutablePath;
        if (!ShellMenuCommand.IsValidExecutablePath(exe) || string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            return new(Tr.SettingsShellStatusUnavailable, false, false, null);
        ShellMenuState state;
        try { state = ShellIntegration.GetState(exe!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(Tr.SettingsShellStatusError(ex.Message), true, false, null);
        }
        var text = state switch
        {
            ShellMenuState.Registered => Tr.SettingsShellStatusRegistered,
            ShellMenuState.RegisteredElsewhere => Tr.SettingsShellStatusElsewhere,
            _ => Tr.SettingsShellStatusNotRegistered,
        };
        return new(text, state != ShellMenuState.Registered, state != ShellMenuState.NotRegistered,
            state == ShellMenuState.RegisteredElsewhere ? Tr.SettingsShellUpdate : Tr.SettingsShellAdd);
    }

    /// <summary>Adds or removes the Explorer entry and returns the status to show afterwards (an error replaces the text).</summary>
    public ShellIntegrationStatus ChangeShellRegistration(bool register)
    {
        try
        {
            if (register) ShellIntegration.Register(ExecutablePath!);
            else ShellIntegration.Unregister();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            AppLog.Error("Explorer integration change failed", ex);
            return RefreshShellStatus() with { Text = Tr.SettingsShellStatusError(ex.Message) };
        }
        return RefreshShellStatus();
    }

    // ---- Manual update check: the only network use in the app, only on click (no auto-check/download/install) ----

    /// <summary>
    /// Runs one check. Null when it was cancelled (the dialog closed): the view must not touch its controls then.
    /// <paramref name="version"/> is the running app version as shown in the dialog footer.
    /// </summary>
    public async Task<UpdateCheckDisplay?> CheckForUpdateAsync(string? version)
    {
        _updateUrl = null;
        using var cts = new CancellationTokenSource();
        _updateCts = cts;
        UpdateCheckResult result;
        try
        {
            var checker = _updateChecker ?? new UpdateChecker();
            result = await checker.CheckAsync(version, cts.Token);
        }
        catch (OperationCanceledException) { return null; } // dialog closed while checking
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = UpdateCheckResult.Failed(UpdateFailure.BadResponse);
        }
        finally
        {
            _updateCts = null;
        }
        if (cts.IsCancellationRequested) return null;

        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                return new UpdateCheckDisplay(Tr.UpdateStatusUpToDate(result.LatestVersion ?? string.Empty), null);
            case UpdateCheckStatus.UpdateAvailable:
                _updateUrl = UpdateUrlPolicy.Validate(result.DownloadUrl);
                var current = AppVersion.TryParse(version, out var parsed) ? parsed.ToString() : version ?? string.Empty;
                return new UpdateCheckDisplay(Tr.UpdateStatusAvailable(result.LatestVersion ?? string.Empty, current), _updateUrl);
            default:
                return new UpdateCheckDisplay(result.Failure switch
                {
                    UpdateFailure.Offline => Tr.UpdateStatusFailedOffline,
                    UpdateFailure.Timeout => Tr.UpdateStatusFailedTimeout,
                    UpdateFailure.RateLimited => Tr.UpdateStatusFailedRateLimited,
                    UpdateFailure.InvalidVersion => Tr.UpdateStatusFailedInvalidVersion,
                    _ => Tr.UpdateStatusFailedBadResponse,
                }, null);
        }
    }

    /// <summary>Opens the download page found by the last check; a status text to show when it cannot be opened, else null.</summary>
    public string? OpenUpdatePage(Action<string> openUrl)
    {
        ArgumentNullException.ThrowIfNull(openUrl);
        var url = UpdateUrlPolicy.Validate(_updateUrl);
        if (url is null) return null;
        try { openUrl(url); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Tr.UpdateOpenPageFailed;
        }
        return null;
    }

    /// <summary>The validated download page of the last check (test seam: lets a test store a foreign URL to prove the open click re-validates).</summary>
    internal string? UpdateUrl
    {
        get => _updateUrl;
        set => _updateUrl = value;
    }

    /// <summary>Cancels a running check (dialog closed).</summary>
    public void CancelUpdateCheck()
    {
        try { _updateCts?.Cancel(); }
        catch (ObjectDisposedException) { } // the check may already be finished and disposed
    }
}
