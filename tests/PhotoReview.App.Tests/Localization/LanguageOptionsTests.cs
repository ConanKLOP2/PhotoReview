using PhotoReview.App.Localization;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests.Localization;

/// <summary>I18N L08: the Settings language picker maps setting values ("auto" or a code) to entries and back.</summary>
public sealed class LanguageOptionsTests
{
    private static readonly LanguageInfo[] Languages =
    [
        new("en", "English", "English", false),
        new("vi", "Vietnamese", "Tiếng Việt", false),
        new("fr", "French", "Français", true),
    ];

    [Fact]
    public void Build_AutoFirst_ThenNativeNames_UserFilesMarked()
    {
        var options = LanguageOptions.Build(Languages);

        Assert.Equal(["auto", "en", "vi", "fr"], options.Select(o => o.Code));
        Assert.Equal("Tự động (theo Windows)", options[0].DisplayName);
        Assert.True(options[0].IsAuto);
        Assert.Equal("English", options[1].DisplayName);
        Assert.Equal("Tiếng Việt", options[2].DisplayName);
        Assert.Equal("Français (file người dùng)", options[3].DisplayName);
    }

    [Fact]
    public void Build_UsesCurrentLanguageForItsOwnLabels()
    {
        var options = LanguageOptions.Build(Languages, TestLocalization.English);

        Assert.Equal("Auto (Windows)", options[0].DisplayName);
        Assert.Equal("Français (user file)", options[3].DisplayName);
    }

    [Theory]
    [InlineData("auto", 0)]
    [InlineData("AUTO", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("en", 1)]
    [InlineData("VI", 2)]
    [InlineData(" fr ", 3)]
    [InlineData("de", -1)]
    public void IndexOf_MapsSettingValueToEntry(string? setting, int expected)
    {
        Assert.Equal(expected, LanguageOptions.IndexOf(LanguageOptions.Build(Languages), setting));
    }

    [Fact]
    public void ToSetting_SelectedEntryWins_NoSelectionKeepsCurrent()
    {
        var options = LanguageOptions.Build(Languages);

        Assert.Equal("auto", LanguageOptions.ToSetting(options[0], "vi"));
        Assert.Equal("vi", LanguageOptions.ToSetting(options[2], "auto"));
        Assert.Equal("de", LanguageOptions.ToSetting(null, "de")); // a removed user language is kept, not reset
        Assert.Equal("auto", LanguageOptions.ToSetting(null, "  "));
    }

    [Fact]
    public void RoundTrip_EverySettingValueSurvivesPickerAndSave()
    {
        var options = LanguageOptions.Build(Languages);
        foreach (var value in new[] { "auto", "en", "vi", "fr" })
        {
            var index = LanguageOptions.IndexOf(options, value);
            Assert.Equal(value, LanguageOptions.ToSetting(options[index], "unused"));
        }
    }
}
