using System.IO;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.Imaging.Tests.Raw;

/// <summary>Reads the bundled Adobe-compatible ICC profile straight from the Raw assembly resource, for byte-exact assertions.</summary>
internal static class BundledAdobeProfile
{
    internal static byte[] Load()
    {
        using var stream = typeof(RawJpegIccProfile).Assembly.GetManifestResourceStream("PhotoReview.Imaging.Raw.AdobeCompat-v2.icc")
            ?? throw new InvalidOperationException("Bundled Adobe-compatible ICC profile is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
