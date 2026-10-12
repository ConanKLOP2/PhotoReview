using PhotoReview.App.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Presenters;

/// <summary>
/// WP-35 G0: the value of every control of the Settings dialog, as plain data (no control type). A view copies its controls
/// into this object before Save and out of it after <see cref="SettingsPresenter.LoadFields"/>; all mapping to and from
/// <see cref="AppSettings"/> lives in <see cref="SettingsFieldMap"/> / <see cref="SettingsPresenter"/>, so a second view
/// (the native Win32 dialog of WP-35 phase 1) reuses it unchanged.
/// </summary>
/// <remarks>
/// Combo boxes are carried as the zero-based index of the item, in the fixed item order the dialog has always had (the
/// order is the contract: see the comments in <see cref="SettingsFieldMap"/>). Text boxes are carried as typed text, because
/// Save validates what the user typed, not a parsed number.
/// </remarks>
internal sealed class SettingsFormState
{
    /// <summary>Shortcut boxes: <see cref="ShortcutMappings"/> property name to the text exactly as typed.</summary>
    public Dictionary<string, string> ShortcutTexts { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw actions JSON text box.</summary>
    public string ActionsJson { get; set; } = string.Empty;

    // ---- View / zoom ----
    public int ViewModeIndex { get; set; }
    public int FitWidthAnchorIndex { get; set; }
    public int FitWidthAnchor2Index { get; set; }
    public int MiddleClickActionIndex { get; set; }
    public bool KeepZoomAcrossImages { get; set; }
    public int LoadingModeIndex { get; set; }
    /// <summary>The <see cref="ImageSortMode"/> member name of the selected sort item (null/unknown = Name).</summary>
    public string? SortModeTag { get; set; }
    public int ScalingQualityIndex { get; set; }
    public int DecoderBackendIndex { get; set; }
    public int InstanceModeIndex { get; set; }
    public bool CompareHashEnabled { get; set; }
    public bool CompareSizeEnabled { get; set; }
    public bool RawSupportEnabled { get; set; }
    public bool WebpHeicSupportEnabled { get; set; }
    public int RawFullDecodeIndex { get; set; }
    public int RawPairModeIndex { get; set; }

    // ---- Files / safety ----
    public bool LoggingEnabled { get; set; }
    public bool AllowPermanentDelete { get; set; }
    public bool ConfirmBeforeDelete { get; set; }
    public string ExternalEditorPath { get; set; } = string.Empty;
    public bool JournalSafe { get; set; }
    public bool MoveCopyReuseLastFolder { get; set; }

    // ---- Info overlay / toolbar ----
    public bool ShowInfoOverlay { get; set; }
    public bool ShowFileInfo { get; set; }
    public bool ShowFolderInfo { get; set; }
    public string InfoOverlayFontSizeText { get; set; } = string.Empty;
    public bool ShowZoomIndicator { get; set; }
    public bool ToolbarAutoHide { get; set; }
    public string ToolbarAutoHideDelayText { get; set; } = string.Empty;
    public bool InfoOverlayAutoHide { get; set; }
    public string InfoOverlayAutoHideDelayText { get; set; } = string.Empty;
    /// <summary>The opacity slider position (a double on the slider; rounded on Save).</summary>
    public double ToolbarOpacityValue { get; set; }
    public bool ShowExifInfo { get; set; }
    public ExifInfoFields ExifInfoFields { get; set; }
    public TitleBarFields TitleBarFields { get; set; }

    // ---- Mouse / keyboard ----
    public int MouseWheelActionIndex { get; set; }
    public bool ClickToZoomEnabled { get; set; }
    public string ClickZoomPercentText { get; set; } = string.Empty;
    public bool KineticPanEnabled { get; set; }
    public int KineticGlideSmoothingIndex { get; set; }
    public bool ArrowKeyNavigatesAtZoomEdge { get; set; }
    public bool ClickZoomKeyTogglesFit { get; set; }
    public string ArrowPanStepText { get; set; } = string.Empty;
    public string KeyboardZoomStepPercentText { get; set; } = string.Empty;
    public int KeyboardZoomAnchorIndex { get; set; }
    public int ImageTransitionIndex { get; set; }
    public string ImageTransitionMsText { get; set; } = string.Empty;
    public bool TouchpadSwipeEnabled { get; set; }
    public string TouchpadSwipeDistanceText { get; set; } = string.Empty;

    // ---- Performance ----
    public string PreloadForwardText { get; set; } = string.Empty;
    public string PreloadBackwardText { get; set; } = string.Empty;
    /// <summary>The RAM cache slider position (rounded on Save).</summary>
    public double RamCachePercent { get; set; }

    // ---- Right-click menu ----
    /// <summary>The tick boxes that are ticked (shown). Items not in this set are saved as hidden.</summary>
    public HashSet<ContextMenuItemId> ContextMenuShown { get; } = [];

    // ---- Language ----
    /// <summary>The selected picker entry; null when nothing is selected (or the picker is hidden).</summary>
    public LanguageOption? SelectedLanguage { get; set; }
}

/// <summary>Which input a refused Save should put the keyboard focus in (a view may ignore it).</summary>
internal enum SettingsFocusField
{
    None,
    ArrowPanStep,
    KeyboardZoomStep,
    ImageTransitionMs,
    TouchpadSwipeDistance,
    PreloadForward,
    PreloadBackward,
    InfoOverlayAutoHideDelay,
}

/// <summary>One refused input: the localized message and the control to focus.</summary>
internal sealed record SettingsFieldError(string Message, SettingsFocusField Focus = SettingsFocusField.None);

internal enum SettingsSaveOutcome
{
    /// <summary>Everything was valid, written to the store and (if changed) the language switched: close the dialog with OK.</summary>
    Saved,
    /// <summary>An input was refused (nothing was persisted): show <see cref="SettingsSaveResult.Message"/> as a warning, stay open.</summary>
    Invalid,
    /// <summary>The store could not write: show <see cref="SettingsSaveResult.Message"/> with the "save failed" title, stay open.</summary>
    SaveFailed,
}

internal sealed record SettingsSaveResult(SettingsSaveOutcome Outcome, string? Message = null, SettingsFocusField Focus = SettingsFocusField.None)
{
    public static SettingsSaveResult Saved { get; } = new(SettingsSaveOutcome.Saved);
}
