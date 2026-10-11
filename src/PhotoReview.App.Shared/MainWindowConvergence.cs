using PhotoReview.App.Viewport;

namespace PhotoReview.App;

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
            before.HorizontalScrollBarVisible == after.HorizontalScrollBarVisible &&
            before.VerticalScrollBarVisible == after.VerticalScrollBarVisible;
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

    private static bool IsPositiveFinite(double value) => value > 0 && double.IsFinite(value);
    private static bool IsNonNegativeFinite(double value) => value >= 0 && double.IsFinite(value);
}

/// <summary>Result of viewport measurement validation.</summary>
internal record ValidationResult(bool IsValid, string? Reason)
{
    public static ValidationResult Valid() => new(true, null);
    public static ValidationResult InvalidDimensions(string reason) => new(false, reason);
}
