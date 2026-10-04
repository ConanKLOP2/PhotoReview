using System.IO;

namespace PhotoReview.Imaging.Decoding;

/// <summary>
/// Thrown when a decoder's upfront memory admission refuses an output the machine cannot hold. The file is not corrupt, so the
/// refusal must reach the caller instead of being handed to another backend: the WPF fallback has no equivalent admission and
/// would allocate the very surface that was refused. It derives from <see cref="IOException"/> (InvalidDataException is sealed) so the existing handlers that
/// treat a failed read/decode as "cannot show this image" (e.g. the viewer's dimension lookup) keep working; <see cref="FallbackImageDecoder.IsFallbackable"/> excludes it
/// explicitly, before the whitelist.
/// </summary>
public sealed class DecoderMemoryAdmissionException : IOException
{
    public DecoderMemoryAdmissionException() : base("The decode was refused: not enough memory for the output.") { }

    public DecoderMemoryAdmissionException(string message) : base(message) { }

    public DecoderMemoryAdmissionException(string message, Exception innerException) : base(message, innerException) { }
}
