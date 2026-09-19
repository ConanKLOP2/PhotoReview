namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Request parameters for decoding an image.
/// </summary>
/// <param name="Path">Absolute path to the image file.</param>
/// <param name="TargetWidth">Desired decode pixel width. If 0 or negative, full original resolution is decoded.</param>
/// <param name="ApplyOrientation">Whether to apply EXIF orientation transformation (enabled in T83).</param>
/// <param name="Bytes">Optional pre-read raw image bytes. If provided, decoders may decode directly from memory.</param>
public readonly record struct DecodeRequest(
    string Path,
    int TargetWidth,
    bool ApplyOrientation = true,
    ReadOnlyMemory<byte>? Bytes = null);
