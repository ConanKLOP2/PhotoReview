using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Decoding;

/// <summary>
/// IMG-07: the shared marker walker must behave like the two hand-rolled loops it replaced (plus the IMG-01 fill/TEM fixes).
/// The pre-refactor ICC loop is kept here verbatim as the oracle; the orientation oracle locates the
/// first Exif APP1 payload with the old loop and asks the (unchanged) public API to parse it.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class JpegSegmentWalkerEquivalenceTests
{
    private static readonly byte[] IccTag = "ICC_PROFILE\0"u8.ToArray();

    [Fact]
    public void Walker_RandomAndCorruptedHeaders_MatchesPreRefactorLoops()
    {
        var rng = new Random(20260925);
        var iccHits = 0;
        var exifHits = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var jpeg = BuildRandomJpeg(rng);
            Assert.Equal(OldHasIcc(jpeg), TurboJpegDecoder.HasEmbeddedIccProfile(jpeg));
            Assert.Equal(OldOrientation(jpeg), TurboJpegDecoder.ReadExifOrientation(jpeg));
            if (OldHasIcc(jpeg)) iccHits++;
            if (OldOrientation(jpeg) != 1) exifHits++;
        }

        // The corpus must actually exercise the positive paths, otherwise equality is vacuous.
        Assert.True(iccHits > 500, $"ICC positives: {iccHits}");
        Assert.True(exifHits > 500, $"Exif orientation positives: {exifHits}");
    }

    [Fact]
    public void Walker_FindsIccAfterOtherSegments_AndStopsAtSos()
    {
        byte[] beforeSos = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, .. Segment(0xE2, IccTag), 0xFF, 0xDA, 0x00, 0x02];
        byte[] afterSos = [0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, .. Segment(0xE2, IccTag)];

        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(beforeSos));
        Assert.False(TurboJpegDecoder.HasEmbeddedIccProfile(afterSos));
    }

    [Fact]
    public void Walker_SkipsMarkerFillBytesBeforeExif()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, .. Segment(0xE1, ExifPayload(6)), 0xFF, 0xDA, 0x00, 0x02];

        Assert.Equal(6, TurboJpegDecoder.ReadExifOrientation(jpeg));
    }

    [Fact]
    public void Walker_SkipsMarkerFillBytesBeforeIcc()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xFF, .. Segment(0xE2, IccTag), 0xFF, 0xDA, 0x00, 0x02];

        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(jpeg));
    }

    [Fact]
    public void Walker_SkipsTemMarkerBeforeIccAndExif()
    {
        byte[] icc = [0xFF, 0xD8, 0xFF, 0x01, .. Segment(0xE2, IccTag), 0xFF, 0xDA, 0x00, 0x02];
        byte[] exif = [0xFF, 0xD8, 0xFF, 0x01, 0xFF, 0x01, .. Segment(0xE1, ExifPayload(3)), 0xFF, 0xDA, 0x00, 0x02];

        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(icc));
        Assert.Equal(3, TurboJpegDecoder.ReadExifOrientation(exif));
    }

    [Fact]
    public void Walker_NormalExifAndIcc_StillFound()
    {
        byte[] jpeg = [0xFF, 0xD8, .. Segment(0xE1, ExifPayload(8)), .. Segment(0xE2, IccTag), 0xFF, 0xDA, 0x00, 0x02];

        Assert.Equal(8, TurboJpegDecoder.ReadExifOrientation(jpeg));
        Assert.True(TurboJpegDecoder.HasEmbeddedIccProfile(jpeg));
    }

    [Fact]
    public void Walker_TruncatedFillOrStuffedByte_DoesNotThrowOrMatch()
    {
        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation([0xFF, 0xD8, 0xFF, 0xFF, 0xFF]));
        Assert.False(TurboJpegDecoder.HasEmbeddedIccProfile([0xFF, 0xD8, 0xFF, 0x00, 0x00, 0x00]));
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    private static byte[] ExifPayload(int orientation)
    {
        // "Exif\0\0" + little-endian TIFF with a single IFD0 entry (tag 0x0112 = Orientation).
        return
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
            1, 0,
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0,
        ];
    }

    private static byte[] BuildRandomJpeg(Random rng)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        var segments = rng.Next(0, 6);
        for (var s = 0; s < segments; s++)
        {
            switch (rng.Next(7))
            {
                case 0: bytes.AddRange(Segment(0xE2, IccTag)); break;
                case 1: bytes.AddRange(Segment(0xE1, ExifPayload(rng.Next(1, 9)))); break;
                case 2: bytes.AddRange(Segment(0xE0, RandomBytes(rng, rng.Next(0, 20)))); break;
                case 3: bytes.AddRange([0xFF, (byte)(0xD0 + rng.Next(8))]); break; // standalone RSTn
                case 4: bytes.AddRange(Segment(0xE2, [.. IccTag, .. RandomBytes(rng, rng.Next(0, 8))])); break;
                case 5: bytes.AddRange(Segment((byte)rng.Next(0xC0, 0xFF), RandomBytes(rng, rng.Next(0, 30)))); break;
                default: bytes.AddRange([0xFF, 0xDA, 0, 2]); break;
            }
        }

        // Corrupt: truncate and/or flip bytes so length/bounds edge cases are hit.
        if (rng.Next(3) == 0 && bytes.Count > 0) bytes[rng.Next(bytes.Count)] = (byte)rng.Next(256);
        if (rng.Next(4) == 0 && bytes.Count > 2) bytes[rng.Next(2, bytes.Count)] = 0xFF;
        var arr = bytes.ToArray();
        if (rng.Next(3) == 0) arr = arr[..rng.Next(0, arr.Length + 1)];
        return arr;
    }

    private static byte[] RandomBytes(Random rng, int count)
    {
        var b = new byte[count];
        rng.NextBytes(b);
        return b;
    }

    // ---- Oracles: the pre-refactor loops, copied verbatim -------------------------------------

    private static bool OldHasIcc(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;

        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) break;

            // IMG-01 deliberate deviation from the pre-refactor loop: skip 0xFF fill, stop at 0xFF00 and at EOI, skip TEM.
            while (offset + 1 < jpeg.Length && jpeg[offset + 1] == 0xFF) offset++;
            if (offset + 4 > jpeg.Length) break;
            byte marker = jpeg[offset + 1];
            if (marker is 0x00 or 0xFF or 0xD9) break;
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            if (marker == 0xDA) break;

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) break;

            if (marker == 0xE2)
            {
                var payload = jpeg.Slice(offset + 4, length - 2);
                if (payload.Length >= 12 &&
                    payload[0] == (byte)'I' && payload[1] == (byte)'C' && payload[2] == (byte)'C' &&
                    payload[3] == (byte)'_' && payload[4] == (byte)'P' && payload[5] == (byte)'R' &&
                    payload[6] == (byte)'O' && payload[7] == (byte)'F' && payload[8] == (byte)'I' &&
                    payload[9] == (byte)'L' && payload[10] == (byte)'E' && payload[11] == 0)
                {
                    return true;
                }
            }

            offset += 2 + length;
        }

        return false;
    }

    private static int OldOrientation(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;

        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            if (jpeg[offset] != 0xFF) break;

            // IMG-01 deliberate deviation from the pre-refactor loop: skip 0xFF fill, stop at 0xFF00 and at EOI, skip TEM.
            while (offset + 1 < jpeg.Length && jpeg[offset + 1] == 0xFF) offset++;
            if (offset + 4 > jpeg.Length) break;
            byte marker = jpeg[offset + 1];
            if (marker is 0x00 or 0xFF or 0xD9) break;
            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                offset += 2;
                continue;
            }
            if (marker == 0xDA) break;

            int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
            if (length < 2 || offset + 2 + length > jpeg.Length) break;

            if (marker == 0xE1)
            {
                var payload = jpeg.Slice(offset + 4, length - 2);
                if (payload.Length >= 6 &&
                    payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i' &&
                    payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
                {
                    // Old code returned ParseTiffOrientation(payload[6..]); re-run that on a minimal
                    // well-formed wrapper around just this payload (walker sees exactly one APP1).
                    byte[] wrapped = [0xFF, 0xD8, .. Segment(0xE1, payload.ToArray())];
                    return TurboJpegDecoder.ReadExifOrientation(wrapped);
                }
            }

            offset += 2 + length;
        }

        return 1;
    }
}
