using System;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;

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
        if (elementWidth <= 0 || elementHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return new(0, 0);

        var scale = Math.Min(elementWidth / sourceWidth, elementHeight / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var left = (elementWidth - renderedWidth) / 2;
        var top = (elementHeight - renderedHeight) / 2;
        return new(
            Math.Clamp((pointerX - left) / scale, 0, sourceWidth),
            Math.Clamp((pointerY - top) / scale, 0, sourceHeight));
    }

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

    internal static ZoomViewportOffsets CalculateZoomViewportOffsets(
        double oldZoom,
        double newZoom,
        double mouseX,
        double mouseY,
        double oldHorizontalOffset,
        double oldVerticalOffset,
        double newExtentWidth,
        double newExtentHeight,
        double viewportWidth,
        double viewportHeight)
    {
        if (!double.IsFinite(oldZoom) || oldZoom <= 0 || !double.IsFinite(newZoom) || newZoom <= 0)
            return new(ClampOffset(oldHorizontalOffset, newExtentWidth, viewportWidth), ClampOffset(oldVerticalOffset, newExtentHeight, viewportHeight));

        var ratio = newZoom / oldZoom;
        var horizontal = (oldHorizontalOffset + Math.Max(0, mouseX)) * ratio - Math.Max(0, mouseX);
        var vertical = (oldVerticalOffset + Math.Max(0, mouseY)) * ratio - Math.Max(0, mouseY);
        return new(ClampOffset(horizontal, newExtentWidth, viewportWidth), ClampOffset(vertical, newExtentHeight, viewportHeight));
    }

    private static double ClampOffset(double value, double extent, double viewport)
    {
        var maximum = Math.Max(0, extent - viewport);
        return Math.Clamp(double.IsFinite(value) ? value : 0, 0, maximum);
    }

}

internal sealed class ForwardingFolderSink(Func<IFolderLoadSink> target) : IFolderLoadSink
{
    public void ResetCaches() => target().ResetCaches();
    public void OnCatalogReady(string folder, int count) => target().OnCatalogReady(folder, count);
    public Task PresentAsync(int index, long presentationGeneration) => target().PresentAsync(index, presentationGeneration);
    public void OnEmpty(string folder) => target().OnEmpty(folder);
    public void OnOrderApplied(int count, int currentIndex) => target().OnOrderApplied(count, currentIndex);
    public void OnFailed(string folder, Exception exception) => target().OnFailed(folder, exception);
}
