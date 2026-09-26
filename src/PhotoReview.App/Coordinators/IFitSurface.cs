using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// AR13b: what <see cref="FitViewController"/> needs from the main window's image view. The WPF adapter is
/// <see cref="PhotoReview.App.Services.WpfImageSurface"/>; unit tests use a fake.
/// </summary>
internal interface IFitSurface
{
    /// <summary>ImageScroll's size inside its border (the space Fit fills).</summary>
    (double Width, double Height) ViewportSize { get; }

    /// <summary>The window is loaded (a pass resuming after close does nothing).</summary>
    bool IsLoaded { get; }

    /// <summary>ImageScroll.UpdateLayout().</summary>
    void UpdateLayout();

    /// <summary>MainWindow.UpdateFitSize: pushes the viewport size to the viewer and the decode box.</summary>
    void UpdateFitSize();

    /// <summary>Completes once the dispatcher has run its queue down to Render priority.</summary>
    Task YieldToRenderAsync();

    /// <summary>The viewer/layout state compared between passes (CaptureViewportSnapshot).</summary>
    ViewportSnapshot Capture();

    /// <summary>ScrollToHome, then horizontal and vertical offset 0.</summary>
    void ScrollHome();
}
