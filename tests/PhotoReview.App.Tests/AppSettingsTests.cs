using System.Text.Json;
using PhotoReview.App;
using PhotoReview.Core;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.App.Tests;

[Trait("Category", "HotPath")]
public sealed class AppSettingsTests
{
    [Fact(DisplayName = "LoadingMode defaults to Preview")]
    public void LoadingModeDefaultsToPreview() => Assert.Equal(LoadingMode.Preview, new AppSettings().LoadingMode);

    [Fact(DisplayName = "LoadingMode deserializes case-insensitively")]
    public void LoadingModeDeserializesCaseInsensitively()
    {
        Assert.Equal(LoadingMode.Preview, JsonSerializer.Deserialize<AppSettings>("{\"LoadingMode\":\"preview\"}")!.LoadingMode);
        Assert.Equal(LoadingMode.Original, JsonSerializer.Deserialize<AppSettings>("{\"LoadingMode\":\"ORIGINAL\"}")!.LoadingMode);
        Assert.Equal(LoadingMode.Fast, JsonSerializer.Deserialize<AppSettings>("{\"LoadingMode\":\"fast\"}")!.LoadingMode);
    }

    [Fact(DisplayName = "LoadingMode fallback on invalid values")]
    public void LoadingModeFallbackOnInvalid()
    {
        Assert.Equal(LoadingMode.Fast, JsonSerializer.Deserialize<AppSettings>("{\"LoadingMode\":\"Nonsense\"}")!.LoadingMode);
        Assert.Equal(LoadingMode.Fast, JsonSerializer.Deserialize<AppSettings>("{\"LoadingMode\":\"\"}")!.LoadingMode);
    }

    [Fact(DisplayName = "DecoderBackend defaults to WicDirect (fastest, ADR 0001); unknown values fall back to Wpf")]
    public void DecoderBackendDefaultsToWicDirect()
    {
        Assert.Equal(DecoderBackend.WicDirect, new AppSettings().DecoderBackend);
        Assert.Equal(DecoderBackend.WicDirect, JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.DecoderBackend);
        Assert.Equal(DecoderBackend.Wpf, JsonSerializer.Deserialize<AppSettings>("{\"DecoderBackend\":\"Unknown\"}")!.DecoderBackend);
    }

    [Fact(DisplayName = "Source bytes cache is disabled by default")]
    public void SourceBytesCacheDefaultsOff() => Assert.False(new AppSettings().UseSourceBytesCache);

    [Fact(DisplayName = "ImageSortMode defaults to Name and deserializes aliases")]
    public void ImageSortModeDefaultsAndDeserializesAliases()
    {
        Assert.Equal(ImageSortMode.Name, new AppSettings().ImageSortMode);
        Assert.Equal(ImageSortMode.SizeDescending, JsonSerializer.Deserialize<AppSettings>("{\"ImageSortMode\":\"Size\"}")!.ImageSortMode);
        Assert.Equal(ImageSortMode.SizeAscending, JsonSerializer.Deserialize<AppSettings>("{\"ImageSortMode\":\"sizeascending\"}")!.ImageSortMode);
        Assert.Equal(ImageSortMode.Name, JsonSerializer.Deserialize<AppSettings>("{\"ImageSortMode\":\"Unknown\"}")!.ImageSortMode);
    }

    [Fact(DisplayName = "InitialViewMode parses percent and fit strings")]
    public void InitialViewModeParsesPercentAndFit()
    {
        Assert.Equal(InitialViewMode.Percent100, JsonSerializer.Deserialize<AppSettings>("{\"InitialViewMode\":\"100%\"}")!.InitialViewMode);
        Assert.Equal(InitialViewMode.Percent200, JsonSerializer.Deserialize<AppSettings>("{\"InitialViewMode\":\"200%\"}")!.InitialViewMode);
        Assert.Equal(InitialViewMode.Fit, JsonSerializer.Deserialize<AppSettings>("{\"InitialViewMode\":\"Fit\"}")!.InitialViewMode);
    }

    [Fact(DisplayName = "Config supports multiple review actions with distinct shortcuts")]
    public void ConfigSupportsMultipleReviewActionsWithDistinctShortcuts()
    {
        var settings = new AppSettings();
        settings.Actions.Add(new ReviewAction { Name = "Loáº¡i 3", Shortcut = "T", Operation = FileOperationType.Copy, Destination = "Loai-3" });
        // AR11a: AppSettings.ValidateShortcuts (static) is gone; validation now lives on a SettingsStore instance
        // (KeyNames defaults to the same permissive fallback the old static Validator used when unassigned).
        var store = new SettingsStore(new AppPaths(AppContext.BaseDirectory), new PhysicalFileSystem(), NullLog.Instance);
        Assert.True(settings.Actions.Count >= 2
            && settings.Actions.All(a => a.Name.Length > 0 && Enum.IsDefined(a.Operation) && a.Destination.Length > 0)
            && store.ValidateShortcuts(settings) is null);
    }

    [Fact(DisplayName = "Config carries an explicit version that round-trips through JSON")]
    public void ConfigCarriesExplicitVersionThatRoundTrips() =>
        Assert.True(new AppSettings().ConfigVersion == AppSettings.CurrentConfigVersion
            && JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.ConfigVersion == 1);
}

