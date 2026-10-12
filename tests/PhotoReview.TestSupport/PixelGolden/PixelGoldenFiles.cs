using PhotoReview.TestSupport.Golden;

namespace PhotoReview.TestSupport.PixelGolden;

/// <summary>WP-28: where the G-PIX reference frames live (<c>tests/Fixtures/golden/pixel/*.png</c>) and where reports go.</summary>
public static class PixelGoldenFiles
{
    /// <summary>Environment variable naming the directory that receives diff images and the report (default: a temp folder).</summary>
    public const string ReportDirectoryVariable = "PHOTOREVIEW_PIXEL_REPORT_DIR";

    public static string Directory { get; } = Path.Combine(GoldenFile.Directory, "pixel");

    public static string PathOf(string caseName) => Path.Combine(Directory, caseName + ".png");

    public static string ReportDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ReportDirectoryVariable);
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "PhotoReview-pixel-golden")
            : configured;
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }
}
