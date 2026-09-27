namespace PhotoReview.Imaging.Tests.Fixtures;

/// <summary>
/// A JPEG that is well-framed (valid SOI/EOI, right dimensions) but carries a damaged EXIF (APP1) segment. WPF decodes such a
/// file's pixels fine but fails in BitmapDecoder/EndInit with <see cref="ArgumentException"/> -- an exception type that the
/// disk-cache read paths must treat as "corrupt entry" just like <see cref="System.IO.FileFormatException"/>.
/// </summary>
internal static class DamagedJpegFixture
{
    /// <param name="bytes">Buffer that contains a JPEG.</param>
    /// <param name="soiOffset">Offset of the JPEG's SOI marker (FF D8) inside <paramref name="bytes"/>.</param>
    public static byte[] WithBrokenExifSegment(byte[] bytes, int soiOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes[soiOffset] != 0xFF || bytes[soiOffset + 1] != 0xD8) throw new ArgumentException("No JPEG SOI at the given offset.", nameof(soiOffset));

        var body = new byte[200];
        for (var i = 0; i < body.Length; i++) body[i] = (byte)(i * 37 + 11);
        "Exif\0\0MM\0*"u8.CopyTo(body); // a TIFF header that is cut off and followed by junk
        var length = body.Length + 2;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF), .. body];
        return [.. bytes.AsSpan(0, soiOffset + 2), .. segment, .. bytes.AsSpan(soiOffset + 2)];
    }
}
