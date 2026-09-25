using System.Text;
using PhotoReview.Imaging.Metadata;
using static PhotoReview.Imaging.Tests.Metadata.ExifTestData;

namespace PhotoReview.Imaging.Tests.Metadata;

/// <summary>
/// Random valid EXIF (either byte order, shuffled tag order, unknown tags mixed in, inline and out-of-line values, SHORT or LONG
/// ISO) is built with the test TIFF writer and must parse to exactly what ExifSummary.Create makes of the same raw values.
/// </summary>
[Trait("Category", "HotPath")]
public sealed class ExifParserRoundTripPropertyTests
{
    private static string RandomAscii(Random rng, int maxLength)
    {
        var length = rng.Next(0, maxLength + 1);
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)rng.Next(0x20, 0x7F);
        return new string(chars);
    }

    private static string RandomDate(Random rng) =>
        $"{rng.Next(1990, 2031):D4}:{rng.Next(1, 13):D2}:{rng.Next(1, 29):D2} {rng.Next(0, 24):D2}:{rng.Next(0, 60):D2}:{rng.Next(0, 60):D2}";

    private static void Shuffle<T>(Random rng, List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    [Fact(DisplayName = "Random valid EXIF blocks parse to exactly the sanitised raw values (both byte orders, any tag order, unknown tags mixed in)")]
    public void RandomValidExif_RoundTrips()
    {
        var rng = new Random(0xE1F);
        var parsedFields = 0;
        for (var i = 0; i < 6_000; i++)
        {
            var little = rng.Next(2) == 0;
            var make = rng.Next(4) == 0 ? null : RandomAscii(rng, 100);
            var model = rng.Next(4) == 0 ? null : RandomAscii(rng, 100);
            var lens = rng.Next(4) == 0 ? null : RandomAscii(rng, 100);
            var dateOriginal = rng.Next(3) == 0 ? null : RandomDate(rng);
            var dateTime = rng.Next(3) == 0 ? null : RandomDate(rng);
            long? iso = rng.Next(3) == 0 ? null : rng.Next(1, 3_000_000);
            ExifRational? Rat() => rng.Next(3) == 0 ? null : new ExifRational((uint)rng.Next(1, 100_000), (uint)rng.Next(1, 100_000));
            var focal = Rat();
            var fNumber = Rat();
            var exposure = Rat();

            var ifd0 = new List<Entry>();
            var exif = new List<Entry>();
            if (make is { Length: > 0 }) ifd0.Add(Ascii(ExifParser.TagMake, make));
            if (model is { Length: > 0 }) ifd0.Add(Ascii(ExifParser.TagModel, model));
            if (dateTime is not null) ifd0.Add(Ascii(ExifParser.TagDateTime, dateTime));
            ifd0.Add(new Entry(0x0112, 3, 1, U16((ushort)rng.Next(1, 9), little))); // orientation: unknown to the summary
            ifd0.Add(new Entry(0x013B, 2, 5, [.. "Anna"u8.ToArray(), 0]));           // Artist: unknown to the summary
            if (lens is { Length: > 0 }) exif.Add(Ascii(ExifParser.TagLensModel, lens));
            if (dateOriginal is not null) exif.Add(Ascii(ExifParser.TagDateTimeOriginal, dateOriginal));
            if (iso is { } isoValue)
                exif.Add(isoValue <= ushort.MaxValue ? Short(ExifParser.TagIso, (ushort)isoValue, little) : new Entry(ExifParser.TagIso, 4, 1, U32((uint)isoValue, little)));
            if (focal is { } f) exif.Add(Rational(ExifParser.TagFocalLength, f.Numerator, f.Denominator, little));
            if (fNumber is { } n) exif.Add(Rational(ExifParser.TagFNumber, n.Numerator, n.Denominator, little));
            if (exposure is { } e) exif.Add(Rational(ExifParser.TagExposureTime, e.Numerator, e.Denominator, little));
            exif.Add(new Entry(0x9204, 3, 1, U16(0, little))); // ExposureBias: unknown to the summary
            if (exif.Count == 1) exif.Clear(); // only the unknown tag: no Exif sub-IFD at all
            Shuffle(rng, ifd0);
            Shuffle(rng, exif);

            var jpeg = JpegWithApp1(Tiff(little, ifd0, exif));
            var actual = ExifParser.TryParseJpeg(jpeg);

            var expected = ExifSummary.Create(dateOriginal ?? dateTime, make, model, lens, iso, focal, fNumber, exposure);
            Assert.True(Equals(expected, actual), $"iteration {i} little={little}: expected {expected}, got {actual}");
            if (actual is not null) parsedFields++;
        }

        Assert.True(parsedFields > 5_000, $"only {parsedFields} iterations produced a summary");
    }
}
