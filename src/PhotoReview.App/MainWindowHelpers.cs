using PhotoReview.App.Coordinators;

namespace PhotoReview.App;

internal static class MainWindowHelpers
{
    internal readonly record struct ZoomViewportOffsets(double Horizontal, double Vertical);
    internal readonly record struct ZoomImagePoint(double X, double Y);

    internal static ZoomImagePoint CalculateUniformImagePoint(
        double elementWidth,
        double elementHeight,
        double sourceWidth,
        double sourceHeight,
        double pointerX,
        double pointerY)
    {
        if (!(elementWidth > 0) || !(elementHeight > 0) || !(sourceWidth > 0) || !(sourceHeight > 0)
            || !double.IsFinite(elementWidth) || !double.IsFinite(elementHeight)
            || !double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight)
            || !double.IsFinite(pointerX) || !double.IsFinite(pointerY))
            return new(0, 0);

        var scale = Math.Min(elementWidth / sourceWidth, elementHeight / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var left = (elementWidth - renderedWidth) / 2;
        var top = (elementHeight - renderedHeight) / 2;
        return new(
            ClampToSource((pointerX - left) / scale, sourceWidth),
            ClampToSource((pointerY - top) / scale, sourceHeight));
    }

    // A denormal element size makes the scale overflow ((x - left) / scale = Infinity, or 0 * Infinity = NaN).
    private static double ClampToSource(double value, double max) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, max);

    /// <summary>
    /// feat(zoom): expresses a point on the image as a fraction of the image's size, so the wheel
    /// anchor survives a zoom step even though the element's own size (not a LayoutTransform) changes.
    /// Not clamped: a pointer beside a centered, smaller-than-viewport image keeps its old behaviour.
    /// </summary>
    internal static ZoomImagePoint NormalizeImagePoint(double x, double y, double width, double height) =>
        width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height)
            && double.IsFinite(x / width) && double.IsFinite(y / height)
            ? new(x / width, y / height)
            : new(0.5, 0.5);

    internal static ZoomViewportOffsets CalculateOffsetsFromAnchorDelta(
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double anchorBeforeX,
        double anchorBeforeY,
        double anchorAfterX,
        double anchorAfterY,
        double newExtentWidth,
        double newExtentHeight,
        double viewportWidth,
        double viewportHeight) => new(
            ClampOffset(currentHorizontalOffset + anchorAfterX - anchorBeforeX, newExtentWidth, viewportWidth),
            ClampOffset(currentVerticalOffset + anchorAfterY - anchorBeforeY, newExtentHeight, viewportHeight));

    /// <summary>
    /// feat/mouse-zoom: scroll offsets that put the image point at <paramref name="anchor"/> (a fraction of the
    /// image, see <see cref="NormalizeImagePoint"/>) back under the cursor after a zoom to ANY level (wheel step or
    /// click-to-zoom). <paramref name="imageLeft"/>/<paramref name="imageTop"/> are where the image element sits in
    /// the viewport after the new layout at the current offsets, <paramref name="imageWidth"/>/<paramref name="imageHeight"/>
    /// its new size; <paramref name="cursorX"/>/<paramref name="cursorY"/> the cursor in viewport coordinates.
    /// </summary>
    internal static ZoomViewportOffsets CalculateZoomToPointOffsets(
        ZoomImagePoint anchor,
        double imageLeft,
        double imageTop,
        double imageWidth,
        double imageHeight,
        double cursorX,
        double cursorY,
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double extentWidth,
        double extentHeight,
        double viewportWidth,
        double viewportHeight) =>
        CalculateOffsetsFromAnchorDelta(
            currentHorizontalOffset,
            currentVerticalOffset,
            cursorX,
            cursorY,
            imageLeft + anchor.X * imageWidth,
            imageTop + anchor.Y * imageHeight,
            extentWidth,
            extentHeight,
            viewportWidth,
            viewportHeight);

    /// <summary>
    /// PR-B: the vertical anchor for <see cref="PhotoReview.Core.Model.InitialViewMode.FitWidth"/> (initial view, and
    /// the FitWidth shortcut when the mouse is not over the viewport), as an image-fraction point (see
    /// <see cref="ZoomImagePoint"/>). <see cref="PhotoReview.Core.Model.FitWidthAnchor.TopThird"/> keeps the point a
    /// third of the way down the image at the viewport centre (portraits usually have the face there);
    /// <see cref="PhotoReview.Core.Model.FitWidthAnchor.BottomThird"/> keeps (0.5, 2/3) there (lower part of the image), and
    /// <see cref="PhotoReview.Core.Model.FitWidthAnchor.Centre"/> keeps the image's vertical centre there instead.
    /// </summary>
    internal static ZoomImagePoint CalculateFitWidthAnchorPoint(PhotoReview.Core.Model.FitWidthAnchor anchor) =>
        anchor switch
        {
            PhotoReview.Core.Model.FitWidthAnchor.TopThird => new ZoomImagePoint(0.5, 1.0 / 3.0),
            PhotoReview.Core.Model.FitWidthAnchor.BottomThird => new ZoomImagePoint(0.5, 2.0 / 3.0),
            _ => new ZoomImagePoint(0.5, 0.5),
        };

    /// <summary>
    /// Which configured anchor a Fit width command uses: <see cref="Input.ReviewCommandType.FitWidth2"/> follows
    /// <see cref="PhotoReview.Core.Settings.AppSettings.FitWidthAnchor2"/>, every other command
    /// <see cref="PhotoReview.Core.Settings.AppSettings.FitWidthAnchor"/> (the first Fit width and the initial view).
    /// Not <see cref="PhotoReview.Core.Settings.AppSettings.KeyboardZoomAnchor"/>, which never affects Fit width.
    /// </summary>
    internal static PhotoReview.Core.Model.FitWidthAnchor FitWidthAnchorFor(PhotoReview.Core.Settings.AppSettings settings, Input.ReviewCommandType type) =>
        type == Input.ReviewCommandType.FitWidth2 ? settings.FitWidthAnchor2 : settings.FitWidthAnchor;

    internal static ZoomViewportOffsets CalculatePanOffsets(
        double currentHorizontalOffset,
        double currentVerticalOffset,
        double deltaX,
        double deltaY,
        double extentWidth,
        double extentHeight,
        double viewportWidth,
        double viewportHeight) => new(
            ClampOffset(currentHorizontalOffset - deltaX, extentWidth, viewportWidth),
            ClampOffset(currentVerticalOffset - deltaY, extentHeight, viewportHeight));

    /// <summary>
    /// True when a pointer displacement from the press point has reached the system drag distance on
    /// either axis (inclusive), i.e. a press-and-move is a pan rather than a click.
    /// </summary>
    internal static bool IsBeyondDragThreshold(double totalDeltaX, double totalDeltaY, double minimumHorizontal, double minimumVertical) =>
        Math.Abs(totalDeltaX) >= minimumHorizontal || Math.Abs(totalDeltaY) >= minimumVertical;

    private static double ClampOffset(double value, double extent, double viewport)
    {
        var room = extent - viewport;
        var maximum = double.IsFinite(room) ? Math.Max(0, room) : 0; // NaN would make Math.Clamp return the value unclamped
        return Math.Clamp(double.IsFinite(value) ? value : 0, 0, maximum);
    }

    /// <summary>
    /// PR-C: "Set current zoom as click level" menu item's target percent -- <paramref name="effectiveZoom"/> (a
    /// factor, e.g. 1.5 = 150 %) rounded to the nearest percent and clamped to <see cref="AppSettings.MinClickZoomPercent"/>/
    /// <see cref="AppSettings.MaxClickZoomPercent"/>. Null when <paramref name="effectiveZoom"/> is null (the viewer
    /// is in Fit), which has no zoom factor to capture; the caller disables the menu item in that case.
    /// </summary>
    internal static int? CalculateClickLevelFromEffectiveZoom(double? effectiveZoom) =>
        effectiveZoom is { } zoom
            ? Math.Clamp((int)Math.Round(zoom * 100, MidpointRounding.AwayFromZero), AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent)
            : null;
}

internal sealed class ForwardingFolderSink(Func<IFolderLoadSink> target) : IFolderLoadSink
{
    public void ResetCaches() => target().ResetCaches();
    public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session) => target().OnCatalogReady(folder, count, session);
    public Task PresentAsync(int index, long presentationGeneration) => target().PresentAsync(index, presentationGeneration);
    public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session) => target().OnEmpty(folder, session);
    public void OnEmptyWithSubfolders(string folder, PhotoReview.Core.Session.SessionState session, int subfolderCount) => target().OnEmptyWithSubfolders(folder, session, subfolderCount);
    public void OnOrderApplied(int count, int currentIndex, bool currentKept) => target().OnOrderApplied(count, currentIndex, currentKept);
    public void OnFilesSkipped(string folder, IReadOnlyList<PhotoReview.Core.Abstractions.SkippedEntry> skipped) => target().OnFilesSkipped(folder, skipped);
    public void OnFailed(string folder, Exception exception) => target().OnFailed(folder, exception);
    public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => target().OnUnreadableRemovedAsync(removedPaths, currentRemoved);
}
