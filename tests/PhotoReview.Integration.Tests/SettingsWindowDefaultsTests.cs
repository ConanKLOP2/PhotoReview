using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Behavior replacement for the former "Settings defaults reset compare options" source-presence check:
/// runs the real Settings window's Restore-defaults handler and reads the resulting settings and controls.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsWindowDefaultsTests
{
    [Fact(DisplayName = "Restore defaults re-enables both compare options in the settings and in the checkboxes")]
    public async Task RestoreDefaults_ReEnablesCompareOptions()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { CompareHashEnabled = false, CompareSizeEnabled = false });
            try
            {
                Assert.False(window.Settings.CompareHashEnabled);
                Assert.False(window.CompareHashCheck.IsChecked);
                Assert.False(window.CompareSizeCheck.IsChecked);

                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);

                Assert.True(window.Settings.CompareHashEnabled);
                Assert.True(window.Settings.CompareSizeEnabled);
                Assert.True(window.CompareHashCheck.IsChecked);
                Assert.True(window.CompareSizeCheck.IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
