using System.Globalization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>The photo information line text: field toggles, skipped gaps, units from the catalog, display culture.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // switches the ambient Localizer
public sealed class ExifFormatterTests
{
    private static readonly ExifSummary Exif = new()
    {
        DateTaken = new DateTime(2024, 5, 1, 14, 3, 22),
        CameraMake = "Canon",
        CameraModel = "Canon EOS R5",
        LensModel = "RF24-70mm F2.8",
        Iso = 400,
        FocalLength = new ExifRational(50, 1),
        FNumber = new ExifRational(28, 10),
        ExposureTime = new ExifRational(1, 250),
    };

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static string Format(ExifInfoFields fields, ExifSummary? exif = null, int width = 6000, int height = 4000) =>
        ExifFormatter.Format(fields, "IMG_1234.jpg", width, height, exif ?? Exif, provider: Invariant);

    [Fact(DisplayName = "All fields, in order, English catalog")]
    public void AllFieldsEnglish()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        Assert.Equal(
            "IMG_1234.jpg · 05/01/2024 14:03 · 6000×4000 · Canon EOS R5 · RF24-70mm F2.8 · ISO 400 · 50 mm · f/2.8 · 1/250 s",
            Format(ExifInfoFields.All));
    }

    [Fact(DisplayName = "Units come from the language catalog (Vietnamese)")]
    public void UnitsAreLocalized()
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);

        Assert.EndsWith("ISO 400 · 50 mm · f/2.8 · 1/250 giây", Format(ExifInfoFields.All), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Each field alone")]
    [InlineData(ExifInfoFields.FileName, "IMG_1234.jpg")]
    [InlineData(ExifInfoFields.DateTaken, "05/01/2024 14:03")]
    [InlineData(ExifInfoFields.Dimensions, "6000×4000")]
    [InlineData(ExifInfoFields.Camera, "Canon EOS R5")]
    [InlineData(ExifInfoFields.Lens, "RF24-70mm F2.8")]
    [InlineData(ExifInfoFields.Iso, "ISO 400")]
    [InlineData(ExifInfoFields.FocalLength, "50 mm")]
    [InlineData(ExifInfoFields.Aperture, "f/2.8")]
    [InlineData(ExifInfoFields.ShutterSpeed, "1/250 s")]
    [InlineData(ExifInfoFields.Iso | ExifInfoFields.FocalLength | ExifInfoFields.Aperture | ExifInfoFields.ShutterSpeed,
        "ISO 400 · 50 mm · f/2.8 · 1/250 s")]
    [InlineData(ExifInfoFields.None, "")]
    public void EachFieldAlone(ExifInfoFields field, string expected)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        Assert.Equal(expected, Format(field));
    }

    [Theory(DisplayName = "Turning one field off removes exactly that part")]
    [InlineData(ExifInfoFields.FileName, "IMG_1234.jpg")]
    [InlineData(ExifInfoFields.DateTaken, "05/01/2024 14:03")]
    [InlineData(ExifInfoFields.Dimensions, "6000×4000")]
    [InlineData(ExifInfoFields.Camera, "Canon EOS R5")]
    [InlineData(ExifInfoFields.Lens, "RF24-70mm F2.8")]
    [InlineData(ExifInfoFields.Iso, "ISO 400")]
    [InlineData(ExifInfoFields.FocalLength, "50 mm")]
    [InlineData(ExifInfoFields.Aperture, "f/2.8")]
    [InlineData(ExifInfoFields.ShutterSpeed, "1/250 s")]
    public void EachFieldOff(ExifInfoFields field, string removedPart)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        var all = Format(ExifInfoFields.All);
        var without = Format(ExifInfoFields.All & ~field);

        Assert.Contains(removedPart, all, StringComparison.Ordinal);
        Assert.DoesNotContain(removedPart, without, StringComparison.Ordinal);
        Assert.Equal(all.Split(ExifFormatter.Separator).Length - 1, without.Split(ExifFormatter.Separator).Length);
    }

    [Fact(DisplayName = "Every field is a separate flag and All is exactly their union")]
    public void FlagsAreSeparate()
    {
        ExifInfoFields[] fields =
        [
            ExifInfoFields.FileName, ExifInfoFields.DateTaken, ExifInfoFields.ModifiedDate, ExifInfoFields.Dimensions, ExifInfoFields.Camera, ExifInfoFields.Lens,
            ExifInfoFields.Iso, ExifInfoFields.FocalLength, ExifInfoFields.Aperture, ExifInfoFields.ShutterSpeed,
        ];
        var union = ExifInfoFields.None;
        foreach (var field in fields)
        {
            Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)field));
            Assert.Equal(ExifInfoFields.None, union & field);
            union |= field;
        }
        Assert.Equal(ExifInfoFields.All, union);
        Assert.Equal(9, Format(ExifInfoFields.All).Split(ExifFormatter.Separator).Length);
    }

    [Fact(DisplayName = "Missing values are skipped, never shown as placeholders")]
    public void MissingValuesAreSkipped()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        Assert.Equal("IMG_1234.jpg · 6000×4000", ExifFormatter.Format(ExifInfoFields.All, "IMG_1234.jpg", 6000, 4000, null, provider: Invariant));
        Assert.Equal("IMG_1234.jpg", ExifFormatter.Format(ExifInfoFields.All, "IMG_1234.jpg", 0, 0, null, provider: Invariant));
        Assert.Equal("IMG_1234.jpg · ISO 100 · f/8",
            ExifFormatter.Format(ExifInfoFields.All, "IMG_1234.jpg", 0, 0, new ExifSummary { Iso = 100, FNumber = new ExifRational(8, 1) }, provider: Invariant));
    }

    [Theory(DisplayName = "Shutter speed: fractions below half a second, seconds above")]
    [InlineData(1u, 250u, "1/250 s")]
    [InlineData(10u, 2500u, "1/250 s")]
    [InlineData(1u, 3u, "1/3 s")]
    [InlineData(3u, 10u, "1/3 s")]
    [InlineData(1u, 2u, "1/2 s")]
    [InlineData(5u, 10u, "0.5 s")]
    [InlineData(13u, 10u, "1.3 s")]
    [InlineData(30u, 1u, "30 s")]
    public void ShutterSpeed(uint numerator, uint denominator, string expected)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        Assert.Equal(expected, ExifFormatter.ShutterText(new ExifRational(numerator, denominator), Invariant));
    }

    [Theory(DisplayName = "Camera make is not repeated when the model already names it")]
    [InlineData("Canon", "Canon EOS R5", "Canon EOS R5")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6_2", "NIKON Z 6_2")]
    [InlineData("SONY", "ILCE-7M3", "SONY ILCE-7M3")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("FUJIFILM", null, "FUJIFILM")]
    [InlineData(null, null, null)]
    public void CameraText(string? make, string? model, string? expected) =>
        Assert.Equal(expected, ExifFormatter.CameraText(make, model));

    [Fact(DisplayName = "Numbers use the display culture (decimal comma in Vietnamese)")]
    public void DisplayCultureNumbers()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var exif = new ExifSummary { FocalLength = new ExifRational(185, 10), FNumber = new ExifRational(28, 10) };

        Assert.Equal("18,5 mm · f/2,8", ExifFormatter.Format(ExifInfoFields.All, null, 0, 0, exif, provider: new CultureInfo("vi-VN")));
    }

    // ---- ModifiedDate (file last-write time, optional EXIF-line field, off by default; placed right after DateTaken) ----

    [Fact(DisplayName = "ModifiedDate is off by default, alongside FileName and Dimensions")]
    public void ModifiedDate_OffByDefault()
    {
        Assert.False(ExifInfoFields.Default.HasFlag(ExifInfoFields.ModifiedDate));
        Assert.True(ExifInfoFields.All.HasFlag(ExifInfoFields.ModifiedDate));
    }

    [Fact(DisplayName = "ModifiedDate renders right after DateTaken, converted to local time, formatted like DateTaken")]
    public void ModifiedDate_RendersAfterDateTakenInLocalTime()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var modifiedUtc = new DateTime(2024, 6, 15, 9, 30, 0, DateTimeKind.Utc);

        var text = ExifFormatter.Format(ExifInfoFields.DateTaken | ExifInfoFields.ModifiedDate, "a.jpg", 0, 0, Exif,
            modifiedUtc, Invariant);

        var expectedDateTaken = Exif.DateTaken!.Value.ToString("g", Invariant);
        var expectedModified = modifiedUtc.ToLocalTime().ToString("g", Invariant);
        Assert.Equal(expectedDateTaken + ExifFormatter.Separator + expectedModified, text);
    }

    [Fact(DisplayName = "ModifiedDate alone shows only the file's last-write time")]
    public void ModifiedDate_Alone()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var modifiedUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var text = ExifFormatter.Format(ExifInfoFields.ModifiedDate, "a.jpg", 0, 0, null, modifiedUtc, Invariant);

        Assert.Equal(modifiedUtc.ToLocalTime().ToString("g", Invariant), text);
    }

    [Fact(DisplayName = "ModifiedDate is skipped (not '?') when the catalog entry has no last-write time")]
    public void ModifiedDate_NullIsSkipped()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        var text = ExifFormatter.Format(ExifInfoFields.FileName | ExifInfoFields.ModifiedDate, "a.jpg", 0, 0, null, modifiedUtc: null, Invariant);

        Assert.Equal("a.jpg", text);
    }

    [Fact(DisplayName = "Turning ModifiedDate off removes exactly that part")]
    public void ModifiedDate_TurningOffRemovesExactlyThatPart()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var modifiedUtc = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var fields = ExifInfoFields.FileName | ExifInfoFields.ModifiedDate | ExifInfoFields.Camera;

        var withModified = ExifFormatter.Format(fields, "a.jpg", 0, 0, Exif, modifiedUtc, Invariant);
        var withoutModified = ExifFormatter.Format(fields & ~ExifInfoFields.ModifiedDate, "a.jpg", 0, 0, Exif, modifiedUtc, Invariant);

        Assert.Equal("a.jpg" + ExifFormatter.Separator + modifiedUtc.ToLocalTime().ToString("g", Invariant) + ExifFormatter.Separator + "Canon EOS R5", withModified);
        Assert.Equal("a.jpg" + ExifFormatter.Separator + "Canon EOS R5", withoutModified);
    }
}
