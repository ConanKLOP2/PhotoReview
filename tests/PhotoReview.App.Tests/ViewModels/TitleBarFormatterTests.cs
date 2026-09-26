using System.Globalization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>The main window title bar's content: field order, defaults, missing-value handling, shared value formatting.</summary>
[Trait("Category", "HotPath")]
[Collection("GlobalState")] // switches the ambient Localizer
public sealed class TitleBarFormatterTests
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
    private static readonly DateTime ModifiedUtc = new(2024, 6, 10, 8, 0, 0, DateTimeKind.Utc);

    private const string Folder = @"C:\Photos\Vacation 2024";
    private const string FolderName = "Vacation 2024";

    private static string Format(TitleBarFields fields, int? index = 11, int? count = 340, string? fileName = "IMG_1234.jpg",
        long? length = 2_500_000, int width = 6000, int height = 4000, ExifSummary? exif = null, DateTime? modifiedUtc = null) =>
        TitleBarFormatter.Format(fields, Folder, index, count, fileName, length, width, height, modifiedUtc ?? ModifiedUtc, exif ?? Exif, Invariant);

    [Fact(DisplayName = "Default (FolderName only) shows just the folder's own name, not the full path")]
    public void Default_IsFolderNameOnly()
    {
        Assert.Equal(FolderName, Format(TitleBarFields.Default));
    }

    [Fact(DisplayName = "FolderPath shows the full path")]
    public void FolderPath_ShowsFullPath()
    {
        Assert.Equal(Folder, Format(TitleBarFields.FolderPath));
    }

    [Fact(DisplayName = "All fields render in the fixed declared order")]
    public void AllFields_RenderInOrder()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        // FileSize is not built here (StatusFormatter.FormatFileSize uses CultureInfo.CurrentCulture, not `provider`):
        // computed the same way TitleBarFormatter does, so this only pins the ORDER, not that method's own formatting.
        var expected = string.Join(TitleBarFormatter.Separator,
            FolderName, Folder, "12/340", "IMG_1234.jpg", StatusFormatter.FormatFileSize(2_500_000), "6000×4000",
            "Modified: " + ModifiedUtc.ToLocalTime().ToString("g", Invariant), "Taken: 05/01/2024 14:03", "Canon EOS R5", "RF24-70mm F2.8",
            "ISO 400", "50 mm", "f/2.8", "1/250 s");

        Assert.Equal(expected, Format(TitleBarFields.All));
    }

    [Fact(DisplayName = "IndexCount renders as 1-based position/count, like the status line")]
    public void IndexCount_Is1BasedPositionOverCount()
    {
        Assert.Equal("1/5", Format(TitleBarFields.IndexCount, index: 0, count: 5));
    }

    [Theory(DisplayName = "Missing values are skipped, never shown as placeholders")]
    [InlineData(TitleBarFields.IndexCount)]
    [InlineData(TitleBarFields.FileName)]
    [InlineData(TitleBarFields.FileSize)]
    [InlineData(TitleBarFields.Dimensions)]
    [InlineData(TitleBarFields.ModifiedDate)]
    [InlineData(TitleBarFields.DateTaken)]
    [InlineData(TitleBarFields.Camera)]
    public void MissingValues_AreSkipped(TitleBarFields field)
    {
        Assert.Equal(FolderName,
            TitleBarFormatter.Format(TitleBarFields.FolderName | field, Folder, null, null, null, null, 0, 0, null, null, Invariant));
    }

    [Fact(DisplayName = "Selecting nothing produces an empty string (caller falls back to the folder name)")]
    public void NoFieldsSelected_ProducesEmpty()
    {
        Assert.Equal(string.Empty, Format(TitleBarFields.None));
    }

    [Fact(DisplayName = "FileSize reuses StatusFormatter's KB/MB units")]
    public void FileSize_UsesSharedUnitFormatting()
    {
        Assert.Equal(StatusFormatter.FormatFileSize(2_500_000), Format(TitleBarFields.FileSize));
    }

    [Fact(DisplayName = "ModifiedDate converts UTC to local time and formats like ExifFormatter's DateTaken")]
    public void ModifiedDate_IsLocalTimeFormattedLikeDateTaken()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        Assert.Equal("Modified: " + ModifiedUtc.ToLocalTime().ToString("g", Invariant), Format(TitleBarFields.ModifiedDate));
    }

    [Fact(DisplayName = "Camera reuses ExifFormatter's make/model de-duplication")]
    public void Camera_ReusesExifFormatterCameraText()
    {
        Assert.Equal("Canon EOS R5", Format(TitleBarFields.Camera));
    }

    [Fact(DisplayName = "Turning one field off removes exactly that part")]
    public void TurningOneFieldOff_RemovesExactlyThatPart()
    {
        using var _ = TestLocalization.Use(TestLocalization.English);
        var all = Format(TitleBarFields.All);
        var withoutModified = Format(TitleBarFields.All & ~TitleBarFields.ModifiedDate);

        Assert.Contains(ModifiedUtc.ToLocalTime().ToString("g", Invariant), all, StringComparison.Ordinal);
        Assert.DoesNotContain(ModifiedUtc.ToLocalTime().ToString("g", Invariant), withoutModified, StringComparison.Ordinal);
        Assert.Equal(all.Split(TitleBarFormatter.Separator).Length - 1, withoutModified.Split(TitleBarFormatter.Separator).Length);
    }
}
