using System.Globalization;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Presenters;

/// <summary>One row of <see cref="SettingsFieldMap"/>: the <see cref="AppSettings"/> properties it owns and both directions.</summary>
/// <param name="Properties">The <see cref="AppSettings"/> property names this row reads and writes.</param>
/// <param name="Load">Settings to form: never fails.</param>
/// <param name="Apply">Form to settings; returns the refused input (and leaves the later rows unapplied) or null.</param>
internal sealed record SettingsFieldBinding(
    string[] Properties,
    Action<AppSettings, SettingsFormState> Load,
    Func<SettingsFormState, AppSettings, SettingsFieldError?> Apply);

/// <summary>
/// WP-35 G0: the single field table of the Settings dialog -- every <see cref="AppSettings"/> property that has a control, in
/// both directions. It replaces the three hand-written lists of the old code-behind (LoadFields, Save_Click and the probe
/// builder). The <b>order of <see cref="Bindings"/> is the order Save has always applied and validated in</b>: a refused input
/// stops the pass, and the rows before it have already been written to the settings clone (the dialog stays open, so this is
/// invisible to the user, but the order is kept so behaviour is identical).
/// </summary>
internal static class SettingsFieldMap
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // ---- Shortcut boxes: one accessor per ShortcutMappings property that has a control (MoveToFolder2 is owned by ReviewAction). ----

    internal sealed record ShortcutAccessor(string Property, Func<ShortcutMappings, string> Get, Action<ShortcutMappings, string> Set);

    /// <summary>Every shortcut property with a text box, in the order of the Shortcuts page.</summary>
    public static IReadOnlyList<ShortcutAccessor> Shortcuts { get; } =
    [
        new(nameof(ShortcutMappings.Next), m => m.Next, (m, v) => m.Next = v),
        new(nameof(ShortcutMappings.Previous), m => m.Previous, (m, v) => m.Previous = v),
        new(nameof(ShortcutMappings.FirstImage), m => m.FirstImage, (m, v) => m.FirstImage = v),
        new(nameof(ShortcutMappings.LastImage), m => m.LastImage, (m, v) => m.LastImage = v),
        new(nameof(ShortcutMappings.NextFolder), m => m.NextFolder, (m, v) => m.NextFolder = v),
        new(nameof(ShortcutMappings.PreviousFolder), m => m.PreviousFolder, (m, v) => m.PreviousFolder = v),
        new(nameof(ShortcutMappings.ZoomIn), m => m.ZoomIn, (m, v) => m.ZoomIn = v),
        new(nameof(ShortcutMappings.ZoomOut), m => m.ZoomOut, (m, v) => m.ZoomOut = v),
        new(nameof(ShortcutMappings.ZoomActualSize), m => m.ZoomActualSize, (m, v) => m.ZoomActualSize = v),
        new(nameof(ShortcutMappings.ToggleFit), m => m.ToggleFit, (m, v) => m.ToggleFit = v),
        new(nameof(ShortcutMappings.Fullscreen), m => m.Fullscreen, (m, v) => m.Fullscreen = v),
        new(nameof(ShortcutMappings.ToggleInfoOverlay), m => m.ToggleInfoOverlay, (m, v) => m.ToggleInfoOverlay = v),
        new(nameof(ShortcutMappings.Skip), m => m.Skip, (m, v) => m.Skip = v),
        new(nameof(ShortcutMappings.Undo), m => m.Undo, (m, v) => m.Undo = v),
        new(nameof(ShortcutMappings.Compare), m => m.Compare, (m, v) => m.Compare = v),
        new(nameof(ShortcutMappings.MoveToFolder), m => m.MoveToFolder, (m, v) => m.MoveToFolder = v),
        new(nameof(ShortcutMappings.CopyToFolder), m => m.CopyToFolder, (m, v) => m.CopyToFolder = v),
        new(nameof(ShortcutMappings.SendToRecycleBin), m => m.SendToRecycleBin, (m, v) => m.SendToRecycleBin = v),
        new(nameof(ShortcutMappings.ClickZoom), m => m.ClickZoom, (m, v) => m.ClickZoom = v),
        new(nameof(ShortcutMappings.FitWidth), m => m.FitWidth, (m, v) => m.FitWidth = v),
        new(nameof(ShortcutMappings.FitHeight), m => m.FitHeight, (m, v) => m.FitHeight = v),
        new(nameof(ShortcutMappings.ToggleKeepZoom), m => m.ToggleKeepZoom, (m, v) => m.ToggleKeepZoom = v),
        new(nameof(ShortcutMappings.OpenFolder), m => m.OpenFolder, (m, v) => m.OpenFolder = v),
        new(nameof(ShortcutMappings.CustomZoom), m => m.CustomZoom, (m, v) => m.CustomZoom = v),
        new(nameof(ShortcutMappings.ToggleCaptureMember), m => m.ToggleCaptureMember, (m, v) => m.ToggleCaptureMember = v),
        new(nameof(ShortcutMappings.FitWidth2), m => m.FitWidth2, (m, v) => m.FitWidth2 = v),
        new(nameof(ShortcutMappings.Refresh), m => m.Refresh, (m, v) => m.Refresh = v),
        new(nameof(ShortcutMappings.OpenInDefaultEditor), m => m.OpenInDefaultEditor, (m, v) => m.OpenInDefaultEditor = v),
    ];

    /// <summary>Settings -> text boxes.</summary>
    public static void LoadShortcuts(ShortcutMappings source, SettingsFormState state)
    {
        foreach (var accessor in Shortcuts) state.ShortcutTexts[accessor.Property] = accessor.Get(source);
    }

    /// <summary>The text boxes as typed (not canonicalized).</summary>
    public static ShortcutMappings ReadShortcuts(SettingsFormState state)
    {
        var result = new ShortcutMappings();
        foreach (var accessor in Shortcuts) accessor.Set(result, ShortcutText(state, accessor.Property));
        return result;
    }

    /// <summary>
    /// What Save persists: every value through <see cref="ShortcutKeyCanonical.Canonicalize"/>. MoveToFolder2 is a legacy alias
    /// with no control (owned by ReviewAction), so it is carried over from <paramref name="existing"/>.
    /// </summary>
    public static ShortcutMappings CanonicalizeShortcuts(ShortcutMappings raw, ShortcutMappings existing)
    {
        var result = new ShortcutMappings();
        foreach (var accessor in Shortcuts) accessor.Set(result, ShortcutKeyCanonical.Canonicalize(accessor.Get(raw)));
        result.MoveToFolder2 = existing.MoveToFolder2;
        return result;
    }

    internal static string ShortcutText(SettingsFormState state, string property) =>
        state.ShortcutTexts.TryGetValue(property, out var text) ? text : string.Empty;

    // ---- Index <-> value tables (the combo item order is fixed by the dialog; see the XAML comments) ----

    /// <summary>The Fit width combos list the FitWidthAnchor members in enum order: index == value.</summary>
    internal static int AnchorToIndex(FitWidthAnchor anchor, FitWidthAnchor fallback) => (int)(Enum.IsDefined(anchor) ? anchor : fallback);

    internal static FitWidthAnchor IndexToAnchor(int index, FitWidthAnchor fallback) =>
        index >= 0 && Enum.IsDefined((FitWidthAnchor)index) ? (FitWidthAnchor)index : fallback;

    /// <summary>Percent400 was removed from the combo; a stray Percent400 still lands on the 200 % item rather than on Fit.</summary>
    internal static int ViewModeToIndex(InitialViewMode mode) => mode switch
    {
        InitialViewMode.FitWidth => 1,
        InitialViewMode.FitHeight => 2,
        InitialViewMode.ClickZoomLevel => 3,
        InitialViewMode.Percent100 => 4,
        InitialViewMode.Percent200 or InitialViewMode.Percent400 => 5,
        _ => 0,
    };

    internal static InitialViewMode IndexToViewMode(int index) => index switch
    {
        1 => InitialViewMode.FitWidth,
        2 => InitialViewMode.FitHeight,
        3 => InitialViewMode.ClickZoomLevel,
        4 => InitialViewMode.Percent100,
        5 => InitialViewMode.Percent200,
        _ => InitialViewMode.Fit,
    };

    // ---- The table ----

    /// <summary>All rows, in the order Save applies them.</summary>
    public static IReadOnlyList<SettingsFieldBinding> Bindings { get; } =
    [
        Row(nameof(AppSettings.InitialViewMode),
            (s, f) => f.ViewModeIndex = ViewModeToIndex(s.InitialViewMode),
            (f, s) => s.InitialViewMode = IndexToViewMode(f.ViewModeIndex)),
        Row(nameof(AppSettings.FitWidthAnchor),
            (s, f) => f.FitWidthAnchorIndex = AnchorToIndex(s.FitWidthAnchor, FitWidthAnchor.Centre),
            (f, s) => s.FitWidthAnchor = IndexToAnchor(f.FitWidthAnchorIndex, FitWidthAnchor.Centre)),
        Row(nameof(AppSettings.FitWidthAnchor2),
            (s, f) => f.FitWidthAnchor2Index = AnchorToIndex(s.FitWidthAnchor2, FitWidthAnchor.BottomThird),
            (f, s) => s.FitWidthAnchor2 = IndexToAnchor(f.FitWidthAnchor2Index, FitWidthAnchor.BottomThird)),
        Row(nameof(AppSettings.MiddleClickAction),
            (s, f) => f.MiddleClickActionIndex = Enum.IsDefined(s.MiddleClickAction) ? (int)s.MiddleClickAction : (int)MiddleClickAction.ActualSize,
            (f, s) => s.MiddleClickAction = f.MiddleClickActionIndex is >= 0 && Enum.IsDefined((MiddleClickAction)f.MiddleClickActionIndex)
                ? (MiddleClickAction)f.MiddleClickActionIndex : MiddleClickAction.ActualSize),
        Row(nameof(AppSettings.KeepZoomAcrossImages),
            (s, f) => f.KeepZoomAcrossImages = s.KeepZoomAcrossImages,
            (f, s) => s.KeepZoomAcrossImages = f.KeepZoomAcrossImages),
        Row(nameof(AppSettings.ImageSortMode),
            (s, f) => f.SortModeTag = s.ImageSortMode.ToString(),
            (f, s) => s.ImageSortMode = f.SortModeTag is { } tag && Enum.TryParse<ImageSortMode>(tag, out var chosen) ? chosen : ImageSortMode.Name),
        Row(nameof(AppSettings.ScalingQuality),
            (s, f) => f.ScalingQualityIndex = s.ScalingQuality == ScalingQuality.Linear ? 1 : 0,
            (f, s) => s.ScalingQuality = f.ScalingQualityIndex == 1 ? ScalingQuality.Linear : ScalingQuality.HighQuality),
        Row(nameof(AppSettings.DecoderBackend),
            (s, f) => f.DecoderBackendIndex = s.DecoderBackend switch { DecoderBackend.WicDirect => 1, DecoderBackend.TurboJpeg => 2, _ => 0 },
            (f, s) => s.DecoderBackend = f.DecoderBackendIndex switch { 1 => DecoderBackend.WicDirect, 2 => DecoderBackend.TurboJpeg, _ => DecoderBackend.Wpf }),
        Row(nameof(AppSettings.LoadingMode),
            (s, f) => f.LoadingModeIndex = s.LoadingMode switch { LoadingMode.Preview => 1, LoadingMode.Original => 2, _ => 0 },
            (f, s) => s.LoadingMode = f.LoadingModeIndex switch { 1 => LoadingMode.Preview, 2 => LoadingMode.Original, _ => LoadingMode.Fast }),
        Row(nameof(AppSettings.InstanceMode),
            (s, f) => f.InstanceModeIndex = s.InstanceMode == InstanceMode.PerFolder ? 1 : 0,
            (f, s) => s.InstanceMode = f.InstanceModeIndex == 1 ? InstanceMode.PerFolder : InstanceMode.SingleWindow),
        Row(nameof(AppSettings.CompareHashEnabled),
            (s, f) => f.CompareHashEnabled = s.CompareHashEnabled,
            (f, s) => s.CompareHashEnabled = f.CompareHashEnabled),
        Row(nameof(AppSettings.CompareSizeEnabled),
            (s, f) => f.CompareSizeEnabled = s.CompareSizeEnabled,
            (f, s) => s.CompareSizeEnabled = f.CompareSizeEnabled),
        Row(nameof(AppSettings.RawSupportEnabled),
            (s, f) => f.RawSupportEnabled = s.RawSupportEnabled,
            (f, s) => s.RawSupportEnabled = f.RawSupportEnabled),
        Row(nameof(AppSettings.WebpHeicSupportEnabled),
            (s, f) => f.WebpHeicSupportEnabled = s.WebpHeicSupportEnabled,
            (f, s) => s.WebpHeicSupportEnabled = f.WebpHeicSupportEnabled),
        Row(nameof(AppSettings.RawFullDecode),
            (s, f) => f.RawFullDecodeIndex = s.RawFullDecode == RawFullDecode.OnZoom ? 1 : 0,
            (f, s) => s.RawFullDecode = f.RawFullDecodeIndex == 1 ? RawFullDecode.OnZoom : RawFullDecode.Never),
        Row(nameof(AppSettings.RawPairMode),
            (s, f) => f.RawPairModeIndex = s.RawPairMode switch { RawPairMode.PreferJpeg => 1, RawPairMode.PreferRaw => 2, _ => 0 },
            (f, s) => s.RawPairMode = f.RawPairModeIndex switch { 1 => RawPairMode.PreferJpeg, 2 => RawPairMode.PreferRaw, _ => RawPairMode.Separate }),
        Row(nameof(AppSettings.LoggingEnabled),
            (s, f) => f.LoggingEnabled = s.LoggingEnabled,
            (f, s) => s.LoggingEnabled = f.LoggingEnabled),
        Row(nameof(AppSettings.AllowPermanentDeleteWithoutRecycleBin),
            (s, f) => f.AllowPermanentDelete = s.AllowPermanentDeleteWithoutRecycleBin,
            (f, s) => s.AllowPermanentDeleteWithoutRecycleBin = f.AllowPermanentDelete),
        Row(nameof(AppSettings.ConfirmBeforeDelete),
            (s, f) => f.ConfirmBeforeDelete = s.ConfirmBeforeDelete,
            (f, s) => s.ConfirmBeforeDelete = f.ConfirmBeforeDelete),
        // FEAT-EXTERNAL-EDITORS: the list, its default and the RAW switch are written first; the single "default editor path" field
        // (the only field the frozen WPF window has) then lands on the default editor, so a path typed there is never lost.
        Row([nameof(AppSettings.ExternalEditors), nameof(AppSettings.DefaultExternalEditorId),
                nameof(AppSettings.ExternalEditorSendsRawOriginal), nameof(AppSettings.ExternalEditorPath)],
            (s, f) =>
            {
                f.ExternalEditors = [.. (s.ExternalEditors ?? []).Select(e => e.Clone())];
                f.DefaultExternalEditorId = s.DefaultExternalEditorId ?? string.Empty;
                f.ExternalEditorSendsRawOriginal = s.ExternalEditorSendsRawOriginal;
                // The legacy path stays the shown value when no list exists yet (settings built by code, not loaded from the store).
                f.ExternalEditorPath = ExternalEditors.Default(s)?.Path ?? s.ExternalEditorPath;
            },
            (f, s) =>
            {
                s.ExternalEditors = [.. f.ExternalEditors.Select(e => e.Clone())];
                s.DefaultExternalEditorId = f.DefaultExternalEditorId;
                s.ExternalEditorSendsRawOriginal = f.ExternalEditorSendsRawOriginal;
                s.ExternalEditorPath = ExternalEditors.Default(s)?.Path ?? string.Empty;
                ExternalEditors.ApplyLegacyPath(s, f.ExternalEditorPath);
                return null;
            }),
        Row(nameof(AppSettings.JournalDurability),
            (s, f) => f.JournalSafe = s.JournalDurability == JournalDurability.PowerLossSafe,
            (f, s) => s.JournalDurability = f.JournalSafe ? JournalDurability.PowerLossSafe : JournalDurability.Fast),
        Row(nameof(AppSettings.ImageCacheRamPercent),
            null, // needs the device RAM and the preload window: SettingsPresenter.LoadFields clamps it into the slider range
            (f, s) => s.ImageCacheRamPercent = (int)Math.Round(f.RamCachePercent)),
        Row(nameof(AppSettings.ShowInfoOverlay),
            (s, f) => f.ShowInfoOverlay = s.ShowInfoOverlay,
            (f, s) => s.ShowInfoOverlay = f.ShowInfoOverlay),
        Row(nameof(AppSettings.ShowFileInfo),
            (s, f) => f.ShowFileInfo = s.ShowFileInfo,
            (f, s) => s.ShowFileInfo = f.ShowFileInfo),
        Row(nameof(AppSettings.ShowFolderInfo),
            (s, f) => f.ShowFolderInfo = s.ShowFolderInfo,
            (f, s) => s.ShowFolderInfo = f.ShowFolderInfo),
        Row(nameof(AppSettings.MouseWheelAction),
            (s, f) => f.MouseWheelActionIndex = s.MouseWheelAction == MouseWheelAction.Navigate ? 1 : 0,
            (f, s) => s.MouseWheelAction = f.MouseWheelActionIndex == 1 ? MouseWheelAction.Navigate : MouseWheelAction.Zoom),
        Row(nameof(AppSettings.ClickToZoomEnabled),
            (s, f) => f.ClickToZoomEnabled = s.ClickToZoomEnabled,
            (f, s) => s.ClickToZoomEnabled = f.ClickToZoomEnabled),
        Row(nameof(AppSettings.KineticPanEnabled),
            (s, f) => f.KineticPanEnabled = s.KineticPanEnabled,
            (f, s) => s.KineticPanEnabled = f.KineticPanEnabled),
        Row(nameof(AppSettings.KineticGlideSmoothing),
            (s, f) => f.KineticGlideSmoothingIndex = s.KineticGlideSmoothing == KineticGlideSmoothing.Predict ? 1 : 0,
            (f, s) => s.KineticGlideSmoothing = f.KineticGlideSmoothingIndex == 1 ? KineticGlideSmoothing.Predict : KineticGlideSmoothing.Off),
        Row(nameof(AppSettings.ArrowKeyNavigatesAtZoomEdge),
            (s, f) => f.ArrowKeyNavigatesAtZoomEdge = s.ArrowKeyNavigatesAtZoomEdge,
            (f, s) => s.ArrowKeyNavigatesAtZoomEdge = f.ArrowKeyNavigatesAtZoomEdge),
        // The tick boxes are the complement of the hidden set; the legacy Show*MenuItems flags are derived by ContextMenuItems.
        Row([nameof(AppSettings.HiddenContextMenuItems), nameof(AppSettings.ShowFolderMenuItems), nameof(AppSettings.ShowZoomMenuItems), nameof(AppSettings.ShowRecycleMenuItem)],
            LoadContextMenu,
            (f, s) =>
            {
                ContextMenuItems.ApplyHidden(s, ContextMenuItems.Items.Select(item => item.Id).Where(id => !f.ContextMenuShown.Contains(id)));
                return null;
            }),
        Row(nameof(AppSettings.ClickZoomKeyTogglesFit),
            (s, f) => f.ClickZoomKeyTogglesFit = s.ClickZoomKeyTogglesFit,
            (f, s) => s.ClickZoomKeyTogglesFit = f.ClickZoomKeyTogglesFit),
        Row(nameof(AppSettings.KeyboardZoomAnchor),
            (s, f) => f.KeyboardZoomAnchorIndex = s.KeyboardZoomAnchor == KeyboardZoomAnchor.ViewportCentre ? 1 : 0,
            (f, s) => s.KeyboardZoomAnchor = f.KeyboardZoomAnchorIndex == 1 ? KeyboardZoomAnchor.ViewportCentre : KeyboardZoomAnchor.Pointer),
        Row(nameof(AppSettings.MoveCopyReuseLastFolder),
            (s, f) => f.MoveCopyReuseLastFolder = s.MoveCopyReuseLastFolder,
            (f, s) => s.MoveCopyReuseLastFolder = f.MoveCopyReuseLastFolder),
        Row(nameof(AppSettings.ShowExifInfo),
            (s, f) => f.ShowExifInfo = s.ShowExifInfo,
            (f, s) => s.ShowExifInfo = f.ShowExifInfo),
        Row(nameof(AppSettings.ExifInfoFields),
            (s, f) => f.ExifInfoFields = s.ExifInfoFields,
            (f, s) => s.ExifInfoFields = f.ExifInfoFields),
        Row(nameof(AppSettings.TitleBarFields),
            (s, f) => f.TitleBarFields = s.TitleBarFields,
            (f, s) => s.TitleBarFields = f.TitleBarFields),
        Row(nameof(AppSettings.ClickZoomPercent),
            (s, f) => f.ClickZoomPercentText = s.ClickZoomPercent.ToString(Invariant),
            (f, s) =>
            {
                if (!TryInt(f.ClickZoomPercentText, AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent, out var value))
                    return new(Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent));
                s.ClickZoomPercent = value;
                return null;
            }),
        Row(nameof(AppSettings.ArrowPanStepPercent),
            (s, f) => f.ArrowPanStepText = s.ArrowPanStepPercent.ToString(Invariant),
            (f, s) =>
            {
                if (!TryInt(f.ArrowPanStepText, AppSettings.MinArrowPanStepPercent, AppSettings.MaxArrowPanStepPercent, out var value))
                    return new(Tr.DialogSettingsInvalidArrowPanStepPercent(AppSettings.MinArrowPanStepPercent, AppSettings.MaxArrowPanStepPercent), SettingsFocusField.ArrowPanStep);
                s.ArrowPanStepPercent = value;
                return null;
            }),
        Row(nameof(AppSettings.KeyboardZoomStepPercent),
            (s, f) => f.KeyboardZoomStepPercentText = s.KeyboardZoomStepPercent.ToString(Invariant),
            (f, s) =>
            {
                if (!TryInt(f.KeyboardZoomStepPercentText, AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent, out var value))
                    return new(Tr.DialogSettingsInvalidKeyboardZoomStepPercent(AppSettings.MinKeyboardZoomStepPercent, AppSettings.MaxKeyboardZoomStepPercent), SettingsFocusField.KeyboardZoomStep);
                s.KeyboardZoomStepPercent = value;
                return null;
            }),
        // The transition kind is written before the duration is validated (as the old Save did).
        Row([nameof(AppSettings.ImageTransition), nameof(AppSettings.ImageTransitionMs)],
            (s, f) =>
            {
                f.ImageTransitionIndex = s.ImageTransition == ImageTransition.Fade ? 1 : 0;
                f.ImageTransitionMsText = s.ImageTransitionMs.ToString(Invariant);
            },
            (f, s) =>
            {
                s.ImageTransition = f.ImageTransitionIndex == 1 ? ImageTransition.Fade : ImageTransition.None;
                if (!TryInt(f.ImageTransitionMsText, AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs, out var value))
                    return new(Tr.DialogSettingsInvalidImageTransitionMs(AppSettings.MinImageTransitionMs, AppSettings.MaxImageTransitionMs), SettingsFocusField.ImageTransitionMs);
                s.ImageTransitionMs = value;
                return null;
            }),
        Row([nameof(AppSettings.TouchpadSwipeEnabled), nameof(AppSettings.TouchpadSwipeDistancePerImage)],
            (s, f) =>
            {
                f.TouchpadSwipeEnabled = s.TouchpadSwipeEnabled;
                f.TouchpadSwipeDistanceText = s.TouchpadSwipeDistancePerImage.ToString(Invariant);
            },
            (f, s) =>
            {
                s.TouchpadSwipeEnabled = f.TouchpadSwipeEnabled;
                if (!TryInt(f.TouchpadSwipeDistanceText, AppSettings.MinTouchpadSwipeDistancePerImage, AppSettings.MaxTouchpadSwipeDistancePerImage, out var value))
                    return new(Tr.DialogSettingsInvalidTouchpadSwipeDistance(AppSettings.MinTouchpadSwipeDistancePerImage, AppSettings.MaxTouchpadSwipeDistancePerImage), SettingsFocusField.TouchpadSwipeDistance);
                s.TouchpadSwipeDistancePerImage = value;
                return null;
            }),
        // Both preload counts are validated before either is written (an unparsable value saves nothing).
        Row([nameof(AppSettings.PreloadForwardCount), nameof(AppSettings.PreloadBackwardCount)],
            (s, f) =>
            {
                f.PreloadForwardText = s.PreloadForwardCount.ToString(Invariant);
                f.PreloadBackwardText = s.PreloadBackwardCount.ToString(Invariant);
            },
            (f, s) =>
            {
                if (!TryInt(f.PreloadForwardText, PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount, out var forward))
                    return new(Tr.DialogSettingsInvalidPreloadForward(PerformanceOptions.MinPreloadForwardCount, PerformanceOptions.MaxPreloadCount), SettingsFocusField.PreloadForward);
                if (!TryInt(f.PreloadBackwardText, PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount, out var backward))
                    return new(Tr.DialogSettingsInvalidPreloadBackward(PerformanceOptions.MinPreloadBackwardCount, PerformanceOptions.MaxPreloadCount), SettingsFocusField.PreloadBackward);
                s.PreloadForwardCount = forward;
                s.PreloadBackwardCount = backward;
                return null;
            }),
        // Auto-hide delays and the overlay font size are all validated first, then written together.
        Row([nameof(AppSettings.ToolbarAutoHide), nameof(AppSettings.ToolbarAutoHideDelayMs), nameof(AppSettings.InfoOverlayAutoHide),
                nameof(AppSettings.InfoOverlayAutoHideDelayMs), nameof(AppSettings.ToolbarOpacityPercent), nameof(AppSettings.InfoOverlayFontSize)],
            (s, f) =>
            {
                f.ToolbarAutoHide = s.ToolbarAutoHide;
                f.ToolbarAutoHideDelayText = s.ToolbarAutoHideDelayMs.ToString(Invariant);
                f.InfoOverlayAutoHide = s.InfoOverlayAutoHide;
                f.InfoOverlayAutoHideDelayText = s.InfoOverlayAutoHideDelayMs.ToString(Invariant);
                f.ToolbarOpacityValue = Math.Clamp(s.ToolbarOpacityPercent, AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
                f.InfoOverlayFontSizeText = s.InfoOverlayFontSize.ToString(Invariant);
            },
            (f, s) =>
            {
                if (!TryInt(f.ToolbarAutoHideDelayText, AppSettings.MinToolbarAutoHideDelayMs, AppSettings.MaxToolbarAutoHideDelayMs, out var toolbarDelay))
                    return new(Tr.DialogSettingsInvalidToolbarAutoHideDelay(AppSettings.MinToolbarAutoHideDelayMs, AppSettings.MaxToolbarAutoHideDelayMs));
                if (!TryInt(f.InfoOverlayAutoHideDelayText, AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs, out var overlayDelay))
                    return new(Tr.DialogSettingsInvalidInfoOverlayAutoHideDelay(AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs), SettingsFocusField.InfoOverlayAutoHideDelay);
                if (!double.TryParse(f.InfoOverlayFontSizeText, NumberStyles.Float, Invariant, out var fontSize)
                    || !double.IsFinite(fontSize) || fontSize < AppSettings.MinInfoOverlayFontSize || fontSize > AppSettings.MaxInfoOverlayFontSize)
                    return new(Tr.DialogSettingsInvalidInfoOverlayFontSize(AppSettings.MinInfoOverlayFontSize, AppSettings.MaxInfoOverlayFontSize));
                s.ToolbarAutoHide = f.ToolbarAutoHide;
                s.ToolbarAutoHideDelayMs = toolbarDelay;
                s.InfoOverlayAutoHide = f.InfoOverlayAutoHide;
                s.InfoOverlayAutoHideDelayMs = overlayDelay;
                s.ToolbarOpacityPercent = Math.Clamp((int)Math.Round(f.ToolbarOpacityValue), AppSettings.MinToolbarOpacityPercent, AppSettings.MaxToolbarOpacityPercent);
                s.InfoOverlayFontSize = fontSize;
                return null;
            }),
        Row(nameof(AppSettings.ShowZoomIndicator),
            (s, f) => f.ShowZoomIndicator = s.ShowZoomIndicator,
            (f, s) => s.ShowZoomIndicator = f.ShowZoomIndicator),
        Row(nameof(AppSettings.Shortcuts),
            (s, f) => LoadShortcuts(s.Shortcuts, f),
            (f, s) => s.Shortcuts = CanonicalizeShortcuts(ReadShortcuts(f), s.Shortcuts)),
        Row(nameof(AppSettings.Actions),
            (s, f) => f.ActionsJson = ActionListJson.Serialize(s.Actions),
            (f, s) =>
            {
                // As the old Save: the parsed list is assigned first and then checked, so a refused list stays in the clone.
                try
                {
                    s.Actions = ActionListJson.Deserialize(f.ActionsJson);
                    ActionListJson.ThrowIfInvalid(s.Actions);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return new(Tr.DialogSettingsInvalidActionsJson);
                }
                return null;
            }),
    ];

    /// <summary>The properties that have a control (or are carried by one): the union of every row's properties, plus the language.</summary>
    public static IReadOnlySet<string> CoveredProperties { get; } =
        new HashSet<string>(Bindings.SelectMany(binding => binding.Properties).Append(nameof(AppSettings.UiLanguage)), StringComparer.Ordinal);

    /// <summary>
    /// Properties of <see cref="AppSettings"/> the Settings dialog deliberately has no control for (the guard test requires
    /// every other public settable property to be in <see cref="CoveredProperties"/>), with the reason.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NotEditedInDialog { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(AppSettings.ConfigVersion)] = "Fixed to AppSettings.CurrentConfigVersion by the type itself; never shown or editable.",
        [nameof(AppSettings.ImageCacheCapacityBytes)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.MemoryReserveBytes)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.PreloadWorkerCount)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.PreloadMemoryLoadLimit)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.PreviewDiskCacheCapacityBytes)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.UseSourceBytesCache)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.SourceBytesCapacityBytes)] = "Performance internal: hand-edit config.json only (Defaults resets it).",
        [nameof(AppSettings.LastMoveToFolder)] = "Internal last-used-folder state written by Move-to; not a user-facing setting.",
        [nameof(AppSettings.LastCopyToFolder)] = "Internal last-used-folder state written by Copy-to; not a user-facing setting.",
        [nameof(AppSettings.SetZoomAlsoSetsClickLevel)] = "Defaults resets it but the dialog has no control (pre-existing gap, kept as is).",
    };

    private static SettingsFieldBinding Row(string property, Action<AppSettings, SettingsFormState>? load, Action<SettingsFormState, AppSettings> apply) =>
        new([property], load ?? ((_, _) => { }), (f, s) => { apply(f, s); return null; });

    private static SettingsFieldBinding Row(string property, Action<AppSettings, SettingsFormState>? load, Func<SettingsFormState, AppSettings, SettingsFieldError?> apply) =>
        new([property], load ?? ((_, _) => { }), apply);

    private static SettingsFieldBinding Row(string[] properties, Action<AppSettings, SettingsFormState> load, Func<SettingsFormState, AppSettings, SettingsFieldError?> apply) =>
        new(properties, load, apply);

    private static void LoadContextMenu(AppSettings settings, SettingsFormState state)
    {
        var hidden = ContextMenuItems.EffectiveHidden(settings);
        state.ContextMenuShown.Clear();
        foreach (var item in ContextMenuItems.Items)
            if (item.Locked || !hidden.Contains(item.Id)) state.ContextMenuShown.Add(item.Id);
    }

    private static bool TryInt(string? text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.Integer, Invariant, out value) && value >= min && value <= max;
}
