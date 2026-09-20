using System.Text.Json;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;

namespace PhotoReview.Core.Tests.Settings;

[Trait("Category", "HotPath")]
public sealed class AppSettingsPocoTests
{
    [Fact(DisplayName = "Core AppSettings defaults match requirements")]
    public void AppSettingsDefaults()
    {
        var s = new AppSettings();
        Assert.Equal(2, s.ConfigVersion);
        Assert.Equal(InitialViewMode.Fit, s.InitialViewMode);
        Assert.Equal(LoadingMode.Preview, s.LoadingMode);
        Assert.False(s.LoggingEnabled);
        Assert.Equal(ImageSortMode.Name, s.ImageSortMode);
        Assert.True(s.CompareHashEnabled);
        Assert.True(s.CompareSizeEnabled);
        Assert.NotEmpty(s.Actions);
        Assert.NotNull(s.Shortcuts);
    }

    [Fact(DisplayName = "Core AppSettings deserializes with enum aliases and case-insensitivity")]
    public void AppSettingsDeserialization()
    {
        var json = """
        {
            "LoadingMode": "fast",
            "ImageSortMode": "Size",
            "InitialViewMode": "100%"
        }
        """;
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(LoadingMode.Fast, s.LoadingMode);
        Assert.Equal(ImageSortMode.SizeDescending, s.ImageSortMode);
        Assert.Equal(InitialViewMode.Percent100, s.InitialViewMode);
    }
}
