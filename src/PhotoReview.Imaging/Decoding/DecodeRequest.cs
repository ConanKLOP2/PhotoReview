namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Request parameters for decoding an image.
/// </summary>
/// <param name="Path">Absolute path to the image file.</param>
/// <param name="TargetWidth">
/// Width of the decode box (displayed, orientation-applied pixels). 0 or negative leaves the width
/// unconstrained; when both <paramref name="TargetWidth"/> and <paramref name="TargetHeight"/> are
/// unconstrained the full original resolution is decoded.
/// </param>
/// <param name="ApplyOrientation">Whether to apply EXIF orientation transformation (enabled in T83).</param>
/// <param name="Bytes">Optional pre-read raw image bytes. If provided, decoders may decode directly from memory.</param>
/// <param name="TargetHeight">
/// Height of the decode box (displayed pixels); 0 = unconstrained (width-only, the pre-box behaviour).
/// Decoders scale to the largest size inside the box, preserving aspect, never upscaling
/// (see <see cref="DecodeBox.Fit"/>).
/// </param>
public readonly record struct DecodeRequest(
    string Path,
    int TargetWidth,
    bool ApplyOrientation = true,
    ReadOnlyMemory<byte>? Bytes = null,
    int TargetHeight = 0)
{
    /// <summary>Builds a request that decodes into <paramref name="box"/>.</summary>
    public DecodeRequest(string path, DecodeBox box, bool applyOrientation = true, ReadOnlyMemory<byte>? bytes = null)
        : this(path, box.Width, applyOrientation, bytes, box.Height) { }

    public DecodeBox Box => new(TargetWidth, TargetHeight);

    /// <summary>True when any axis is constrained, i.e. the caller asked for a downscaled decode.</summary>
    public bool IsDownscaleRequested => TargetWidth > 0 || TargetHeight > 0;
}
