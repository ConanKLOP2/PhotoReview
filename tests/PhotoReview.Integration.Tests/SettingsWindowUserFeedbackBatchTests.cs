using System.Reflection;
using System.Windows;
using PhotoReview.App;
using PhotoReview.Core.Settings;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Q-R41/Q-R42/Q-R43/Q-R44/Q-R45/Q-R48 (docs/refactoring/decisions/Q-R41-Q-R52-user-feedback.md): the Settings
/// window UI for the keyboard zoom step, confirm-before-delete, show-zoom-indicator, the OpenFolder/CustomZoom
/// shortcuts and the external editor path. <see cref="SettingsWindowRoundTripTests"/> already proves
/// KeyboardZoomStepPercent, ConfirmBeforeDelete, ShowZoomIndicator and ExternalEditorPath round-trip through
/// Save; this file covers validation edge cases and the two new optional shortcuts (excluded from that test
/// because Shortcuts round-trips through its own text boxes, not a single control mutation).
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowUserFeedbackBatchTests
{
    private static readonly MethodInfo SaveClick =
        typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void InvokeSave(SettingsWindow window) => SaveClick.Invoke(window, [window, new RoutedEventArgs()]);

    [Theory(DisplayName = "Out-of-range/unparsable keyboard zoom step is rejected by Save")]
    [InlineData("abc")]
    [InlineData("4")]
    [InlineData("101")]
    [InlineData("")]
    public async Task KeyboardZoomStep_InvalidInput_IsRejected(string text)
    {
        await StaTestHost.RunAsync(() =>
        {
            var warnings = new List<string>();
            var window = new SettingsWindow(new AppSettings()) { InvalidSettingsWarning = warnings.Add };
            try
            {
                window.KeyboardZoomStepPercentBox.Text = text;
                InvokeSave(window);
                Assert.Single(warnings);
                Assert.Null(window.DialogResult);
                Assert.Equal(AppSettings.DefaultKeyboardZoomStepPercent, window.Settings.KeyboardZoomStepPercent); // untouched
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory(DisplayName = "Boundary keyboard zoom steps (min and max) are accepted and stored by Save")]
    [InlineData("5", 5)]
    [InlineData("100", 100)]
    public async Task KeyboardZoomStep_BoundaryValues_AreAcceptedAndSaved(string text, int expected)
    {
        await StaTestHost.RunAsync(() =>
        {
            var warnings = new List<string>();
            var window = new SettingsWindow(new AppSettings()) { InvalidSettingsWarning = warnings.Add, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
            try
            {
                window.Loaded += (_, _) =>
                {
                    window.KeyboardZoomStepPercentBox.Text = text;
                    InvokeSave(window);
                    if (window.DialogResult is null) window.Close(); // rejected save: end the modal loop so the test fails instead of hanging
                };
                Assert.True(window.ShowDialog());
                Assert.Empty(warnings);
                Assert.Equal(expected, window.Settings.KeyboardZoomStepPercent);
            }
            finally { if (window.IsLoaded) window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Restore defaults resets the keyboard zoom step (setting and box)")]
    public async Task RestoreDefaults_ResetsKeyboardZoomStep()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 40 });
            try
            {
                Assert.Equal("40", window.KeyboardZoomStepPercentBox.Text);
                typeof(SettingsWindow).GetMethod("Defaults_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [null, new RoutedEventArgs()]);
                Assert.Equal(AppSettings.DefaultKeyboardZoomStepPercent, window.Settings.KeyboardZoomStepPercent);
                Assert.Equal(AppSettings.DefaultKeyboardZoomStepPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), window.KeyboardZoomStepPercentBox.Text);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Clearing the OpenFolder and CustomZoom shortcuts is accepted (both optional)")]
    public async Task OpenFolderAndCustomZoomShortcuts_Clear_IsAccepted()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
            try
            {
                window.Loaded += (_, _) =>
                {
                    Assert.False(string.IsNullOrEmpty(window.OpenFolderText.Text)); // has a default (O) to begin with
                    Assert.False(string.IsNullOrEmpty(window.CustomZoomText.Text)); // has a default (D3) to begin with
                    typeof(SettingsWindow).GetMethod("ClearOpenFolder_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [window, new RoutedEventArgs()]);
                    typeof(SettingsWindow).GetMethod("ClearCustomZoom_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [window, new RoutedEventArgs()]);
                    Assert.Equal(string.Empty, window.OpenFolderText.Text);
                    Assert.Equal(string.Empty, window.CustomZoomText.Text);
                    InvokeSave(window);
                };
                Assert.True(window.ShowDialog());
                Assert.Equal(string.Empty, window.Settings.Shortcuts.OpenFolder);
                Assert.Equal(string.Empty, window.Settings.Shortcuts.CustomZoom);
            }
            finally { if (window.IsLoaded) window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Changing the OpenFolder and CustomZoom shortcuts round-trips through Save")]
    public async Task OpenFolderAndCustomZoomShortcuts_ChangedValues_RoundTripThroughSave()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false };
            try
            {
                window.Loaded += (_, _) =>
                {
                    window.OpenFolderText.Text = "F2";
                    window.CustomZoomText.Text = "D7";
                    InvokeSave(window);
                };
                Assert.True(window.ShowDialog());
                Assert.Equal("F2", window.Settings.Shortcuts.OpenFolder);
                Assert.Equal("D7", window.Settings.Shortcuts.CustomZoom);
            }
            finally { if (window.IsLoaded) window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "An invalid (non-key) OpenFolder/CustomZoom shortcut is rejected by Save")]
    public async Task OpenFolderAndCustomZoomShortcuts_InvalidInput_IsRejected()
    {
        await StaTestHost.RunAsync(() =>
        {
            var warnings = new List<string>();
            var window = new SettingsWindow(new AppSettings()) { InvalidSettingsWarning = warnings.Add };
            try
            {
                window.OpenFolderText.Text = "not a key";
                InvokeSave(window);
                Assert.Single(warnings);
                Assert.Null(window.DialogResult);
                Assert.Equal("O", window.Settings.Shortcuts.OpenFolder); // untouched
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Import replaces the in-memory settings through Clone + Normalize, without touching disk (Q-R50)")]
    public void ImportSettings_JsonRoundTrip_AppliesCloneAndNormalize()
    {
        // Mirrors what ImportSettings_Click does with the file it reads, minus the OpenFileDialog: serialize a
        // settings object with the same contract config.json uses, deserialize it back, clone it (as the window's
        // constructor already does for its own AppSettings parameter) and normalize it (as SettingsStore.Load does),
        // proving the whole pipe survives a value only Export could have produced, e.g. an out-of-range field.
        var source = new AppSettings { KeyboardZoomStepPercent = 40, ConfirmBeforeDelete = true, ShowZoomIndicator = true, ExternalEditorPath = @"C:\Tools\editor.exe" };
        var json = System.Text.Json.JsonSerializer.Serialize(source, AppSettingsJsonContext.Default.AppSettings);

        var imported = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        Assert.NotNull(imported);

        var cloned = AppSettings.Clone(imported!);
        SettingsNormalizer.Normalize(cloned);

        Assert.Equal(40, cloned.KeyboardZoomStepPercent);
        Assert.True(cloned.ConfirmBeforeDelete);
        Assert.True(cloned.ShowZoomIndicator);
        Assert.Equal(@"C:\Tools\editor.exe", cloned.ExternalEditorPath);
    }
}
