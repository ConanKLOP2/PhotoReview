using PhotoReview.App;
using PhotoReview.Core.Localization;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>Texts the Settings window assigns in code follow a language change / "Reload translations" like the {loc:Tr} XAML texts do.</summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowLanguageRefreshTests
{
    [Fact(DisplayName = "Settings page title, hints and zoom shortcut summary are re-rendered when the language changes")]
    public async Task CodeSetTexts_FollowLanguageChange()
    {
        try
        {
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings());
                var viTitle = window.PageTitleText.Text;
                var viSummary = window.ZoomShortcutsSummary.Text;
                var viHint = window.ToolbarOpacityHint.Text;
                Assert.Equal(Tr.SettingsNavGeneral, viTitle);

                using (TestLocalization.Use(TestLocalization.English))
                {
                    Assert.Equal(Tr.SettingsNavGeneral, window.PageTitleText.Text);
                    Assert.NotEqual(viTitle, window.PageTitleText.Text);
                    Assert.Contains(Tr.SettingsShortcutClickZoom, window.ZoomShortcutsSummary.Text);
                    Assert.NotEqual(viSummary, window.ZoomShortcutsSummary.Text);
                    Assert.Equal(Tr.SettingsToolbarOpacityHint(AppSettings.MinToolbarOpacityPercent), window.ToolbarOpacityHint.Text);
                    Assert.NotEqual(viHint, window.ToolbarOpacityHint.Text);
                }
                window.Close();
                return Task.CompletedTask;
            });
        }
        finally
        {
            TestLocalization.UseVietnamese();
        }
    }
}
