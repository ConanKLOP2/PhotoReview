namespace PhotoReview.Imaging.Tests;

/// <summary>
/// ICC-presence probe for tests: the production walk (<see cref="TurboJpegDecoder.ScanHeader"/>) asked only for ICC,
/// exactly as the former public <c>TurboJpegDecoder.HasEmbeddedIccProfile</c> did before it became test-only.
/// </summary>
internal static class JpegIccProbe
{
    public static bool HasIcc(ReadOnlySpan<byte> jpeg)
    {
        TurboJpegDecoder.ScanHeader(jpeg, wantIcc: true, wantExif: false, out var hasIcc, out _);
        return hasIcc;
    }
}