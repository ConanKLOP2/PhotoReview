using PhotoReview.Imaging.Metadata;
using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.Imaging.Tests.Robustness;

/// <summary>
/// Three production code paths walk the JPEG marker area independently (TurboJpegDecoder ICC + orientation, and
/// ExifParser.FindExifTiffBlock). Each is compared against one reference oracle written straight from ITU T.81 B.1.1.2
/// (fill bytes, stuffed 0xFF00, parameterless markers, EOI/SOS end the header area) over generated and mutated headers.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class JpegHeaderOracleTests
{
    private readonly record struct Expected(bool HasIcc, byte[]? ExifTiff);

    /// <summary>Reference walker: first Exif-headed APP1 wins (even if its TIFF is empty/garbage); any ICC APP2 counts.</summary>
    private static Expected Oracle(byte[] j)
    {
        if (j.Length < 4 || j[0] != 0xFF || j[1] != 0xD8) return new(false, null);
        var hasIcc = false;
        byte[]? exif = null;
        var i = 2;
        while (i < j.Length)
        {
            if (j[i] != 0xFF) break;
            while (i + 1 < j.Length && j[i + 1] == 0xFF) i++;
            if (i + 1 >= j.Length) break;
            var m = j[i + 1];
            i += 2;
            if (m == 0x00) break;                       // stuffed byte, not a marker
            if (m is 0xD9 or 0xDA) break;               // EOI / SOS: header area is over
            if (m == 0x01 || m == 0xD8 || (m >= 0xD0 && m <= 0xD7)) continue; // no length field
            if (i + 2 > j.Length) break;
            var len = (j[i] << 8) | j[i + 1];
            if (len < 2 || i + len > j.Length) break;
            var payload = j.AsSpan(i + 2, len - 2);
            i += len;
            if (m == 0xE2 && payload.Length >= 12 && payload[..12].SequenceEqual(JpegBytes.IccTag)) hasIcc = true;
            if (m == 0xE1 && exif is null && payload.Length >= 6 && payload[..6].SequenceEqual(JpegBytes.ExifTag)) exif = payload[6..].ToArray();
        }
        return new(hasIcc, exif);
    }

    /// <summary>Reference orientation: IFD0 entries in order, tag 0x0112 with a valid 1..8 value in the SHORT position.</summary>
    private static int OracleOrientation(byte[]? tiff)
    {
        if (tiff is null || tiff.Length < 8) return 1;
        bool le;
        if (tiff[0] == 'I' && tiff[1] == 'I') le = true; else if (tiff[0] == 'M' && tiff[1] == 'M') le = false; else return 1;
        int U16(int p) => p + 2 > tiff.Length ? 0 : le ? tiff[p] | (tiff[p + 1] << 8) : (tiff[p] << 8) | tiff[p + 1];
        long U32(int p) => p + 4 > tiff.Length ? 0 : le
            ? (uint)(tiff[p] | (tiff[p + 1] << 8) | (tiff[p + 2] << 16) | (tiff[p + 3] << 24))
            : (uint)((tiff[p] << 24) | (tiff[p + 1] << 16) | (tiff[p + 2] << 8) | tiff[p + 3]);
        if (U16(2) != 42) return 1;
        var ifd = U32(4);
        if (ifd >= tiff.Length) return 1;
        var count = U16((int)ifd);
        for (var n = 0; n < count; n++)
        {
            var e = (int)ifd + 2 + n * 12;
            if (e + 12 > tiff.Length) break;
            if (U16(e) == 0x0112)
            {
                var v = U16(e + 8);
                if (v is >= 1 and <= 8) return v;
            }
        }
        return 1;
    }

    private static byte[] BuildHeader(Random rng)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        var items = rng.Next(0, 8);
        for (var s = 0; s < items; s++)
        {
            switch (rng.Next(14))
            {
                case 0: bytes.AddRange(JpegBytes.Segment(0xE2, JpegBytes.IccTag)); break;
                case 1: bytes.AddRange(JpegBytes.Segment(0xE2, [.. JpegBytes.IccTag, .. JpegBytes.Bytes(rng, rng.Next(0, 9))])); break;
                case 2: bytes.AddRange(JpegBytes.ExifApp1(JpegBytes.OrientationTiff(rng.Next(2) == 0, rng.Next(0, 11)))); break;
                case 3: bytes.AddRange(JpegBytes.Segment(0xE1, [.. JpegBytes.ExifTag, .. JpegBytes.Bytes(rng, rng.Next(0, 10))])); break; // tiny/garbage TIFF
                case 4: bytes.AddRange(JpegBytes.Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8.ToArray())); break;
                case 5: bytes.AddRange(JpegBytes.Segment(0xE0, JpegBytes.Bytes(rng, rng.Next(0, 20)))); break;
                case 6: bytes.AddRange([0xFF, (byte)(0xD0 + rng.Next(8))]); break;
                case 7: bytes.AddRange([0xFF, 0x01]); break;                       // TEM
                case 8: bytes.Add(0xFF); bytes.Add(0xFF); break;                   // fill
                case 9: bytes.AddRange([0xFF, 0x00]); break;                       // stuffed byte in the header area
                case 10: bytes.AddRange([0xFF, 0xD9]); break;                      // EOI
                case 11: bytes.AddRange([0xFF, 0xD8]); break;                      // duplicate SOI
                case 12: bytes.AddRange(JpegBytes.Segment((byte)rng.Next(0xC0, 0xFF), JpegBytes.Bytes(rng, rng.Next(0, 30)))); break;
                default: bytes.AddRange([0xFF, 0xDA, 0, 2]); break;
            }
        }
        return [.. bytes];
    }

    [Fact(DisplayName = "ICC detection, Exif block location and orientation agree with the T.81 reference walker on 60,000 generated/mutated headers")]
    public void AllWalkers_AgreeWithReferenceOracle()
    {
        var rng = new Random(20260926);
        var iccHits = 0;
        var exifHits = 0;
        var orientationHits = 0;
        for (var i = 0; i < 60_000; i++)
        {
            var jpeg = BuildHeader(rng);
            if (rng.Next(2) == 0) jpeg = JpegBytes.Mutate(rng, jpeg);
            var expected = Oracle(jpeg);
            var context = $"iteration {i}: {Convert.ToHexString(jpeg)}";

            Assert.True(expected.HasIcc == TurboJpegDecoder.HasEmbeddedIccProfile(jpeg), "ICC mismatch, " + context);

            var found = ExifParser.FindExifTiffBlock(jpeg);
            if (expected.ExifTiff is null) Assert.True(found.IsEmpty, "ExifParser found a block the oracle did not, " + context);
            else Assert.True(found.SequenceEqual(expected.ExifTiff), "ExifParser block mismatch, " + context);

            var orientation = OracleOrientation(expected.ExifTiff);
            Assert.True(orientation == TurboJpegDecoder.ReadExifOrientation(jpeg), "orientation mismatch, " + context);

            if (expected.HasIcc) iccHits++;
            if (expected.ExifTiff is not null) exifHits++;
            if (orientation != 1) orientationHits++;
        }

        // Vacuity guard: the corpus must reach every positive path many times.
        Assert.True(iccHits > 2_000, $"ICC positives: {iccHits}");
        Assert.True(exifHits > 2_000, $"Exif block positives: {exifHits}");
        Assert.True(orientationHits > 1_000, $"orientation positives: {orientationHits}");
    }

    [Fact(DisplayName = "Nothing after EOI or after a stuffed 0xFF00 in the header area is ever read as ICC or EXIF")]
    public void HeaderAreaEnds_AtEoiAndStuffedByte()
    {
        byte[] afterEoi = [0xFF, 0xD8, 0xFF, 0xD9, .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(true, 6)), .. JpegBytes.Segment(0xE2, JpegBytes.IccTag)];
        byte[] afterStuffed = [0xFF, 0xD8, 0xFF, 0x00, .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(true, 6)), .. JpegBytes.Segment(0xE2, JpegBytes.IccTag)];

        foreach (var jpeg in new[] { afterEoi, afterStuffed })
        {
            Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(jpeg));
            Assert.False(TurboJpegDecoder.HasEmbeddedIccProfile(jpeg));
            Assert.True(ExifParser.FindExifTiffBlock(jpeg).IsEmpty);
        }
    }

    [Fact(DisplayName = "A first Exif APP1 that is too short to hold a TIFF header is the one that counts: a later valid APP1 does not override it")]
    public void FirstExifApp1Wins_EvenWhenTooShort()
    {
        byte[] jpeg = [0xFF, 0xD8, .. JpegBytes.Segment(0xE1, [.. JpegBytes.ExifTag, 1, 2, 3]), .. JpegBytes.ExifApp1(JpegBytes.OrientationTiff(true, 6)), 0xFF, 0xDA, 0, 2];

        Assert.Equal(1, TurboJpegDecoder.ReadExifOrientation(jpeg));
        Assert.Equal(3, ExifParser.FindExifTiffBlock(jpeg).Length);
    }
}
