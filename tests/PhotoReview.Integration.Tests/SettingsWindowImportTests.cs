using PhotoReview.App;
using PhotoReview.Integration.Tests.Infrastructure;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// Settings > Import goes through <see cref="SettingsStore.ParseText"/>, the same pipeline as start-up: the valid part of a file
/// is kept, bad values are reset and named, and an unusable file leaves the window as it was.
/// </summary>
[Trait("Category", "UI")]
[Collection("GlobalState")]
public sealed class SettingsWindowImportTests
{
    [Fact(DisplayName = "Import keeps the valid part of a half-valid file and names the value it reset")]
    public async Task TryApplyImportedJson_HalfValidFile_KeepsValidPartAndReportsTheResetValue()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                var ok = window.TryApplyImportedJson("""
                    {"ConfigVersion":3,"UiLanguage":"en","ClickZoomPercent":"abc","KeyboardZoomStepPercent":40,
                     "Shortcuts":{"Next":"N"},"Actions":[{"Name":"Mine","Shortcut":"M"}]}
                    """, out var message);

                Assert.True(ok);
                Assert.Contains(nameof(AppSettings.ClickZoomPercent), message);
                Assert.Equal(new AppSettings().ClickZoomPercent, window.Settings.ClickZoomPercent);
                Assert.Equal(40, window.Settings.KeyboardZoomStepPercent);
                Assert.Equal("N", window.Settings.Shortcuts.Next);
                Assert.Equal("Mine", Assert.Single(window.Settings.Actions).Name);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Import of a fully valid file reports nothing")]
    public async Task TryApplyImportedJson_ValidFile_ReturnsNoMessage()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                var ok = window.TryApplyImportedJson("""{"ConfigVersion":3,"UiLanguage":"en","KeyboardZoomStepPercent":40}""", out var message);

                Assert.True(ok);
                Assert.Null(message);
                Assert.Equal(40, window.Settings.KeyboardZoomStepPercent);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Import of a file that is not JSON fails and keeps the previous settings")]
    public async Task TryApplyImportedJson_NotJson_ReturnsErrorAndRestoresPreviousSettings()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 33 });
            try
            {
                var before = window.Settings;

                var ok = window.TryApplyImportedJson("this is not json", out var message);

                Assert.False(ok);
                Assert.False(string.IsNullOrWhiteSpace(message));
                Assert.Same(before, window.Settings);
                Assert.Equal(33, window.Settings.KeyboardZoomStepPercent);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Import of a legacy unversioned file is migrated like at start-up (Vietnamese UI)")]
    public async Task TryApplyImportedJson_UnversionedFile_IsMigratedToVietnamese()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings { UiLanguage = "en" });
            try
            {
                var ok = window.TryApplyImportedJson("""{"KeyboardZoomStepPercent":40}""", out _);

                Assert.True(ok);
                Assert.Equal("vi", window.Settings.UiLanguage);
                Assert.Equal(40, window.Settings.KeyboardZoomStepPercent);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact(DisplayName = "Import turns off a conflicting optional shortcut and says so")]
    public async Task TryApplyImportedJson_ConflictingOptionalShortcut_IsDisabledAndReported()
    {
        await StaTestHost.RunAsync(() =>
        {
            var window = new SettingsWindow(new AppSettings());
            try
            {
                var ok = window.TryApplyImportedJson("""{"ConfigVersion":3,"UiLanguage":"en","Actions":[{"Name":"Mine","Shortcut":"K"}]}""", out var message);

                Assert.True(ok);
                Assert.Equal("", window.Settings.Shortcuts.ToggleKeepZoom);
                Assert.Contains(nameof(ShortcutMappings.ToggleKeepZoom), message);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}