using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class AppSettings
{
    public const int CurrentConfigVersion = 3;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public InitialViewMode InitialViewMode { get; set; } = InitialViewMode.Fit;
    [JsonConverter(typeof(SettingsEnumConverter<LoadingMode>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Preview;
    public bool LoggingEnabled { get; set; }
    [JsonConverter(typeof(SettingsEnumConverter<ImageSortMode>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.Default;
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
    [JsonConverter(typeof(SettingsEnumConverter<ScalingQuality>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public ScalingQuality ScalingQuality { get; set; } = ScalingQuality.HighQuality;
    [JsonConverter(typeof(SettingsEnumConverter<DecoderBackend>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public DecoderBackend DecoderBackend { get; set; } = DecoderBackend.WicDirect;
    /// <summary>Preview cache byte budget; only used when physical RAM is unknown (otherwise <see cref="ImageCacheRamPercent"/> wins).</summary>
    public long ImageCacheCapacityBytes { get; set; } = PerformanceOptions.ImageCacheCapacityBytes;

    /// <summary>
    /// In-memory preview (+ source-bytes) cache budget as a percent of physical RAM, [system minimum, 90]. Absent in older
    /// configs, so they load with the default 50 %. Applied when the cache is created, i.e. after a restart.
    /// </summary>
    public int ImageCacheRamPercent { get; set; } = PerformanceOptions.ImageCacheRamPercent;
    public long MemoryReserveBytes { get; set; } = PerformanceOptions.MemoryReserveBytes;
    public int PreloadWorkerCount { get; set; } = PerformanceOptions.PreloadWorkerCount;
    public double PreloadMemoryLoadLimit { get; set; } = PerformanceOptions.PreloadMemoryLoadLimit;
    public long PreviewDiskCacheCapacityBytes { get; set; } = PerformanceOptions.PreviewDiskCacheCapacityBytes;
    public bool UseSourceBytesCache { get; set; } = PerformanceOptions.UseSourceBytesCache;
    public long SourceBytesCapacityBytes { get; set; } = PerformanceOptions.SourceBytesCapacityBytes;
    public List<ReviewAction> Actions { get; set; } = ReviewAction.Defaults();
    public ShortcutMappings Shortcuts { get; set; } = ShortcutMappings.Default();

    /// <summary>
    /// UI language code (<c>en</c>, <c>vi</c>, ...) or <c>auto</c> = follow the Windows UI language (ADR 0006).
    /// Configs written before version 3 are migrated to <c>vi</c> so existing users keep the Vietnamese UI (Q-L1).
    /// </summary>
    public string UiLanguage { get; set; } = PhotoReview.Core.Localization.LanguageLoader.AutoCode;

    /// <summary>
    /// Operation journal durability (ADR 0007, IO03). Absent in older configs, so they load as <see cref="JournalDurability.Fast"/>
    /// (no migration step needed); applies to the next journal write without a restart.
    /// </summary>
    public JournalDurability JournalDurability { get; set; } = JournalDurability.Fast;

    /// <summary>
    /// Q-R8: when true, Delete/Recycle on a drive without a Recycle Bin (removable, network/UNC, unknown) deletes the file
    /// PERMANENTLY after a confirmation. Default false = such deletes are refused (R2-F-05). Absent in older configs = false.
    /// </summary>
    public bool AllowPermanentDeleteWithoutRecycleBin { get; set; }

    /// <summary>
    /// "Move to…": the folder the last Move-to went to; the folder picker starts there. Absent in older configs = null
    /// (the picker starts at the photo folder's parent).
    /// </summary>
    public string? LastMoveToFolder { get; set; }

    /// <summary>"Copy to…": the folder the last Copy-to went to; the folder picker starts there. Absent = null.</summary>
    public string? LastCopyToFolder { get; set; }

    /// <summary>
    /// When true and the last Move-to/Copy-to folder still exists, the shortcut acts on it immediately without the folder
    /// picker (Shift+shortcut still opens the picker). Default false.
    /// </summary>
    public bool MoveCopyReuseLastFolder { get; set; }

    /// <summary>
    /// Q-R18: one window for everything (default) or one window per folder. Absent in older configs = SingleWindow. Read at
    /// startup from the settings store BEFORE the instance lock is taken, so a change takes effect at the next start.
    /// </summary>
    public InstanceMode InstanceMode { get; set; } = InstanceMode.SingleWindow;

    /// <summary>
    /// Master switch for the on-image info overlays (<see cref="ShowFileInfo"/>, <see cref="ShowFolderInfo"/>).
    /// Flipped at runtime by <see cref="ShortcutMappings.ToggleInfoOverlay"/> and persisted. Absent in older configs = true.
    /// </summary>
    public bool ShowInfoOverlay { get; set; } = true;

    /// <summary>Bottom-left file status block (position/count, size, name, dimensions). Absent in older configs = true.</summary>
    public bool ShowFileInfo { get; set; } = true;

    /// <summary>
    /// Bottom-right folder block: current folder and the sibling image folders that PageUp/PageDown would open.
    /// Default off (the window title already shows the current folder name); absent in older configs = false.
    /// When hidden the siblings are not computed at all (no disk I/O).
    /// </summary>
    public bool ShowFolderInfo { get; set; }

    /// <summary>
    /// Shows the photo information line (file name, date taken, dimensions, camera, lens, exposure) under the status line.
    /// Default off; absent in older configs = false. The EXIF values come from the decode that already runs, never an extra file read.
    /// </summary>
    public bool ShowExifInfo { get; set; }

    /// <summary>Parts of the photo information line to show; absent in older configs = <see cref="ExifInfoFields.Default"/>.</summary>
    public ExifInfoFields ExifInfoFields { get; set; } = ExifInfoFields.Default;

    /// <summary>
    /// Deep-clones <paramref name="source"/> by round-tripping it through the same JSON contract as disk storage
    /// (<see cref="AppSettingsJsonContext"/>), so every property is copied automatically -- including ones a caller
    /// (e.g. the Settings window) has no control for yet. Prefer this over hand-copying fields one by one: a
    /// hand-copy silently drops any property the copier forgot, which is exactly the bug this method replaces.
    /// </summary>
    public static AppSettings Clone(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var json = JsonSerializer.Serialize(source, AppSettingsJsonContext.Default.AppSettings);
        return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
    }

    // ---- Mouse / zoom (feat/mouse-zoom). Absent in older configs = these defaults; no migration step. ----

    /// <summary>Smallest accepted <see cref="ClickZoomPercent"/>.</summary>
    public const int MinClickZoomPercent = 10;

    /// <summary>Largest accepted <see cref="ClickZoomPercent"/>.</summary>
    public const int MaxClickZoomPercent = 800;

    /// <summary>Smallest accepted <see cref="ArrowPanStepPercent"/>.</summary>
    public const int MinArrowPanStepPercent = 1;

    /// <summary>Largest accepted <see cref="ArrowPanStepPercent"/> (a whole viewport per press).</summary>
    public const int MaxArrowPanStepPercent = 100;

    /// <summary>Default <see cref="ArrowPanStepPercent"/>.</summary>
    public const int DefaultArrowPanStepPercent = 10;

    /// <summary>Default <see cref="ClickZoomPercent"/>: 100 % = one source pixel per device pixel (ADR 0008).</summary>
    public const int DefaultClickZoomPercent = 100;

    /// <summary>What the plain mouse wheel does over the image; Ctrl+wheel always zooms at the cursor.</summary>
    public MouseWheelAction MouseWheelAction { get; set; } = MouseWheelAction.Zoom;

    /// <summary>A left click (no drag) toggles between Fit and <see cref="ClickZoomPercent"/>, anchored at the cursor. Default off.</summary>
    public bool ClickToZoomEnabled { get; set; }

    /// <summary>Zoom a click jumps to, in percent of source pixels (ADR 0008), [<see cref="MinClickZoomPercent"/>, <see cref="MaxClickZoomPercent"/>].</summary>
    public int ClickZoomPercent { get; set; } = DefaultClickZoomPercent;

    /// <summary>After a drag-pan is released the image keeps gliding with the release velocity and slows down.</summary>
    public bool KineticPanEnabled { get; set; }

    // ---- Preload window (feat/preload-window-setting). Absent in older configs = these defaults; no migration step. ----

    /// <summary>
    /// Direction-of-travel preload lookahead, images, [<see cref="PerformanceOptions.MinPreloadForwardCount"/>,
    /// <see cref="PerformanceOptions.MaxPreloadCount"/>]. Captured once at composition (applies after restart).
    /// </summary>
    public int PreloadForwardCount { get; set; } = PerformanceOptions.PreloadForwardCount;

    /// <summary>
    /// Backward preload lookahead, images, [<see cref="PerformanceOptions.MinPreloadBackwardCount"/>,
    /// <see cref="PerformanceOptions.MaxPreloadCount"/>]. Captured once at composition (applies after restart).
    /// </summary>
    public int PreloadBackwardCount { get; set; } = PerformanceOptions.PreloadBackwardCount;
    // ---- Toolbar auto-hide / info overlay font size (feat/ui-dark-chrome-toolbar). Absent in older configs = these defaults. ----

    /// <summary>Smallest accepted <see cref="ToolbarAutoHideDelayMs"/>.</summary>
    public const int MinToolbarAutoHideDelayMs = 0;

    /// <summary>Largest accepted <see cref="ToolbarAutoHideDelayMs"/>.</summary>
    public const int MaxToolbarAutoHideDelayMs = 10000;

    /// <summary>Default <see cref="ToolbarAutoHideDelayMs"/>.</summary>
    public const int DefaultToolbarAutoHideDelayMs = 1500;

    /// <summary>Smallest accepted <see cref="InfoOverlayAutoHideDelayMs"/>.</summary>
    public const int MinInfoOverlayAutoHideDelayMs = 0;

    /// <summary>Largest accepted <see cref="InfoOverlayAutoHideDelayMs"/>.</summary>
    public const int MaxInfoOverlayAutoHideDelayMs = 10000;

    /// <summary>Default <see cref="InfoOverlayAutoHideDelayMs"/>: longer than the toolbar's, because reading text takes longer than spotting a button.</summary>
    public const int DefaultInfoOverlayAutoHideDelayMs = 3000;

    /// <summary>Lowest accepted <see cref="ToolbarOpacityPercent"/>: the toolbar can never become practically invisible.</summary>
    public const int MinToolbarOpacityPercent = 20;

    /// <summary>Highest accepted <see cref="ToolbarOpacityPercent"/> (fully opaque).</summary>
    public const int MaxToolbarOpacityPercent = 100;

    /// <summary>Default <see cref="ToolbarOpacityPercent"/> (no visual change).</summary>
    public const int DefaultToolbarOpacityPercent = 100;

    /// <summary>Smallest accepted <see cref="InfoOverlayFontSize"/>.</summary>
    public const double MinInfoOverlayFontSize = 8;

    /// <summary>Largest accepted <see cref="InfoOverlayFontSize"/>.</summary>
    public const double MaxInfoOverlayFontSize = 24;

    /// <summary>Default <see cref="InfoOverlayFontSize"/>, matching the size the overlay used before this setting existed.</summary>
    public const double DefaultInfoOverlayFontSize = 12;

    /// <summary>
    /// The top-left toolbar (folder/settings/fit/tools) fades out after <see cref="ToolbarAutoHideDelayMs"/> once the
    /// mouse leaves it, and fades back in when the mouse enters its hot zone. Always visible when no folder is open,
    /// while its Tools popup is open, or while keyboard focus is inside it. Default off (Q-R34); a saved true is kept.
    /// </summary>
    public bool ToolbarAutoHide { get; set; }

    /// <summary>Delay, in milliseconds, before the toolbar fades out once eligible; [<see cref="MinToolbarAutoHideDelayMs"/>, <see cref="MaxToolbarAutoHideDelayMs"/>].</summary>
    public int ToolbarAutoHideDelayMs { get; set; } = DefaultToolbarAutoHideDelayMs;

    /// <summary>
    /// Font size of the bottom-left status line and the bottom-right folder info panel; the EXIF line (below the
    /// status line) is always one point smaller. [<see cref="MinInfoOverlayFontSize"/>, <see cref="MaxInfoOverlayFontSize"/>].
    /// </summary>
    public double InfoOverlayFontSize { get; set; } = DefaultInfoOverlayFontSize;

    /// <summary>Parts shown in the main window's title bar; absent in older configs = <see cref="TitleBarFields.Default"/>.</summary>
    public TitleBarFields TitleBarFields { get; set; } = TitleBarFields.Default;

    // ---- Q-R41..Q-R52 user feedback (2026-09-28): keyboard zoom step, delete confirmation, zoom HUD, ----
    // ---- external editor. Absent in older configs = these defaults; no migration step. ----

    /// <summary>Smallest accepted <see cref="KeyboardZoomStepPercent"/>.</summary>
    public const int MinKeyboardZoomStepPercent = 5;

    /// <summary>Largest accepted <see cref="KeyboardZoomStepPercent"/>.</summary>
    public const int MaxKeyboardZoomStepPercent = 100;

    /// <summary>
    /// Default <see cref="KeyboardZoomStepPercent"/>: 10 %, the value the user suggested (Q-R41) as a finer
    /// alternative to the previous hardcoded 25 % jump.
    /// </summary>
    public const int DefaultKeyboardZoomStepPercent = 10;

    /// <summary>
    /// Q-R41: how far ZoomIn/ZoomOut/wheel-zoom step the zoom level each press, in percent of the original size;
    /// [<see cref="MinKeyboardZoomStepPercent"/>, <see cref="MaxKeyboardZoomStepPercent"/>]. Replaces the previous
    /// hardcoded <c>ViewerState.ZoomStep</c> constant (0.25 = 25 %).
    /// </summary>
    public int KeyboardZoomStepPercent { get; set; } = DefaultKeyboardZoomStepPercent;

    /// <summary>
    /// Q-R44: when true, a confirmation dialog is shown before Recycle/Delete (the general case; an action profile's
    /// own <see cref="ReviewAction.Confirm"/> and the narrow <see cref="AllowPermanentDeleteWithoutRecycleBin"/>
    /// prompt are unaffected). Default false (off), matching the behaviour before this setting existed.
    /// </summary>
    public bool ConfirmBeforeDelete { get; set; }

    /// <summary>
    /// Q-R45: shows a small current-zoom-percentage HUD in a corner of the viewer, reusing the on-image info
    /// overlay infrastructure (<see cref="InfoOverlayFontSize"/> etc.). Default false (off, not shown).
    /// </summary>
    public bool ShowZoomIndicator { get; set; }

    /// <summary>
    /// Q-R48: full path to an external image editor executable. Empty (default) = not configured, which hides the
    /// "Open in External Editor" context-menu entry. Absent in older configs = empty.
    /// </summary>
    public string ExternalEditorPath { get; set; } = string.Empty;

    /// <summary>How the kinetic glide is timed against the display refresh (smoother on irregular frame delivery).</summary>
    [JsonConverter(typeof(SettingsEnumConverter<KineticGlideSmoothing>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public KineticGlideSmoothing KineticGlideSmoothing { get; set; } = KineticGlideSmoothing.Predict;

    /// <summary>
    /// Q-R34: the text overlays drawn over the photo (status line, EXIF line, folder info) fade out after
    /// <see cref="InfoOverlayAutoHideDelayMs"/> without mouse/key/navigation activity and fade back in on activity. Never while a
    /// message needs reading, no folder is open, Compare is open or the window is inactive. Default off.
    /// </summary>
    public bool InfoOverlayAutoHide { get; set; }

    /// <summary>Idle delay, in milliseconds, before the info overlays fade out (independent of the toolbar's delay); [<see cref="MinInfoOverlayAutoHideDelayMs"/>, <see cref="MaxInfoOverlayAutoHideDelayMs"/>], default <see cref="DefaultInfoOverlayAutoHideDelayMs"/>.</summary>
    public int InfoOverlayAutoHideDelayMs { get; set; } = DefaultInfoOverlayAutoHideDelayMs;

    /// <summary>
    /// How solid the toolbar looks while it is visible, in percent; [<see cref="MinToolbarOpacityPercent"/>, <see cref="MaxToolbarOpacityPercent"/>].
    /// The auto-hide fade goes between 0 (hidden) and this value.
    /// </summary>
    public int ToolbarOpacityPercent { get; set; } = DefaultToolbarOpacityPercent;

    /// <summary>
    /// Default off: arrow keys only pan a zoomed image and never change photo (go back to Fit first). On: a fresh
    /// arrow press at the edge of a zoomed image navigates to the next/previous photo (auto-repeat is swallowed).
    /// </summary>
    public bool ArrowKeyNavigatesAtZoomEdge { get; set; }

    /// <summary>How far one arrow press moves a zoomed image, in percent of the viewport; [<see cref="MinArrowPanStepPercent"/>, <see cref="MaxArrowPanStepPercent"/>], default <see cref="DefaultArrowPanStepPercent"/>.</summary>
    public int ArrowPanStepPercent { get; set; } = DefaultArrowPanStepPercent;

    // ---- Fit width / Fit height, keep zoom across images (PR-B feat/fit-width-height-keep-zoom). Absent in older configs = these defaults; no migration step. ----

    /// <summary>
    /// When true, an image change does not reset the view at all: <see cref="ViewModels.ViewerState.ApplyInitialViewMode"/>
    /// (called by <c>ImagePresenter</c> through <c>IPresentationSink.ApplyInitialViewMode</c>) is a no-op, so Fit stays
    /// Fit and a zoom stays at the same zoom -- the ScrollViewer keeps/clamps its offsets, giving the same framing for
    /// same-sized photos. Default off. Toggled by <see cref="ShortcutMappings.ToggleKeepZoom"/> (key <c>K</c>).
    /// </summary>
    public bool KeepZoomAcrossImages { get; set; }

    /// <summary>
    /// Vertical anchor for <see cref="Model.InitialViewMode.FitWidth"/> (initial view and the FitWidth shortcut,
    /// regardless of mouse position). Default <see cref="Model.FitWidthAnchor.Centre"/>.
    /// </summary>
    public Model.FitWidthAnchor FitWidthAnchor { get; set; } = Model.FitWidthAnchor.Centre;

    /// <summary>
    /// Vertical anchor of the SECOND, independent Fit width command (<see cref="ShortcutMappings.FitWidth2"/>, the
    /// "Fit width 2" context-menu item, the middle-click "Fit width 2" choice). Does not affect the initial view
    /// (that is <see cref="FitWidthAnchor"/>). Default <see cref="Model.FitWidthAnchor.BottomThird"/>; absent in older
    /// configs loads as that default, and an unparsable value is reset to it and reported (RV-D2).
    /// </summary>
    [JsonConverter(typeof(SettingsEnumConverter<Model.FitWidthAnchor>))]
    public Model.FitWidthAnchor FitWidthAnchor2 { get; set; } = Model.FitWidthAnchor.BottomThird;

    /// <summary>
    /// What a middle-button click on the main image does (<see cref="Model.MiddleClickAction"/>). Default
    /// <see cref="Model.MiddleClickAction.ActualSize"/> (100 %). Absent in older configs loads as the default; an
    /// unparsable value is reset to it and reported (RV-D2).
    /// </summary>
    [JsonConverter(typeof(SettingsEnumConverter<Model.MiddleClickAction>))]
    public Model.MiddleClickAction MiddleClickAction { get; set; } = Model.MiddleClickAction.ActualSize;

    /// <summary>
    /// feat/zoom-key-anchor: what point stays under the zoom for +/- (keyboard zoom in/out), 100 % and the
    /// click-zoom shortcut/menu (mouse wheel and click-to-zoom already anchor at the cursor regardless of this
    /// setting). Default <see cref="KeyboardZoomAnchor.ViewportCentre"/>; absent in older configs also loads as
    /// <see cref="KeyboardZoomAnchor.ViewportCentre"/> (the property initializer is the deserialization default).
    /// </summary>
    [JsonConverter(typeof(SettingsEnumConverter<KeyboardZoomAnchor>))] // RV-S01: unparsable -> undefined -> reset to the default + reported
    public KeyboardZoomAnchor KeyboardZoomAnchor { get; set; } = KeyboardZoomAnchor.ViewportCentre;

    // ---- Image change transition (PR-D feat/image-crossfade). Absent in older configs = these defaults; no migration step. ----

    /// <summary>Smallest accepted <see cref="ImageTransitionMs"/>.</summary>
    public const int MinImageTransitionMs = 40;

    /// <summary>Largest accepted <see cref="ImageTransitionMs"/>.</summary>
    public const int MaxImageTransitionMs = 400;

    /// <summary>Default <see cref="ImageTransitionMs"/>.</summary>
    public const int DefaultImageTransitionMs = 120;

    /// <summary>
    /// Optional transition when the CURRENT PHOTO CHANGES (navigation to another file). Never applied to the
    /// progressive upgrades of the same image (thumbnail -> preview -> original); those stay an instant swap.
    /// Default <see cref="ImageTransition.None"/> (zero extra cost: no elements rendered, no animation created).
    /// </summary>
    public ImageTransition ImageTransition { get; set; } = ImageTransition.None;

    /// <summary>Duration of the <see cref="ImageTransition.Fade"/> transition, in milliseconds; [<see cref="MinImageTransitionMs"/>, <see cref="MaxImageTransitionMs"/>], default <see cref="DefaultImageTransitionMs"/>.</summary>
    public int ImageTransitionMs { get; set; } = DefaultImageTransitionMs;

    // ---- Context menu redesign (PR-C feat/context-menu-redesign). Absent in older configs = these defaults; no migration step. ----

    /// <summary>
    /// When true (default, matches the behaviour before this setting existed), choosing a preset or Custom in the
    /// right-click "Zoom" submenu also updates <see cref="ClickZoomPercent"/>, like "Zoom to N%" then reads back.
    /// When false, the same choice still zooms the image immediately (<c>PointerInputController.SetClickZoomLevelAsync</c>)
    /// but is a one-off: it does not touch the saved click zoom level.
    /// </summary>
    public bool SetZoomAlsoSetsClickLevel { get; set; } = true;

    /// <summary>
    /// Shows the folder group (Open folder / Next folder / Previous folder) in the right-click context menu, as one
    /// unit including the separator above it. Default true. Toggled from Settings only (no shortcut); Next/Previous
    /// folder still work via their own shortcuts (<see cref="ShortcutMappings.NextFolder"/>/<see cref="ShortcutMappings.PreviousFolder"/>)
    /// when the menu items are hidden.
    /// </summary>
    public bool ShowFolderMenuItems { get; set; } = true;

    /// <summary>
    /// Shows the zoom cluster ("Fit to window", "Zoom to N%" and the "Zoom" submenu) in the right-click context
    /// menu, as one unit including the separator above it. Default false (new behaviour; unlike
    /// <see cref="ShowFolderMenuItems"/>, this cluster starts hidden). Toggled from Settings only (no shortcut);
    /// the underlying zoom actions still work via their own shortcuts when the menu items are hidden.
    /// </summary>
    public bool ShowZoomMenuItems { get; set; }

    /// <summary>
    /// Shows the "Move to Recycle Bin" item in the right-click context menu. Default false (starts hidden, like
    /// <see cref="ShowZoomMenuItems"/>): the Move to Recycle Bin shortcut still works either way, so an accidental
    /// click on a destructive menu item isn't a risk for users who never opt in. Unlike <see cref="ShowFolderMenuItems"/>
    /// and <see cref="ShowZoomMenuItems"/>, it does not gate a whole cluster or its own separator -- it toggles a
    /// single item right after Undo (see Q-R38 ordering).
    /// </summary>
    public bool ShowRecycleMenuItem { get; set; }

    // ---- Camera RAW support (feat/raw-support-integration). Absent in older configs = these defaults. ----

    /// <summary>
    /// Master switch for Camera RAW format support (.cr2, .cr3, .nef, .arw, .dng, .raf, .orf, .rw2).
    /// Default true (RAW-70 enables the completed RAW support by default).
    /// </summary>
    public bool RawSupportEnabled { get; set; } = true;

    /// <summary>
    /// RAW full demosaicing behaviour (Q-RAW-02).
    /// Default <see cref="RawFullDecode.Never"/> (fast embedded previews).
    /// </summary>
    public RawFullDecode RawFullDecode { get; set; } = RawFullDecode.Never;

    /// <summary>
    /// Handling mode for JPG+RAW pairs (Q-RAW-04).
    /// Default <see cref="RawPairMode.Separate"/>.
    /// </summary>
    public RawPairMode RawPairMode { get; set; } = RawPairMode.Separate;

    /// <summary>
    /// Lists WebP and HEIC/HEIF files (.webp, .heic, .heif) and decodes them through the Windows codecs (WIC), never a bundled
    /// library (Q-FMT-WEBP-HEIC). Default true, including for older configs that omit it. A PC without the codec still lists
    /// the files and shows a localized "install ... from the Microsoft Store" error on them.
    /// </summary>
    public bool WebpHeicSupportEnabled { get; set; } = true;
}

