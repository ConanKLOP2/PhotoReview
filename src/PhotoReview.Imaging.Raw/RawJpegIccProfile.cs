using System.IO;
using System.Reflection;

namespace PhotoReview.Imaging.Raw;

/// <summary>Supplies the decided CC0 Adobe-compatible source profile when a RAW preview is tagged R03 but has no ICC.</summary>
internal static class RawJpegIccProfile
{
    private static readonly Lazy<byte[]> s_adobeRgbProfile = new(LoadProfile, LazyThreadSafetyMode.ExecutionAndPublication);
    private static ReadOnlySpan<byte> IccIdentifier => "ICC_PROFILE\0"u8;

    internal static byte[] EnsureAdobeRgbProfile(byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        if (HasEmbeddedIcc(jpeg)) return jpeg;
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Adobe RGB preview is not a valid JPEG stream.");

        var profile = s_adobeRgbProfile.Value;
        var segmentLength = checked(16 + profile.Length); // length field + ICC identifier + sequence/count + payload
        if (segmentLength > ushort.MaxValue) throw new InvalidDataException("Adobe RGB ICC profile exceeds JPEG APP2 limits.");
        var result = new byte[checked(jpeg.Length + 4 + 12 + 2 + profile.Length)];
        result[0] = 0xFF; result[1] = 0xD8;
        result[2] = 0xFF; result[3] = 0xE2;
        result[4] = (byte)(segmentLength >> 8); result[5] = (byte)segmentLength;
        IccIdentifier.CopyTo(result.AsSpan(6, 12));
        result[18] = 1; result[19] = 1;
        profile.CopyTo(result, 20);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(20 + profile.Length));
        return result;
    }

    internal static byte[] GetBundledAdobeRgbProfile() => (byte[])s_adobeRgbProfile.Value.Clone();

    private static bool HasEmbeddedIcc(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        var offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) return false;
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset >= jpeg.Length) return false;
            var marker = jpeg[offset++];
            if (marker is 0xDA or 0xD9) return false;
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;
            if (offset + 2 > jpeg.Length) return false;
            var length = (jpeg[offset] << 8) | jpeg[offset + 1];
            if (length < 2 || offset + length > jpeg.Length) return false;
            var payload = jpeg.Slice(offset + 2, length - 2);
            if (marker == 0xE2 && payload.Length >= 12 && payload[..12].SequenceEqual(IccIdentifier)) return true;
            offset += length;
        }
        return false;
    }

    private static byte[] LoadProfile()
    {
        using var stream = typeof(RawJpegIccProfile).Assembly.GetManifestResourceStream("PhotoReview.Imaging.Raw.AdobeCompat-v2.icc")
            ?? throw new InvalidOperationException("Bundled Adobe-compatible ICC profile is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
