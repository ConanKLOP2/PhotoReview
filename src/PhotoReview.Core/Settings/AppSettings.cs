using System.Text.Json;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Settings;

public class AppSettings
{
    public const int CurrentConfigVersion = 3;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public InitialViewMode InitialViewMode { get; set; } = InitialViewMode.Fit;
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Preview;
    public bool LoggingEnabled { get; set; }
    public ImageSortMode ImageSortMode { get; set; } = ImageSortMode.Name;
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
    public ScalingQuality ScalingQuality { get; set; } = ScalingQuality.HighQuality;
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

    /// <summary>Default <see cref="ClickZoomPercent"/>: 100 % = one source pixel per device pixel (ADR 0008).</summary>
    public const int DefaultClickZoomPercent = 100;

    /// <summary>What the plain mouse wheel does over the image; Ctrl+wheel always zooms at the cursor.</summary>
    public MouseWheelAction MouseWheelAction { get; set; } = MouseWheelAction.Zoom;

    /// <summary>A left click (no drag) toggles between Fit and <see cref="ClickZoomPercent"/>, anchored at the cursor. Default off.</summary>
    public bool ClickToZoomEnabled { get; set; }

    /// <summary>Zoom a click jumps to, in percent of source pixels (ADR 0008), [<see cref="MinClickZoomPercent"/>, <see cref="MaxClickZoomPercent"/>].</summary>
    public int ClickZoomPercent { get; set; } = DefaultClickZoomPercent;

    /// <summary>After a drag-pan is released the image keeps gliding with the release velocity and slows down.</summary>
    public bool KineticPanEnabled { get; set; } = true;

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

    /// <summary>Smallest accepted <see cref="InfoOverlayFontSize"/>.</summary>
    public const double MinInfoOverlayFontSize = 8;

    /// <summary>Largest accepted <see cref="InfoOverlayFontSize"/>.</summary>
    public const double MaxInfoOverlayFontSize = 24;

    /// <summary>Default <see cref="InfoOverlayFontSize"/>, matching the size the overlay used before this setting existed.</summary>
    public const double DefaultInfoOverlayFontSize = 12;

    /// <summary>
    /// The top-left toolbar (folder/settings/fit/tools) fades out after <see cref="ToolbarAutoHideDelayMs"/> once the
    /// mouse leaves it, and fades back in when the mouse enters its hot zone. Always visible when no folder is open,
    /// while its Tools popup is open, or while keyboard focus is inside it. Default on.
    /// </summary>
    public bool ToolbarAutoHide { get; set; } = true;

    /// <summary>Delay, in milliseconds, before the toolbar fades out once eligible; [<see cref="MinToolbarAutoHideDelayMs"/>, <see cref="MaxToolbarAutoHideDelayMs"/>].</summary>
    public int ToolbarAutoHideDelayMs { get; set; } = DefaultToolbarAutoHideDelayMs;

    /// <summary>
    /// Font size of the bottom-left status line and the bottom-right folder info panel; the EXIF line (below the
    /// status line) is always one point smaller. [<see cref="MinInfoOverlayFontSize"/>, <see cref="MaxInfoOverlayFontSize"/>].
    /// </summary>
    public double InfoOverlayFontSize { get; set; } = DefaultInfoOverlayFontSize;

    /// <summary>Parts shown in the main window's title bar; absent in older configs = <see cref="TitleBarFields.Default"/>.</summary>
    public TitleBarFields TitleBarFields { get; set; } = TitleBarFields.Default;

    /// <summary>How the kinetic glide is timed against the display refresh (smoother on irregular frame delivery).</summary>
    public KineticGlideSmoothing KineticGlideSmoothing { get; set; } = KineticGlideSmoothing.Predict;

    /// <summary>
    /// Default off: arrow keys only pan a zoomed image and never change photo (go back to Fit first). On: a fresh
    /// arrow press at the edge of a zoomed image navigates to the next/previous photo (auto-repeat is swallowed).
    /// </summary>
    public bool ArrowKeyNavigatesAtZoomEdge { get; set; }
}

