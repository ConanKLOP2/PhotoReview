using PhotoReview.App;

namespace PhotoReview.Tests.Unit;

public sealed class AppSettingsTests
{
    [Fact(DisplayName = "LoadingMode defaults to Preview")]
    public void LoadingModeDefaultsToPreview() => Assert.Equal("Preview", new AppSettings().LoadingMode);

    [Fact(DisplayName = "LoadingMode has Fast, Preview, and Original options and accepts them case-insensitively")]
    public void LoadingModeHasFastPreviewOriginalOptions() =>
        Assert.True(AppSettings.IsValidLoadingMode("Fast") && AppSettings.IsValidLoadingMode("Preview")
            && AppSettings.IsValidLoadingMode("Original")
            && AppSettings.NormalizeLoadingMode("preview") == "Preview"
            && AppSettings.NormalizeLoadingMode("ORIGINAL") == "Original");

    [Fact(DisplayName = "LoadingMode validation rejects unknown, null, and empty values")]
    public void LoadingModeValidationRejectsUnknownNullAndEmpty() =>
        Assert.True(!AppSettings.IsValidLoadingMode("Nonsense") && !AppSettings.IsValidLoadingMode(null)
            && !AppSettings.IsValidLoadingMode(""));

    [Fact(DisplayName = "LoadingMode normalization always yields a supported mode for corrupt config values")]
    public void LoadingModeNormalizationAlwaysYieldsSupportedMode() =>
        Assert.True(AppSettings.IsValidLoadingMode(AppSettings.NormalizeLoadingMode("Nonsense"))
            && AppSettings.IsValidLoadingMode(AppSettings.NormalizeLoadingMode(null)));

    [Fact(DisplayName = "ImageSortMode defaults to Name, validates known modes, and normalizes unknown ones")]
    public void ImageSortModeDefaultsValidatesAndNormalizes() =>
        Assert.True(new AppSettings().ImageSortMode == "Name" && AppSettings.IsValidImageSortMode("SizeAscending")
            && !AppSettings.IsValidImageSortMode("Whatever")
            && AppSettings.NormalizeImageSortMode("Size") == "SizeDescending"
            && AppSettings.NormalizeImageSortMode("Whatever") == "Name");

    [Fact(DisplayName = "Config supports multiple review actions with distinct shortcuts")]
    public void ConfigSupportsMultipleReviewActionsWithDistinctShortcuts()
    {
        var settings = new AppSettings();
        settings.Actions.Add(new ReviewAction { Name = "Loại 3", Shortcut = "T", Operation = "Copy", Destination = "Loai-3" });
        Assert.True(settings.Actions.Count >= 2
            && settings.Actions.All(a => a.Name.Length > 0 && a.Operation.Length > 0 && a.Destination.Length > 0)
            && AppSettings.ValidateShortcuts(settings) is null);
    }

    [Fact(DisplayName = "Config carries an explicit version that round-trips through JSON")]
    public void ConfigCarriesExplicitVersionThatRoundTrips() =>
        Assert.True(new AppSettings().ConfigVersion == AppSettings.CurrentConfigVersion
            && System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.ConfigVersion == 1);
}
