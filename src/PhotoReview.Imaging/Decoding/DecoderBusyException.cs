namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Thrown when a background (preload-priority) decode is refused because the decoder's bounded queue is full. The condition is
/// transient: the file is fine, so it must neither fall back to another decoder (that would bypass the gate that refused it)
/// nor be treated as a corrupt file; callers skip it now and retry later.
/// </summary>
public sealed class DecoderBusyException : Exception
{
    public DecoderBusyException() : base("The decoder is busy; this background decode was skipped.") { }

    public DecoderBusyException(string message) : base(message) { }

    public DecoderBusyException(string message, Exception innerException) : base(message, innerException) { }
}
