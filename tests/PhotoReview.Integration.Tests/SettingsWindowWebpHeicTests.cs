using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Integration.Tests.Infrastructure;
using PhotoReview.TestSupport;

namespace PhotoReview.Integration.Tests;

/// <summary>Q-FMT-WEBP-HEIC: the WebP/HEIC switch and the codec status line in the Settings window.</summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowWebpHeicTests
{
    [Theory]
    [InlineData(true, true, true, "WebP available; HEIC/HEIF available")]
    [InlineData(false, false, false, "WebP missing (install \"WebP Image Extensions\"")]
    [InlineData(true, true, false, "HEIC/HEIF missing (\"HEIF Image Extensions\" is installed; also install \"HEVC Video Extensions\"")]
    [InlineData(true, false, true, "HEIC/HEIF missing (install \"HEIF Image Extensions\" and \"HEVC Video Extensions\"")]
    public void CodecStatus_NamesWhatIsMissing(bool webp, bool heifContainer, bool hevc, string expectedPart)
    {
        using var _ = TestLocalization.Use(TestLocalization.English);

        var text = SettingsWindow.FormatWebpHeicCodecStatus(new WicCodecSupport(webp, heifContainer, hevc, "t"));

        Assert.Contains(expectedPart, text, StringComparison.Ordinal);
    }

    [Fact]
    public void CodecStatus_IsTranslated()
    {
        var codecs = new WicCodecSupport(true, true, false, "t");
        string english, vietnamese;
        using (TestLocalization.Use(TestLocalization.English)) english = SettingsWindow.FormatWebpHeicCodecStatus(codecs);
        using (TestLocalization.Use(TestLocalization.Vietnamese)) vietnamese = SettingsWindow.FormatWebpHeicCodecStatus(codecs);

        Assert.NotEqual(english, vietnamese);
        Assert.Contains("HEVC Video Extensions", vietnamese, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Window_ShowsTheSwitchAndThisPcsCodecStatus_AndRestoreDefaultsTurnsItOn()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { WebpHeicSupportEnabled = false });
            try
            {
                Assert.False(window.WebpHeicSupportEnabledCheck.IsChecked);
                Assert.Equal(SettingsWindow.FormatWebpHeicCodecStatus(WicCodecAvailability.Current), window.WebpHeicCodecStatusText.Text);

                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);

                Assert.True(window.Settings.WebpHeicSupportEnabled);
                Assert.True(window.WebpHeicSupportEnabledCheck.IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
