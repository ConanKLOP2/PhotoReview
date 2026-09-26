using PhotoReview.Core.Settings;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Q-R34: pure decision logic for auto-hiding the text overlays drawn over the photo (status line, EXIF line, folder
/// info). Dependency-free (no WPF types), like <see cref="ToolbarAutoHidePolicy"/>. The wiring only animates one
/// container's Opacity / IsHitTestVisible from <see cref="Evaluate"/>; it never changes a child's Visibility, so an
/// overlay the user turned off (Show* toggles) stays off.
/// </summary>
public static class InfoOverlayAutoHidePolicy
{
    /// <summary>What the overlay container should look like: <see cref="Opacity"/> 0 or 1, and whether it may take mouse input.</summary>
    public readonly record struct Outcome(double Opacity, bool IsHitTestVisible);

    /// <summary>
    /// True when the overlays must be shown regardless of idle time: the feature is off, no folder is open, a message the
    /// user must read is showing (loading/error/no-images/event text/skipped warning), Compare is open, or the main window
    /// is not active (a dialog, Settings or another app has the focus).
    /// </summary>
    public static bool MustStayVisible(bool autoHideEnabled, bool hasFolderOpen, bool statusNeedsAttention, bool isCompareOpen, bool isWindowActive) =>
        !autoHideEnabled || !hasFolderOpen || statusNeedsAttention || isCompareOpen || !isWindowActive;

    /// <summary>The info overlays' idle delay in ms: only <see cref="AppSettings.InfoOverlayAutoHideDelayMs"/> (never the toolbar's delay), clamped to its range.</summary>
    public static int DelayMs(AppSettings settings) =>
        Math.Clamp(settings.InfoOverlayAutoHideDelayMs, AppSettings.MinInfoOverlayAutoHideDelayMs, AppSettings.MaxInfoOverlayAutoHideDelayMs);

    /// <summary>Hidden (opacity 0, click-through) only when nothing forces visibility and the idle delay has elapsed.</summary>
    public static Outcome Evaluate(bool autoHideEnabled, bool hasFolderOpen, bool statusNeedsAttention, bool isCompareOpen, bool isWindowActive, bool idleElapsed)
    {
        var hidden = !MustStayVisible(autoHideEnabled, hasFolderOpen, statusNeedsAttention, isCompareOpen, isWindowActive) && idleElapsed;
        return new Outcome(hidden ? 0.0 : 1.0, IsHitTestVisible: !hidden);
    }
}
