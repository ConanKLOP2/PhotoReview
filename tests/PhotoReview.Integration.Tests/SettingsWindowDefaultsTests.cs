using System.Reflection;
using System.Text.Json;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Behavior replacement for the former "Settings defaults reset compare options" source-presence check:
/// runs the real Settings window's Restore-defaults handler and reads the resulting settings and controls.
/// </summary>
[Trait("Category", "UI")]
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

    [Fact(DisplayName = "Restore defaults selects the default decoder (WicDirect) and hides the EXIF line")]
    public async Task RestoreDefaults_UsesDecoderAndExifDefaults()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { DecoderBackend = PhotoReview.Core.Model.DecoderBackend.Wpf, ShowExifInfo = true });
            try
            {
                Assert.Equal(PhotoReview.Core.Model.DecoderBackend.Wpf, window.Settings.DecoderBackend);
                Assert.True(window.Settings.ShowExifInfo);

                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);

                Assert.Equal(new AppSettings().DecoderBackend, window.Settings.DecoderBackend);
                Assert.Equal(PhotoReview.Core.Model.DecoderBackend.WicDirect, window.Settings.DecoderBackend);
                Assert.False(window.Settings.ShowExifInfo);
                Assert.Equal(1, window.DecoderBackendCombo.SelectedIndex);
                Assert.False(window.ShowExifInfoCheck.IsChecked);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Properties Restore-defaults intentionally leaves untouched, each with a one-line reason. Everything else on
    /// <see cref="AppSettings"/> must be reset by <c>Defaults_Click</c> -- this is the regression net for the whole
    /// class of "a new setting was added but Restore defaults forgot to reset it" bug (see e.g. ShowRecycleMenuItem
    /// and ImageTransition/ImageTransitionMs, both fixed alongside this test).
    /// </summary>
    private static readonly HashSet<string> Keepers = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ConfigVersion),       // schema version -- not a user preference
        nameof(AppSettings.Actions),             // user's own action profiles -- Restore defaults must not discard them
        nameof(AppSettings.UiLanguage),          // UI language choice -- unrelated to the rest of the settings being reset
        nameof(AppSettings.LastMoveToFolder),    // remembered last-used folder -- convenience state, not a setting
        nameof(AppSettings.LastCopyToFolder),    // remembered last-used folder -- convenience state, not a setting
    };

    /// <summary>Mutates every public read/write instance property of <paramref name="settings"/> away from its <c>new AppSettings()</c> default.</summary>
    private static void MutateEveryProperty(AppSettings settings)
    {
        var defaults = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            var defaultValue = prop.GetValue(defaults);
            var mutated = MutatedValue(prop.Name, prop.PropertyType, defaultValue);
            prop.SetValue(settings, mutated);
        }
    }

    private static object? MutatedValue(string name, Type type, object? defaultValue)
    {
        if (type == typeof(bool)) return !(bool)defaultValue!;
        if (type == typeof(int)) return (int)defaultValue! + 1;
        if (type == typeof(long)) return (long)defaultValue! + 1;
        if (type == typeof(double)) return (double)defaultValue! + 1;
        if (type == typeof(string)) return defaultValue + "x"; // null default + "x" => "x"
        if (type.IsEnum)
        {
            foreach (var value in Enum.GetValues(type))
                if (!Equals(value, defaultValue)) return value;
            throw new InvalidOperationException($"Enum {type} has no value other than the default -- teach this test another one.");
        }
        if (type == typeof(ShortcutMappings)) return new ShortcutMappings { Next = "F12" };
        if (type == typeof(List<ReviewAction>))
            return new List<ReviewAction> { new() { Name = "Mutated", Shortcut = "F9", Operation = PhotoReview.Core.Model.FileOperationType.Copy, Destination = "Mutated-Dest", Confirm = true } };

        Assert.Fail($"Teach this test how to change {name} ({type}).");
        return null; // unreachable
    }

    [Fact(DisplayName = "Restore defaults resets every setting except the documented keepers")]
    public async Task RestoreDefaults_ResetsEverySetting_ExceptDocumentedKeepers()
    {
        await StaTestHost.RunAsync(() =>
        {
            var mutated = new AppSettings();
            MutateEveryProperty(mutated);

            var window = new SettingsWindow(mutated) { InvalidSettingsWarning = _ => { } };
            try
            {
                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);

                var fresh = new AppSettings();
                var mismatches = new List<string>();
                foreach (var prop in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite) continue;
                    if (Keepers.Contains(prop.Name)) continue;

                    var actual = JsonSerializer.Serialize(prop.GetValue(window.Settings));
                    var expected = JsonSerializer.Serialize(prop.GetValue(fresh));
                    if (actual != expected) mismatches.Add(prop.Name);
                }

                Assert.True(mismatches.Count == 0,
                    "Defaults_Click did not reset: " + string.Join(", ", mismatches) +
                    ". Reset the property in Defaults_Click, or if it must survive Restore defaults, add it to Keepers with a reason.");

                foreach (var name in Keepers)
                {
                    var prop = typeof(AppSettings).GetProperty(name)!;
                    var actualAfterReset = JsonSerializer.Serialize(prop.GetValue(window.Settings));
                    var mutatedValue = JsonSerializer.Serialize(prop.GetValue(mutated));
                    Assert.True(actualAfterReset == mutatedValue, $"Keeper {name} should survive Restore defaults unchanged.");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
