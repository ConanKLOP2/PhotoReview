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

    private static AppSettings Loaded(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        SettingsNormalizer.Normalize(settings);
        return settings;
    }

    [Fact(DisplayName = "LoadingMode fallback on invalid values")]
    public void LoadingModeFallbackOnInvalid()
    {
        // RV-S01/RV-D2: the settings converter yields an undefined sentinel that Normalize resets to the AppSettings default.
        Assert.Equal(LoadingMode.Preview, Loaded("{\"LoadingMode\":\"Nonsense\"}").LoadingMode);
        Assert.Equal(LoadingMode.Preview, Loaded("{\"LoadingMode\":\"\"}").LoadingMode);
    }

    [Fact(DisplayName = "DecoderBackend defaults to WicDirect (fastest, ADR 0001); unknown values fall back to the default (WicDirect)")]
    public void DecoderBackendDefaultsToWicDirect()
    {
        Assert.Equal(DecoderBackend.WicDirect, new AppSettings().DecoderBackend);
        Assert.Equal(DecoderBackend.WicDirect, JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.DecoderBackend);
        Assert.Equal(DecoderBackend.WicDirect, Loaded("{\"DecoderBackend\":\"Unknown\"}").DecoderBackend);
    }

    [Fact(DisplayName = "Source bytes cache is disabled by default")]
    public void SourceBytesCacheDefaultsOff() => Assert.False(new AppSettings().UseSourceBytesCache);

    [Fact(DisplayName = "ImageSortMode defaults to Default (file system order) and deserializes aliases")]
    public void ImageSortModeDefaultsAndDeserializesAliases()
    {
        Assert.Equal(ImageSortMode.Default, new AppSettings().ImageSortMode);
        Assert.Equal(ImageSortMode.SizeDescending, JsonSerializer.Deserialize<AppSettings>("{\"ImageSortMode\":\"Size\"}")!.ImageSortMode);
        Assert.Equal(ImageSortMode.SizeAscending, JsonSerializer.Deserialize<AppSettings>("{\"ImageSortMode\":\"sizeascending\"}")!.ImageSortMode);
        Assert.Equal(ImageSortMode.Default, Loaded("{\"ImageSortMode\":\"Unknown\"}").ImageSortMode);
    }

    [Theory(DisplayName = "ImageSortMode Default/NameAscending/NameDescending load by name and by number; unknown falls back to the default")]
    [InlineData("\"Default\"", ImageSortMode.Default)]
    [InlineData("3", ImageSortMode.Default)]
    [InlineData("\"NameAscending\"", ImageSortMode.NameAscending)]
    [InlineData("4", ImageSortMode.NameAscending)]
    [InlineData("\"NameDescending\"", ImageSortMode.NameDescending)]
    [InlineData("5", ImageSortMode.NameDescending)]
    [InlineData("\"Bogus\"", ImageSortMode.Default)]
    public void ImageSortModeNewValuesRoundTrip(string json, ImageSortMode expected)
    {
        var loaded = Loaded("{\"ImageSortMode\":" + json + "}");
        Assert.Equal(expected, loaded.ImageSortMode);
        var again = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(loaded))!;
        Assert.Equal(expected, again.ImageSortMode);
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

    // ---- Q-R41..Q-R50 user feedback: new settings default off/unconfigured, absent-in-old-config = defaults ----

    [Fact(DisplayName = "Q-R41: KeyboardZoomStepPercent defaults to 10 and round-trips; absent config = default")]
    public void KeyboardZoomStepPercentDefaultsTo10()
    {
        Assert.Equal(10, AppSettings.DefaultKeyboardZoomStepPercent);
        Assert.Equal(10, new AppSettings().KeyboardZoomStepPercent);
        Assert.Equal(10, JsonSerializer.Deserialize<AppSettings>("{\"ConfigVersion\":1}")!.KeyboardZoomStepPercent);
        Assert.Equal(25, JsonSerializer.Deserialize<AppSettings>("{\"KeyboardZoomStepPercent\":25}")!.KeyboardZoomStepPercent);
    }

    [Fact(DisplayName = "Q-R44: ConfirmBeforeDelete defaults to false")]
    public void ConfirmBeforeDeleteDefaultsToFalse() => Assert.False(new AppSettings().ConfirmBeforeDelete);

    [Fact(DisplayName = "Q-R45: ShowZoomIndicator defaults to false (HUD off, not shown)")]
    public void ShowZoomIndicatorDefaultsToFalse() => Assert.False(new AppSettings().ShowZoomIndicator);

    [Fact(DisplayName = "Q-R48: ExternalEditorPath defaults to empty (not configured)")]
    public void ExternalEditorPathDefaultsToEmpty() => Assert.Equal(string.Empty, new AppSettings().ExternalEditorPath);

    [Fact(DisplayName = "Q-R42/Q-R43: OpenFolder and CustomZoom shortcuts have distinct, non-empty defaults")]
    public void OpenFolderAndCustomZoomShortcuts_HaveDistinctDefaults()
    {
        var shortcuts = new ShortcutMappings();
        Assert.Equal("O", shortcuts.OpenFolder);
        Assert.Equal("D3", shortcuts.CustomZoom);
        Assert.True(ShortcutMappings.IsOptional(nameof(ShortcutMappings.OpenFolder)));
        Assert.True(ShortcutMappings.IsOptional(nameof(ShortcutMappings.CustomZoom)));
    }

    [Fact(DisplayName = "AppSettings.Clone round-trips the new Q-R41..Q-R50 settings")]
    public void Clone_RoundTripsNewSettings()
    {
        var source = new AppSettings
        {
            KeyboardZoomStepPercent = 20,
            ConfirmBeforeDelete = true,
            ShowZoomIndicator = true,
            ExternalEditorPath = @"C:\Tools\editor.exe",
        };
        source.Shortcuts.OpenFolder = "F2";
        source.Shortcuts.CustomZoom = "F3";

        var clone = AppSettings.Clone(source);

        Assert.Equal(20, clone.KeyboardZoomStepPercent);
        Assert.True(clone.ConfirmBeforeDelete);
        Assert.True(clone.ShowZoomIndicator);
        Assert.Equal(@"C:\Tools\editor.exe", clone.ExternalEditorPath);
        Assert.Equal("F2", clone.Shortcuts.OpenFolder);
        Assert.Equal("F3", clone.Shortcuts.CustomZoom);
    }
}

