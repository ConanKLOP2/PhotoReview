using System.Diagnostics;
using System.Globalization;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Metadata;
using Xunit.Abstractions;

namespace PhotoReview.App.Tests.HotPath;

/// <summary>
/// PERF-TITLE-01: <c>MainViewModel.NotifyNavigationStateChanged</c> rebuilds the title bar
/// (<see cref="TitleBarFormatter.Format"/>) and the EXIF line (<see cref="ExifFormatter.Format"/>) 2-3 times per
/// navigation (once from each <c>NotifyCurrentImageChanged</c> call -- one per bitmap stage -- plus once from
/// <c>NotifyPresentationChanged</c>; see the design note above <c>NotifyNavigationStateChanged</c>). Cost report
/// only (no timing assert: wall-clock numbers are not deterministic) -- decides whether that rebuild needs
/// caching. All title-bar/EXIF-line fields are on (worst case: every branch in both formatters taken).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class TitleBarAndExifLineCostTests
{
    private readonly ITestOutputHelper _output;

    public TitleBarAndExifLineCostTests(ITestOutputHelper output) => _output = output;

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

    private static readonly CultureInfo Provider = CultureInfo.InvariantCulture;
    private static readonly DateTime ModifiedUtc = new(2024, 6, 10, 8, 0, 0, DateTimeKind.Utc);
    private const string Folder = @"C:\Photos\Vacation 2024\Day 3 - Waterfalls and the old town";
    private const string FileName = "IMG_1234.jpg";

    private static string BuildTitleBar() => TitleBarFormatter.Format(
        TitleBarFields.All, Folder, 11, 340, FileName, 2_500_000, 6000, 4000, ModifiedUtc, Exif, Provider);

    private static string BuildExifLine() => ExifFormatter.Format(
        ExifInfoFields.All, FileName, 6000, 4000, Exif, ModifiedUtc, Provider);

    [Fact(DisplayName = "PERF-TITLE-01: reports the cost of one title-bar + EXIF-line rebuild (all fields on)")]
    public void ReportsRebuildCost()
    {
        const int rounds = 20_000;
        string lastTitle = "", lastExif = "";

        // Warm-up (JIT).
        lastTitle = BuildTitleBar();
        lastExif = BuildExifLine();

        var titleBar = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) lastTitle = BuildTitleBar();
        titleBar.Stop();

        var exifLine = Stopwatch.StartNew();
        for (var i = 0; i < rounds; i++) lastExif = BuildExifLine();
        exifLine.Stop();

        var titleUs = titleBar.Elapsed.TotalMilliseconds * 1000 / rounds;
        var exifUs = exifLine.Elapsed.TotalMilliseconds * 1000 / rounds;

        _output.WriteLine($"TitleBarFormatter.Format: {titleUs:F2} us/call");
        _output.WriteLine($"ExifFormatter.Format:     {exifUs:F2} us/call");
        _output.WriteLine($"Combined (one NotifyNavigationStateChanged rebuild): {titleUs + exifUs:F2} us/call");

        Assert.NotEmpty(lastTitle);
        Assert.NotEmpty(lastExif);
    }
}
