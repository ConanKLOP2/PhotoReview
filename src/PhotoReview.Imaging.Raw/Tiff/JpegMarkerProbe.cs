using System.IO;

namespace PhotoReview.Imaging.Raw.Tiff;

/// <summary>
/// Walks JPEG marker segments inside a candidate embedded-preview byte range to tell decodable lossy JPEG
/// (SOF0/1/2) apart from lossless JPEG (SOF3) sensor data, which shares TIFF Compression=7 with real previews.
/// </summary>
internal static class JpegMarkerProbe
{
    /// <summary>
    /// True when the range starts with SOI and its first frame header is baseline, extended sequential or
    /// progressive (SOF0/1/2). Lossless (SOF3), arithmetic/hierarchical frames and truncated data return false.
    /// </summary>
    public static bool TryReadLossyFrame(IRawHeaderSource source, long offset, long length, out int width, out int height)
    {
        try
        {
            return WalkLossyFrame(source, offset, length, out width, out height);
        }
        catch (InvalidDataException ex) when (!RawHeaderErrors.IsIoFailure(ex))
        {
            // Exhausted header budget: not a usable preview range, but the container must still parse.
            width = 0;
            height = 0;
            return false;
        }
    }

    private static bool WalkLossyFrame(IRawHeaderSource source, long offset, long length, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (length < 4 || offset < 0 || offset > source.Length - length) return false;

        long end = offset + length;
        if (source.Read(offset, 2) is not [0xFF, 0xD8]) return false;

        long pos = offset + 2;
        int fillBytes = 0;
        for (int segment = 0; segment < RawContainerLimits.MaxJpegMarkerSegments && pos + 4 <= end; segment++)
        {
            var head = source.Read(pos, 4);
            if (head[0] != 0xFF) return false;

            byte marker = head[1];
            if (marker == 0xFF)
            {
                // Fill byte: does not consume a segment iteration (only the separate fill cap bounds it).
                if (++fillBytes > RawContainerLimits.MaxJpegFillBytes) return false;
                pos++;
                segment--;
                continue;
            }
            fillBytes = 0;
            if (marker == 0x00 || marker is 0xDA or 0xD9) return false; // stuffed / SOS / EOI before any frame header
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD8) { pos += 2; continue; } // standalone markers

            int segmentLength = (head[2] << 8) | head[3];
            if (segmentLength < 2) return false;

            // SOF markers are C0..CF except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (marker > 0xC2 || segmentLength < 8 || pos + 9 > end) return false;

                var frame = source.Read(pos + 4, 5); // precision, height (2), width (2)
                if (frame[0] != 8) return false; // 12-bit (SOF1) and other precisions cannot be decoded as a preview
                height = (frame[1] << 8) | frame[2];
                width = (frame[3] << 8) | frame[4];
                return width > 0 && height > 0;
            }

            pos += 2 + segmentLength;
        }

        return false;
    }
}
