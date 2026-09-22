using System.Windows;
using PhotoReview.App.ViewModels;
using Xunit;

namespace PhotoReview.App.Tests;

public class ViewportConvergenceTests
{
    [Fact]
    public void IsStableViewport_SameSnapshot_ReturnsTrue()
    {
        var snapshot = CreateSnapshot(
            viewport: (800, 600),
            extent: (800, 600),
            scrollbarVis: (Visibility.Collapsed, Visibility.Collapsed));

        var result = ViewportConvergence.IsStableViewport(snapshot, snapshot);

        Assert.True(result);
    }

    [Fact]
    public void IsStableViewport_SubpixelDifference_ReturnsTrue()
    {
        var before = CreateSnapshot(viewport: (800.0, 600.0));
        var after = CreateSnapshot(viewport: (800.3, 600.2)); // < Epsilon (0.5 DIP)

        var result = ViewportConvergence.IsStableViewport(before, after);

        Assert.True(result);
    }

    [Fact]
    public void IsStableViewport_SignificantViewportChange_ReturnsFalse()
    {
        var before = CreateSnapshot(viewport: (800, 600));
        var after = CreateSnapshot(viewport: (750, 600)); // > Epsilon

        var result = ViewportConvergence.IsStableViewport(before, after);

        Assert.False(result);
    }

    [Fact]
    public void IsStableViewport_ScrollbarVisibilityChanged_ReturnsFalse()
    {
        var before = CreateSnapshot(
            viewport: (800, 600),
            scrollbarVis: (Visibility.Collapsed, Visibility.Collapsed));
        var after = CreateSnapshot(
            viewport: (800, 600),
            scrollbarVis: (Visibility.Visible, Visibility.Collapsed)); // Horizontal scrollbar appeared

        var result = ViewportConvergence.IsStableViewport(before, after);

        Assert.False(result);
    }

    [Fact]
    public void HasMeaningfulChange_UnchangedViewport_ReturnsFalse()
    {
        var snapshot = CreateSnapshot();
        var result = ViewportConvergence.HasMeaningfulChange(snapshot, snapshot);
        Assert.False(result);
    }

    [Fact]
    public void HasMeaningfulChange_ViewportSizeChanged_ReturnsTrue()
    {
        var before = CreateSnapshot(viewport: (800, 600));
        var after = CreateSnapshot(viewport: (750, 600));
        var result = ViewportConvergence.HasMeaningfulChange(before, after);
        Assert.True(result);
    }

    [Fact]
    public void ValidateMeasurements_ValidSnapshot_ReturnsValid()
    {
        var snapshot = CreateSnapshot(
            viewport: (800, 600),
            extent: (1000, 1200),
            offsets: (0, 0));

        var result = ViewportConvergence.ValidateMeasurements(snapshot);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateMeasurements_InvalidViewportWidth_ReturnsInvalid()
    {
        var snapshot = CreateSnapshot(viewport: (-1, 600)); // Invalid width

        var result = ViewportConvergence.ValidateMeasurements(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains("Viewport", result.Reason);
    }

    [Fact]
    public void ValidateMeasurements_NaNViewportHeight_ReturnsInvalid()
    {
        var snapshot = CreateSnapshot(viewport: (800, double.NaN));

        var result = ViewportConvergence.ValidateMeasurements(snapshot);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateMeasurements_NegativeOffset_ReturnsInvalid()
    {
        var snapshot = CreateSnapshot(offsets: (-1, 0));

        var result = ViewportConvergence.ValidateMeasurements(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains("Offsets", result.Reason);
    }

    [Fact]
    public void OffsetsAreClamped_ZeroOffsets_ReturnsTrue()
    {
        var snapshot = CreateSnapshot(offsets: (0, 0));
        Assert.True(ViewportConvergence.OffsetsAreClamped(snapshot));
    }

    [Fact]
    public void OffsetsAreClamped_SubpixelOffsets_ReturnsTrue()
    {
        var snapshot = CreateSnapshot(offsets: (0.3, 0.2)); // < Epsilon
        Assert.True(ViewportConvergence.OffsetsAreClamped(snapshot));
    }

    [Fact]
    public void OffsetsAreClamped_SignificantOffset_ReturnsFalse()
    {
        var snapshot = CreateSnapshot(offsets: (10, 0));
        Assert.False(ViewportConvergence.OffsetsAreClamped(snapshot));
    }

    [Fact]
    public void Convergence_ScrollbarDisappears_ViewportGrows()
    {
        // Scenario: zoom out → scrollbars disappear → viewport grows
        var beforeZoom = CreateSnapshot(
            viewport: (784, 576), // Reduced by scrollbar width (16 DIP)
            extent: (2000, 2000),
            scrollbarVis: (Visibility.Visible, Visibility.Visible));

        var afterConverge = CreateSnapshot(
            viewport: (800, 600), // Full viewport (no scrollbars)
            extent: (800, 600), // Image fits
            scrollbarVis: (Visibility.Collapsed, Visibility.Collapsed));

        // Should detect meaningful change
        Assert.True(ViewportConvergence.HasMeaningfulChange(beforeZoom, afterConverge));
        Assert.False(ViewportConvergence.IsStableViewport(beforeZoom, afterConverge));
    }

    // Helper to create test snapshots with sensible defaults
    private static ViewportSnapshot CreateSnapshot(
        (double, double)? viewport = null,
        (double, double)? extent = null,
        (double, double)? offsets = null,
        (Visibility, Visibility)? scrollbarVis = null,
        (double, double)? maxImage = null,
        (double, double)? actualImage = null)
    {
        var (vpWidth, vpHeight) = viewport ?? (800, 600);
        var (extWidth, extHeight) = extent ?? (800, 600);
        var (hOff, vOff) = offsets ?? (0, 0);
        var (hVis, vVis) = scrollbarVis ?? (Visibility.Collapsed, Visibility.Collapsed);
        var (maxW, maxH) = maxImage ?? (800, 600);
        var (actW, actH) = actualImage ?? (800, 600);

        return new ViewportSnapshot(
            Zoom: 1.0,
            Stretch: ViewerStretchMode.Uniform,
            MaxImageWidth: maxW,
            MaxImageHeight: maxH,
            ActualImageWidth: actW,
            ActualImageHeight: actH,
            ExtentWidth: extWidth,
            ExtentHeight: extHeight,
            ViewportWidth: vpWidth,
            ViewportHeight: vpHeight,
            HorizontalOffset: hOff,
            VerticalOffset: vOff,
            HorizontalScrollbarVisibility: hVis,
            VerticalScrollbarVisibility: vVis);
    }
}
