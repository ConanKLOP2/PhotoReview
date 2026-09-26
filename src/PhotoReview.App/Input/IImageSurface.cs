using System;
using System.Threading.Tasks;
using System.Windows;

namespace PhotoReview.App.Input;

/// <summary>
/// AR13a: what <see cref="PointerInputController"/> needs from the image view (the <c>ImageScroll</c> ScrollViewer and
/// the <c>MainImage</c> element). Points are in ImageScroll coordinates. The WPF adapter is
/// <see cref="PhotoReview.App.Services.WpfImageSurface"/>; unit tests use a fake.
/// </summary>
internal interface IImageSurface
{
    /// <summary>The window is loaded (a glide frame or a zoom continuation after close does nothing).</summary>
    bool IsLoaded { get; }

    double HorizontalOffset { get; }
    double VerticalOffset { get; }
    double ViewportWidth { get; }
    double ViewportHeight { get; }
    double ExtentWidth { get; }
    double ExtentHeight { get; }

    /// <summary>SystemParameters.MinimumHorizontalDragDistance / MinimumVerticalDragDistance.</summary>
    (double Horizontal, double Vertical) DragThreshold { get; }

    /// <summary>ScrollToHorizontalOffset(<paramref name="horizontal"/>) then ScrollToVerticalOffset(<paramref name="vertical"/>).</summary>
    void ScrollTo(double horizontal, double vertical);

    /// <summary>ImageScroll.UpdateLayout().</summary>
    void UpdateLayout();

    /// <summary>Completes once the dispatcher has run its queue down to Render priority.</summary>
    Task YieldToRenderAsync();

    /// <summary>The image element's top-left corner in ImageScroll coordinates.</summary>
    Point ImageOrigin { get; }

    /// <summary>Translates an ImageScroll point into image-element coordinates.</summary>
    Point ToImageElement(Point surfacePoint);

    double ImageActualWidth { get; }
    double ImageActualHeight { get; }

    /// <summary>The displayed bitmap's size (ImageSource.Width/Height), or null without one.</summary>
    (double Width, double Height)? SourceSize { get; }

    /// <summary>The image element captures the mouse.</summary>
    void CaptureMouse();

    /// <summary>Releases the mouse capture if (and only if) the image element holds it.</summary>
    void ReleaseMouseCapture();

    /// <summary>The image's cursor: the pan cursor while a pannable press is tracked, the arrow otherwise.</summary>
    void SetPanCursor(bool panning);

    /// <summary>Subscribes to the per-frame render callback (CompositionTarget.Rendering).</summary>
    void HookRenderFrame(EventHandler handler);

    /// <summary>Unsubscribes from the per-frame render callback.</summary>
    void UnhookRenderFrame(EventHandler handler);

    /// <summary>The frame time carried by a render callback's arguments, or null if they are not frame arguments.</summary>
    TimeSpan? RenderingTime(EventArgs e);
}
