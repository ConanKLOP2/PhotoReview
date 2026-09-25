using System;
using System.Globalization;
using System.Linq;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>Photo information line with missing, odd or hostile metadata, in both UI languages and any display culture.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // switches the ambient Localizer
public sealed class ExifFormatterEdgeTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static TheoryData<bool> Languages => [true, false]; // true = English, false = Vietnamese

    private static IDisposable Use(bool english) =>
        TestLocalization.Use(english ? TestLocalization.English : TestLocalization.Vietnamese);

    [Theory]
    [MemberData(nameof(Languages))]
    public void NullExifAndNoFields_ProduceEmptyText_NotPlaceholders(bool english)
    {
        using var _ = Use(english);

        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.None, "a.jpg", 10, 10, new ExifSummary { Iso = 100 }, Invariant));
        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.All, null, 0, 0, null, Invariant));
        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.All, "   ", -1, 5, new ExifSummary(), Invariant));
        Assert.Equal("a.jpg", ExifFormatter.Format(ExifInfoFields.All, "a.jpg", 0, 0, null, Invariant));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void HugeAndZeroValues_AreHandledWithoutThrowing(bool english)
    {
        using var _ = Use(english);
        var exif = new ExifSummary
        {
            Iso = int.MaxValue,
            FocalLength = new ExifRational(uint.MaxValue, 1),
            FNumber = new ExifRational(1, uint.MaxValue),
            ExposureTime = new ExifRational(uint.MaxValue, 1),
        };

        var text = ExifFormatter.Format(ExifInfoFields.All, "x.jpg", int.MaxValue, int.MaxValue, exif, Invariant);

        Assert.Contains("2147483647×2147483647", text, StringComparison.Ordinal);
        Assert.Contains("2147483647", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("∞", text, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "A zero denominator, zero value or non-positive ISO is dropped, never shown as '0 mm' / 'f/0' / 'ISO 0'")]
    [MemberData(nameof(Languages))]
    public void ZeroOrNegativeValues_AreSkipped(bool english)
    {
        using var _ = Use(english);
        var exif = new ExifSummary
        {
            Iso = 0,
            FocalLength = new ExifRational(50, 0),
            FNumber = new ExifRational(0, 10),
            ExposureTime = new ExifRational(0, 0),
        };
        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.Iso | ExifInfoFields.FocalLength | ExifInfoFields.Aperture | ExifInfoFields.ShutterSpeed, "x.jpg", 1, 1, exif, Invariant));

        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.Iso, "x.jpg", 1, 1, new ExifSummary { Iso = -100 }, Invariant));
    }

    [Theory]
    [InlineData("東京カメラ", "東京 α7")]
    [InlineData("ARABIC", "كاميرا رقمية")]
    [InlineData("שלום", "מצלמה")]
    [InlineData("Ünïcödé Ltd", "Ünïcödé Ltd Ω-1 😀")]
    public void UnicodeAndRightToLeftCameraNames_PassThroughVerbatim(string make, string model)
    {
        var expected = model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : make + " " + model;

        var text = ExifFormatter.Format(ExifInfoFields.Camera, "f.jpg", 1, 1, new ExifSummary { CameraMake = make, CameraModel = model }, Invariant);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void RightToLeftFileName_IsKeptInsideTheSeparatedLine()
    {
        var text = ExifFormatter.Format(ExifInfoFields.FileName | ExifInfoFields.Dimensions, "صورة‏.jpg", 640, 480, null, Invariant);

        Assert.StartsWith("صورة‏.jpg" + ExifFormatter.Separator, text, StringComparison.Ordinal);
        Assert.EndsWith("640×480", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Any display culture, in any calendar, formats any EXIF date without throwing (Hijri/Um al-Qura reject dates outside their range)")]
    public void EveryCulture_FormatsEveryPlausibleDate_WithoutThrowing()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var dates = new[]
        {
            DateTime.MinValue, new DateTime(1600, 1, 1), new DateTime(1850, 6, 1), new DateTime(1970, 1, 1),
            new DateTime(2024, 5, 1, 14, 3, 22), new DateTime(2099, 12, 31), new DateTime(9999, 12, 31, 23, 59, 59),
        };

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            foreach (var date in dates)
            {
                var exif = new ExifSummary { DateTaken = date };
                var text = Record.Exception(() => ExifFormatter.Format(ExifInfoFields.DateTaken, "f.jpg", 1, 1, exif, culture));
                Assert.True(text is null, $"culture {culture.Name}, date {date:O}: {text}");
            }
        }
    }

    [Theory]
    [InlineData(1, 250, "1/250")]
    [InlineData(1, 1, "1")]
    [InlineData(3, 10, "1/3")]
    [InlineData(1, 2, "1/2")]
    [InlineData(5, 10, "0.5")]
    [InlineData(2, 1, "2")]
    [InlineData(30, 1, "30")]
    [InlineData(15, 10, "1.5")]
    [InlineData(1, 8000, "1/8000")]
    [InlineData(1, 4294967295, "1/4294967295")]
    public void ShutterSpeed_BoundaryValues(uint numerator, uint denominator, string expectedNumberPart)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        var text = ExifFormatter.ShutterText(new ExifRational(numerator, denominator), Invariant);

        Assert.NotNull(text);
        Assert.Contains(expectedNumberPart, text, StringComparison.Ordinal);
        Assert.EndsWith(" s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutterSpeed_DecimalSeparatorFollowsTheDisplayCulture()
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        var vi = new CultureInfo("vi-VN");

        Assert.Contains("1,5", ExifFormatter.ShutterText(new ExifRational(15, 10), vi), StringComparison.Ordinal);
        Assert.Contains("1.5", ExifFormatter.ShutterText(new ExifRational(15, 10), Invariant), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", " ")]
    public void CameraText_BlankPartsProduceNothing(string? make, string? model)
    {
        Assert.Null(ExifFormatter.CameraText(make, model));
    }

    [Fact(DisplayName = "Camera make repeated in the model is not doubled, including a make with a company suffix")]
    public void CameraText_DoesNotRepeatTheMake()
    {
        Assert.Equal("Canon EOS R5", ExifFormatter.CameraText("Canon", "Canon EOS R5"));
        Assert.Equal("NIKON Z 6", ExifFormatter.CameraText("NIKON CORPORATION", "NIKON Z 6"));
        Assert.Equal("SONY ILCE-7M3", ExifFormatter.CameraText("SONY", "ILCE-7M3"));
        Assert.Equal("Canon", ExifFormatter.CameraText("Canon", null));
        Assert.Equal("EOS R5", ExifFormatter.CameraText(null, "EOS R5"));
    }
}
