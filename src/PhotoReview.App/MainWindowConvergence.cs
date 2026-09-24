using System;
using System.Windows;
using System.Windows.Controls;
using PhotoReview.App.ViewModels;

namespace PhotoReview.App;

/// <summary>Immutable snapshot of viewport/layout state for convergence detection.</summary>
internal record ViewportSnapshot(
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
    Visibility HorizontalScrollbarVisibility,
    Visibility VerticalScrollbarVisibility)
{
    public override string ToString() =>
        $"Zoom={Zoom:F2} Stretch={Stretch} MaxImage=({MaxImageWidth:F0},{MaxImageHeight:F0}) " +
        $"Actual=({ActualImageWidth:F0},{ActualImageHeight:F0}) Extent=({ExtentWidth:F0},{ExtentHeight:F0}) " +
        $"Viewport=({ViewportWidth:F0},{ViewportHeight:F0}) Offset=({HorizontalOffset:F1},{VerticalOffset:F1}) " +
        $"Scrollbars=({HorizontalScrollbarVisibility},{VerticalScrollbarVisibility})";
}

/// <summary>Helper to detect viewport convergence and validate measurements.</summary>
internal static class ViewportConvergence
{
    private const double Epsilon = 0.5; // DIP tolerance for subpixel/DPI noise

    /// <summary>Check if two snapshots represent the same stable state (within epsilon).</summary>
    internal static bool IsStableViewport(ViewportSnapshot before, ViewportSnapshot after)
    {
        if (!ValidateMeasurements(after).IsValid)
            return false;

        return
            Math.Abs(before.ViewportWidth - after.ViewportWidth) < Epsilon &&
            Math.Abs(before.ViewportHeight - after.ViewportHeight) < Epsilon &&
            Math.Abs(before.ExtentWidth - after.ExtentWidth) < Epsilon &&
            Math.Abs(before.ExtentHeight - after.ExtentHeight) < Epsilon &&
            before.HorizontalScrollbarVisibility == after.HorizontalScrollbarVisibility &&
            before.VerticalScrollbarVisibility == after.VerticalScrollbarVisibility;
    }

    /// <summary>Check if a meaningful layout change occurred (viewport or scrollbar visibility changed).</summary>
    internal static bool HasMeaningfulChange(ViewportSnapshot before, ViewportSnapshot after)
    {
        if (!ValidateMeasurements(after).IsValid)
            return false;

        return !IsStableViewport(before, after);
    }

    /// <summary>Validate that measurements are finite, positive, and usable.</summary>
    internal static ValidationResult ValidateMeasurements(ViewportSnapshot snapshot)
    {
        if (!IsPositiveFinite(snapshot.ViewportWidth) || !IsPositiveFinite(snapshot.ViewportHeight))
            return ValidationResult.InvalidDimensions("Viewport not ready");

        if (!IsNonNegativeFinite(snapshot.HorizontalOffset) || !IsNonNegativeFinite(snapshot.VerticalOffset))
            return ValidationResult.InvalidDimensions("Offsets invalid");

        if (!IsNonNegativeFinite(snapshot.MaxImageWidth) || !IsNonNegativeFinite(snapshot.MaxImageHeight))
            return ValidationResult.InvalidDimensions("MaxImage not set");

        if (!IsNonNegativeFinite(snapshot.ActualImageWidth) || !IsNonNegativeFinite(snapshot.ActualImageHeight))
            return ValidationResult.InvalidDimensions("ActualImage invalid");

        return ValidationResult.Valid();
    }

    /// <summary>Check if offsets are clamped to zero (or nearly zero after Fit reset).</summary>
    internal static bool OffsetsAreClamped(ViewportSnapshot snapshot)
    {
        return snapshot.HorizontalOffset < Epsilon && snapshot.VerticalOffset < Epsilon;
    }

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);
    private static bool IsNonNegativeFinite(double value) => value >= 0 && double.IsFinite(value);
}

/// <summary>Result of viewport measurement validation.</summary>
internal record ValidationResult(bool IsValid, string? Reason)
{
    public static ValidationResult Valid() => new(true, null);
    public static ValidationResult InvalidDimensions(string reason) => new(false, reason);
}
