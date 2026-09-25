using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Preload;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>RAM%: the real Settings window's cache slider is bounded to [device minimum, 90] and saves the percent.</summary>
[Collection("GlobalState")]
public sealed class SettingsRamCacheSliderTests
{
    [Fact]
    public async Task Slider_RangeIsDeviceMinimumToNinety_AndBelowMinimumValueIsRaised()
    {
        var physical = RamBudgetPolicy.GetPhysicalMemoryBytes();
        var minimum = RamBudgetPolicy.MinimumCachePercent(physical);
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { ImageCacheRamPercent = 1 });
            try
            {
                Assert.Equal(minimum, window.RamCacheSlider.Minimum);
                Assert.Equal(90, window.RamCacheSlider.Maximum);
                Assert.Equal(minimum, window.RamCacheSlider.Value);
                Assert.Contains(minimum.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%", window.RamCacheValueText.Text, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(window.RamCacheHint.Text));

                window.RamCacheSlider.Value = 200;
                Assert.Equal(90, window.RamCacheSlider.Value);
                Assert.Contains("90%", window.RamCacheValueText.Text, StringComparison.Ordinal);

                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);
                Assert.Equal(PerformanceOptions.ImageCacheRamPercent, window.RamCacheSlider.Value);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Save_WritesSliderPercentToStoreAndConfig()
    {
        using var root = new TempRoot("settings-ram");
        var appPaths = new AppPaths(root.Path);
        var store = new SettingsStore(appPaths, new PhysicalFileSystem(), NullLog.Instance);
        store.Load();

        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(store)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
            };
            var save = typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            window.Loaded += (_, _) =>
            {
                window.RamCacheSlider.Value = 70;
                save.Invoke(window, [window, new RoutedEventArgs()]);
            };
            Assert.True(window.ShowDialog());
            return Task.CompletedTask;
        });

        Assert.Equal(70, store.Current.ImageCacheRamPercent);
        var reloaded = new SettingsStore(appPaths, new PhysicalFileSystem(), NullLog.Instance).Load();
        Assert.Equal(70, reloaded.ImageCacheRamPercent);
    }
}
