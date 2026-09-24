namespace PhotoReview.Imaging;

/// <summary>
/// Bounding box, in device pixels, that a preview is decoded into. The box applies to the
/// <em>displayed</em> image, i.e. after EXIF orientation: a portrait shot stored as a rotated
/// landscape (orientations 5-8) is fitted with its rotated dimensions. A value of 0 (or less) on an
/// axis leaves that axis unconstrained; <see cref="Unbounded"/> (both 0) means "full size".
/// Fitting never upscales and always preserves the aspect ratio.
/// </summary>
public readonly record struct DecodeBox(int Width, int Height)
{
    /// <summary>No constraint: decode at full resolution.</summary>
    public static DecodeBox Unbounded => default;

    public bool IsUnbounded => Width <= 0 && Height <= 0;

    /// <summary>
    /// Largest size inside this box with the aspect ratio of <paramref name="sourceWidth"/> x
    /// <paramref name="sourceHeight"/> (the displayed, orientation-applied dimensions). Returns the
    /// source size unchanged when it already fits or the box is unbounded (no upscaling). The
    /// constraining side gets exactly the box dimension; the other side is floored (never below 1).
    /// </summary>
    public (int Width, int Height) Fit(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || IsUnbounded) return (sourceWidth, sourceHeight);
        var widthExceeds = Width > 0 && sourceWidth > Width;
        var heightExceeds = Height > 0 && sourceHeight > Height;
        if (!widthExceeds && !heightExceeds) return (sourceWidth, sourceHeight);

        // Width constrains when Width/sourceWidth <= Height/sourceHeight (cross-multiplied, exact).
        var widthConstrains = Height <= 0 || (Width > 0 && (long)Width * sourceHeight <= (long)Height * sourceWidth);
        return widthConstrains
            ? (Width, (int)Math.Max(1, (long)sourceHeight * Width / sourceWidth))
            : ((int)Math.Max(1, (long)sourceWidth * Height / sourceHeight), Height);
    }

    /// <summary>
    /// Target size in the <em>stored</em> (pre-orientation) pixel grid for a frame of
    /// <paramref name="rawWidth"/> x <paramref name="rawHeight"/>: when <paramref name="transposed"/>
    /// (EXIF 5-8) the box is fitted against the rotated dimensions and the result swapped back, so a
    /// decoder can scale first and rotate afterwards.
    /// </summary>
    public (int Width, int Height) FitStored(int rawWidth, int rawHeight, bool transposed)
    {
        if (!transposed) return Fit(rawWidth, rawHeight);
        var (displayedWidth, displayedHeight) = Fit(rawHeight, rawWidth);
        return (displayedHeight, displayedWidth);
    }
}
