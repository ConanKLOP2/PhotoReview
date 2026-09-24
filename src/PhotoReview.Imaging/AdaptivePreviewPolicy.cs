namespace PhotoReview.Imaging;

/// <summary>Calculates the bounded preview decode size from the viewport and display DPI.</summary>
public static class AdaptivePreviewPolicy
{
    public const int MinimumDecodeWidth = 1200;
    public const int MaximumDecodeWidth = 4000;

    /// <summary>The box's long side is scaled up (keeping the viewport's aspect) to at least this.</summary>
    public const int MinimumBoxLongSide = 1200;

    /// <summary>Each box side is capped here (applied after quantization).</summary>
    public const int MaximumBoxSide = 4000;

    /// <summary>
    /// Box sides are rounded <em>up</em> to a multiple of this, so resizing the window by less than
    /// a step keeps the same cache keys (no RAM/disk cache thrash) while a clearly bigger window
    /// still yields bigger decodes.
    /// </summary>
    public const int BoxQuantum = 128;

    public static int CalculateTargetDecodeWidth(double viewportWidth, double dpiScale = 1.0, double qualityMultiplier = 1.0)
    {
        Validate(viewportWidth, nameof(viewportWidth));
        Validate(dpiScale, nameof(dpiScale));
        Validate(qualityMultiplier, nameof(qualityMultiplier));
        var requested = viewportWidth * dpiScale * qualityMultiplier;
        return (int)Math.Clamp(Math.Round(requested, MidpointRounding.AwayFromZero), MinimumDecodeWidth, MaximumDecodeWidth);
    }

    /// <summary>
    /// Preview decode box (device pixels) for a viewport of <paramref name="viewportWidth"/> x
    /// <paramref name="viewportHeight"/> DIPs: both sides are multiplied by DPI and quality, the long
    /// side is raised to <see cref="MinimumBoxLongSide"/> if needed, each side is rounded up to a
    /// multiple of <see cref="BoxQuantum"/> and capped at <see cref="MaximumBoxSide"/>. Images are
    /// then decoded to the largest size inside this box (see <see cref="DecodeBox.Fit"/>).
    /// </summary>
    public static DecodeBox CalculateTargetDecodeBox(double viewportWidth, double viewportHeight, double dpiScale = 1.0, double qualityMultiplier = 1.0)
    {
        Validate(viewportWidth, nameof(viewportWidth));
        Validate(viewportHeight, nameof(viewportHeight));
        Validate(dpiScale, nameof(dpiScale));
        Validate(qualityMultiplier, nameof(qualityMultiplier));
        var width = viewportWidth * dpiScale * qualityMultiplier;
        var height = viewportHeight * dpiScale * qualityMultiplier;
        var longSide = Math.Max(width, height);
        if (longSide < MinimumBoxLongSide)
        {
            var scale = MinimumBoxLongSide / longSide;
            width *= scale;
            height *= scale;
        }

        return new DecodeBox(Quantize(width), Quantize(height));
    }

    /// <summary>Rounds up to a multiple of <see cref="BoxQuantum"/>, then caps at <see cref="MaximumBoxSide"/>.</summary>
    public static int Quantize(double side)
    {
        // Subtract a hair before Ceiling so floating-point noise (e.g. 1280.0000001) does not
        // jump a whole quantum.
        var steps = Math.Max(1, Math.Ceiling(side / BoxQuantum - 1e-9));
        return (int)Math.Min(steps * BoxQuantum, MaximumBoxSide);
    }

    private static void Validate(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
