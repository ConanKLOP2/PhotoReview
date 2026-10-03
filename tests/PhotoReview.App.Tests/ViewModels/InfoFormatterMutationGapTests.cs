using System.Globalization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>
/// Boundary pins for the photo information line and the title bar text found by Stryker (mutation gaps): a size with a
/// zero axis is "unknown" and must not be printed, the camera-name merge, and the shutter-speed fraction/decimal choice.
/// </summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // reads the ambient Localizer through Tr
public sealed class InfoFormatterMutationGapTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static IDisposable English() => TestLocalization.Use(TestLocalization.English);

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void ExifFormat_WithAZeroDimensionAxis_OmitsTheDimensions(int width, int height)
    {
        using var _ = English();

        Assert.Equal(string.Empty, ExifFormatter.Format(ExifInfoFields.Dimensions, "a.jpg", width, height, null, provider: Invariant));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void ExifFormat_WithAZeroRawPreviewAxis_OmitsThePreviewSize(int previewWidth, int previewHeight)
    {
        using var _ = English();

        Assert.Equal(string.Empty,
            ExifFormatter.Format(ExifInfoFields.Dimensions, "a.jpg", 0, 0, null, provider: Invariant, rawPreviewWidth: previewWidth, rawPreviewHeight: previewHeight));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void TitleBarFormat_WithAZeroDimensionAxis_OmitsTheDimensions(int width, int height)
    {
        using var _ = English();

        Assert.Equal(string.Empty,
            TitleBarFormatter.Format(TitleBarFields.Dimensions, @"C:\Photos", 0, 1, null, null, width, height, null, null, Invariant));
    }

    [Fact]
    public void TitleBarFormat_WithACatalogCountOfZero_OmitsThePosition()
    {
        using var _ = English();

        Assert.Equal(string.Empty,
            TitleBarFormatter.Format(TitleBarFields.IndexCount, @"C:\Photos", 0, 0, null, null, 0, 0, null, null, Invariant));
    }

    [Fact]
    public void CameraText_WhenTheMakeStartsWithASpace_DoesNotTreatItAsAnEmptyFirstWord()
    {
        // IndexOf(' ') is 0 here: there is no leading "word" to look for in the model, so both parts are joined.
        Assert.Equal(" Canon Canon X", ExifFormatter.CameraText(" Canon", "Canon X"));
    }

    [Fact]
    public void ShutterText_OneSecondExactly_IsDecimalSecondsNotAOneOverOneFraction()
    {
        using var _ = English();

        Assert.Equal(Tr.ExifShutterSeconds("1"), ExifFormatter.ShutterText(new ExifRational(1, 1), Invariant));
    }

    [Fact]
    public void ShutterText_NonUnitFractionOfExactlyAFraction_ShowsTheFraction()
    {
        using var _ = English();

        // 2/20 s == 0.1 s == 1/10 s: the reciprocal is a whole number, so the fraction is faithful.
        Assert.Equal(Tr.ExifShutterFraction("10"), ExifFormatter.ShutterText(new ExifRational(2, 20), Invariant));
    }

    [Fact]
    public void ShutterText_FractionWithinOnePercentOfAWholeReciprocal_ShowsTheFraction()
    {
        using var _ = English();

        // 20/199 s = 0.1005 s: 1/s = 9.95, within 1 % of 10.
        Assert.Equal(Tr.ExifShutterFraction("10"), ExifFormatter.ShutterText(new ExifRational(20, 199), Invariant));
    }

    [Fact]
    public void ShutterText_FractionBelowTenthOfASecondWithAnUnfaithfulReciprocal_StillShowsAFraction()
    {
        using var _ = English();

        // 5/138 s = 0.036 s: 1/s = 27.6 is not within 1 % of a whole number, but below 0.1 s decimal seconds are never used.
        Assert.Equal(Tr.ExifShutterFraction("28"), ExifFormatter.ShutterText(new ExifRational(5, 138), Invariant));
    }

    [Fact]
    public void ShutterText_NonUnitFractionAboveTenthOfASecondWithAnUnfaithfulReciprocal_ShowsDecimalSeconds()
    {
        using var _ = English();

        Assert.Equal(Tr.ExifShutterSeconds("0.4"), ExifFormatter.ShutterText(new ExifRational(4, 10), Invariant));
    }
}
