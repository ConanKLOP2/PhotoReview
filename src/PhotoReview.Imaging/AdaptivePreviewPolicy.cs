namespace PhotoReview.Imaging;

/// <summary>Calculates a bounded decode width from viewport and display DPI.</summary>
public static class AdaptivePreviewPolicy
{
    public const int MinimumDecodeWidth = 1200;
    public const int MaximumDecodeWidth = 4000;

    public static int CalculateTargetDecodeWidth(double viewportWidth, double dpiScale = 1.0, double qualityMultiplier = 1.0)
    {
        Validate(viewportWidth, nameof(viewportWidth));
        Validate(dpiScale, nameof(dpiScale));
        Validate(qualityMultiplier, nameof(qualityMultiplier));
        var requested = viewportWidth * dpiScale * qualityMultiplier;
        return (int)Math.Clamp(Math.Round(requested, MidpointRounding.AwayFromZero), MinimumDecodeWidth, MaximumDecodeWidth);
    }

    private static void Validate(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
