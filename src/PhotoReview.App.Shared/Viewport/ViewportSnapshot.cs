using PhotoReview.App.ViewModels;

namespace PhotoReview.App.Viewport;

/// <summary>Immutable snapshot of viewport/layout state for convergence detection.</summary>
public record ViewportSnapshot(
    double Zoom,
    ViewerStretchMode Stretch,
    double MaxImageWidth,
    double MaxImageHeight,
    double ActualImageWidth,
    double ActualImageHeight,
    double ExtentWidth,
    double ExtentHeight,
    double ViewportWidth,
    double ViewportHeight,
    double HorizontalOffset,
    double VerticalOffset,
    bool HorizontalScrollBarVisible,
    bool VerticalScrollBarVisible)
{
    public override string ToString() =>
        $"Zoom={Zoom:F2} Stretch={Stretch} MaxImage=({MaxImageWidth:F0},{MaxImageHeight:F0}) " +
        $"Actual=({ActualImageWidth:F0},{ActualImageHeight:F0}) Extent=({ExtentWidth:F0},{ExtentHeight:F0}) " +
        $"Viewport=({ViewportWidth:F0},{ViewportHeight:F0}) Offset=({HorizontalOffset:F1},{VerticalOffset:F1}) " +
        $"Scrollbars=({(HorizontalScrollBarVisible ? "Visible" : "Collapsed")},{(VerticalScrollBarVisible ? "Visible" : "Collapsed")})"; // same text as when these were WPF Visibility (diagnostics)
}
