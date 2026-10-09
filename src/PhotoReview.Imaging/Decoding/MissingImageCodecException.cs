namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// The file is a WebP/HEIC/HEIF image but this PC lacks the Windows codec for it (Q-FMT-WEBP-HEIC). A
/// <see cref="NotSupportedException"/>, so every caller that already treats "cannot decode this file" as a per-file failure
/// keeps doing so; it carries the localized install guidance (see <see cref="PhotoReview.Core.Localization.UserFacingError"/>).
/// Preload skips such files without a log line per file (<see cref="WebpHeicRoutingDecoder"/> logs the guidance once).
/// </summary>
public sealed class MissingImageCodecException : NotSupportedException
{
    public MissingImageCodecException() : base("The Windows codec for this image format is not installed.") { }

    public MissingImageCodecException(string message) : base(message) { }

    public MissingImageCodecException(string message, Exception innerException) : base(message, innerException) { }
}
