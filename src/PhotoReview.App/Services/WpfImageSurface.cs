using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Input;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Services;

/// <summary>
/// AR13: the WPF side of the main window's image view (ImageScroll + MainImage) for
/// <see cref="PointerInputController"/> and <see cref="FitViewController"/>. Each member is the WPF call the
/// controllers used to make directly in MainWindow. Holds the two elements, never the window
/// (<paramref name="isLoaded"/> reads the window's IsLoaded, <paramref name="updateFitSize"/> is MainWindow.UpdateFitSize).
/// </summary>
internal sealed class WpfImageSurface(ScrollViewer scroll, Image image, ViewerState viewer, Func<bool> isLoaded, Action updateFitSize, IDisplayClock? displayClock = null)
    : IImageSurface, IFitSurface
{
    public bool IsLoaded => isLoaded();

    public (double Width, double Height) ViewportSize =>
        (Math.Max(0, scroll.ActualWidth - scroll.BorderThickness.Left - scroll.BorderThickness.Right),
         Math.Max(0, scroll.ActualHeight - scroll.BorderThickness.Top - scroll.BorderThickness.Bottom));

    public void UpdateFitSize() => updateFitSize();

    public void ScrollHome()
    {
        scroll.ScrollToHome();
        scroll.ScrollToHorizontalOffset(0);
        scroll.ScrollToVerticalOffset(0);
    }

    public ViewportSnapshot Capture() =>
        new ViewportSnapshot(
            Zoom: viewer.Zoom,
            Stretch: viewer.Stretch,
            MaxImageWidth: viewer.MaxImageWidth,
            MaxImageHeight: viewer.MaxImageHeight,
            ActualImageWidth: image.ActualWidth,
            ActualImageHeight: image.ActualHeight,
            ExtentWidth: scroll.ExtentWidth,
            ExtentHeight: scroll.ExtentHeight,
            ViewportWidth: scroll.ViewportWidth,
            ViewportHeight: scroll.ViewportHeight,
            HorizontalOffset: scroll.HorizontalOffset,
            VerticalOffset: scroll.VerticalOffset,
            HorizontalScrollbarVisibility: scroll.ComputedHorizontalScrollBarVisibility,
            VerticalScrollbarVisibility: scroll.ComputedVerticalScrollBarVisibility);

    public double HorizontalOffset => scroll.HorizontalOffset;
    public double VerticalOffset => scroll.VerticalOffset;
    public double ViewportWidth => scroll.ViewportWidth;
    public double ViewportHeight => scroll.ViewportHeight;
    public double ExtentWidth => scroll.ExtentWidth;
    public double ExtentHeight => scroll.ExtentHeight;

    public (double Horizontal, double Vertical) DragThreshold =>
        (SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);

    public void ScrollTo(double horizontal, double vertical)
    {
        scroll.ScrollToHorizontalOffset(horizontal);
        scroll.ScrollToVerticalOffset(vertical);
    }

    public void UpdateLayout() => scroll.UpdateLayout();

    // DispatcherOperation's awaiter is its Task's awaiter: awaiting .Task resumes exactly as awaiting the operation did.
    public Task YieldToRenderAsync() => scroll.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render).Task;

    public Point ImageOrigin => image.TranslatePoint(new Point(0, 0), scroll);
    public Point ToImageElement(Point surfacePoint) => scroll.TranslatePoint(surfacePoint, image);
    public double ImageActualWidth => image.ActualWidth;
    public double ImageActualHeight => image.ActualHeight;
    public (double Width, double Height)? SourceSize => image.Source is { } source ? (source.Width, source.Height) : null;

    public void CaptureMouse() => image.CaptureMouse();

    public void ReleaseMouseCapture()
    {
        if (Mouse.Captured == image) Mouse.Capture(null);
    }

    public void SetPanCursor(bool panning) => image.Cursor = panning ? Cursors.SizeAll : Cursors.Arrow;

    public void HookRenderFrame(EventHandler handler) => CompositionTarget.Rendering += handler;
    public void UnhookRenderFrame(EventHandler handler) => CompositionTarget.Rendering -= handler;
    public TimeSpan? RenderingTime(EventArgs e) => e is RenderingEventArgs rendering ? rendering.RenderingTime : null;
    public long Timestamp => System.Diagnostics.Stopwatch.GetTimestamp();
    public DisplayTiming? DisplayTiming =>
        displayClock is not null && PresentationSource.FromVisual(scroll) is System.Windows.Interop.HwndSource source
            ? displayClock.GetTiming(source.Handle)
            : null;
}
