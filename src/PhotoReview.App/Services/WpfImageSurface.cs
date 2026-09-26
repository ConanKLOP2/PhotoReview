using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoReview.App.Input;

namespace PhotoReview.App.Services;

/// <summary>
/// AR13: the WPF side of the main window's image view (ImageScroll + MainImage) for
/// <see cref="PointerInputController"/>. Each member is the one-line WPF call the controller used to make directly
/// in MainWindow. Holds the two elements, never the window (<paramref name="isLoaded"/> reads the window's IsLoaded).
/// </summary>
internal sealed class WpfImageSurface(ScrollViewer scroll, Image image, Func<bool> isLoaded) : IImageSurface
{
    public bool IsLoaded => isLoaded();

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
}
