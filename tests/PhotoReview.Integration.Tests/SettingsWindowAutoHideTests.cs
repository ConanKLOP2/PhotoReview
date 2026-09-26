using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R34: the Settings window's two independent auto-hide groups (toolbar / info over the photo) and the toolbar
/// opacity slider: each delay box is enabled only by its own checkbox, and the slider cannot go below the minimum.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsWindowAutoHideTests
{
    private static async Task WithWindowAsync(AppSettings settings, Action<SettingsWindow> assertions)
    {
        Exception? failure = null;
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(settings)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            window.Loaded += (_, _) =>
            {
                try { assertions(window); }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); }
            };
            window.ShowDialog();
            return Task.CompletedTask;
        });
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public async Task ToolbarCheckboxOnly_EnablesOnlyTheToolbarDelayBox()
    {
        await WithWindowAsync(new AppSettings { ToolbarAutoHide = true, InfoOverlayAutoHide = false }, w =>
        {
            Assert.True(w.ToolbarAutoHideDelayBox.IsEnabled);
            Assert.False(w.InfoOverlayAutoHideDelayBox.IsEnabled);
        });
    }

    [Fact]
    public async Task InfoCheckboxOnly_EnablesOnlyTheInfoDelayBox()
    {
        await WithWindowAsync(new AppSettings { ToolbarAutoHide = false, InfoOverlayAutoHide = true }, w =>
        {
            Assert.False(w.ToolbarAutoHideDelayBox.IsEnabled);
            Assert.True(w.InfoOverlayAutoHideDelayBox.IsEnabled);
        });
    }

    [Fact]
    public async Task TogglingACheckbox_UpdatesOnlyItsOwnDelayBox()
    {
        await WithWindowAsync(new AppSettings(), w =>
        {
            Assert.False(w.ToolbarAutoHideDelayBox.IsEnabled);
            Assert.False(w.InfoOverlayAutoHideDelayBox.IsEnabled);
            w.InfoOverlayAutoHideCheck.IsChecked = true;
            Assert.False(w.ToolbarAutoHideDelayBox.IsEnabled);
            Assert.True(w.InfoOverlayAutoHideDelayBox.IsEnabled);
            w.ToolbarAutoHideCheck.IsChecked = true;
            w.InfoOverlayAutoHideCheck.IsChecked = false;
            Assert.True(w.ToolbarAutoHideDelayBox.IsEnabled);
            Assert.False(w.InfoOverlayAutoHideDelayBox.IsEnabled);
        });
    }

    [Fact]
    public async Task ToolbarOpacitySlider_HasTheMinimumAndCannotGoBelowIt()
    {
        await WithWindowAsync(new AppSettings(), w =>
        {
            Assert.Equal(AppSettings.MinToolbarOpacityPercent, w.ToolbarOpacitySlider.Minimum);
            Assert.Equal(20, w.ToolbarOpacitySlider.Minimum);
            Assert.Equal(100, w.ToolbarOpacitySlider.Maximum);
            Assert.Equal(100, w.ToolbarOpacitySlider.Value);
            w.ToolbarOpacitySlider.Value = 5;
            Assert.Equal(20, w.ToolbarOpacitySlider.Value);
            w.ToolbarOpacitySlider.Value = 500;
            Assert.Equal(100, w.ToolbarOpacitySlider.Value);
        });
    }

    [Fact]
    public async Task ToolbarOpacitySlider_ShowsTheStoredValue_ClampedIfHandEdited()
    {
        await WithWindowAsync(new AppSettings { ToolbarOpacityPercent = 60 }, w => Assert.Equal(60, w.ToolbarOpacitySlider.Value));
        await WithWindowAsync(new AppSettings { ToolbarOpacityPercent = 3 }, w => Assert.Equal(20, w.ToolbarOpacitySlider.Value));
    }
}
