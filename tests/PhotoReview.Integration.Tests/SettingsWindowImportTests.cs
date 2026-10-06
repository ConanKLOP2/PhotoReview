using System.IO;
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

    [Fact(DisplayName = "Export writes a file that Import reads back into the same settings")]
    public async Task ExportSettingsTo_ThenImportSettingsFrom_RoundTripsTheSettings()
    {
        var dir = Directory.CreateTempSubdirectory("pr-settings-rt-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            await StaTestHost.RunAsync(() =>
            {
                var source = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 37, UiLanguage = "en" });
                var target = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 20, UiLanguage = "en" });
                try
                {
                    source.InvalidSettingsWarning = m => Assert.Fail("export must not warn: " + m);
                    target.InvalidSettingsWarning = m => Assert.Fail("import must not warn: " + m);
                    target.ImportRepairsNotice = m => Assert.Fail("nothing should be reset: " + m);

                    source.ExportSettingsTo(path);
                    target.ImportSettingsFrom(path);

                    Assert.True(File.Exists(path));
                    Assert.Equal(37, target.Settings.KeyboardZoomStepPercent);
                }
                finally { source.Close(); target.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Export to an unwritable path warns instead of throwing")]
    public async Task ExportSettingsTo_PathIsADirectory_WarnsWithTheFailureText()
    {
        var dir = Directory.CreateTempSubdirectory("pr-settings-exp-").FullName; // a directory cannot be written as a file
        try
        {
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings());
                try
                {
                    var warnings = new List<string>();
                    window.InvalidSettingsWarning = warnings.Add;

                    window.ExportSettingsTo(dir);

                    Assert.Single(warnings);
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Import of a missing file warns and keeps the previous settings")]
    public async Task ImportSettingsFrom_MissingFile_WarnsAndKeepsSettings()
    {
        var dir = Directory.CreateTempSubdirectory("pr-settings-imp-").FullName;
        try
        {
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 33 });
                try
                {
                    var warnings = new List<string>();
                    window.InvalidSettingsWarning = warnings.Add;
                    var before = window.Settings;

                    window.ImportSettingsFrom(Path.Combine(dir, "does-not-exist.json"));

                    Assert.Single(warnings);
                    Assert.Same(before, window.Settings);
                    Assert.Equal(33, window.Settings.KeyboardZoomStepPercent);
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Import of a half-valid file routes the reset values to the notice, not the error warning")]
    public async Task ImportSettingsFrom_HalfValidFile_ShowsRepairNoticeNotWarning()
    {
        var dir = Directory.CreateTempSubdirectory("pr-settings-imp-").FullName;
        try
        {
            var path = Path.Combine(dir, "half.json");
            File.WriteAllText(path, """{"ConfigVersion":3,"UiLanguage":"en","ClickZoomPercent":"abc","KeyboardZoomStepPercent":40}""");
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings());
                try
                {
                    var notices = new List<string>();
                    window.ImportRepairsNotice = notices.Add;
                    window.InvalidSettingsWarning = m => Assert.Fail("a usable file must not raise the error warning: " + m);

                    window.ImportSettingsFrom(path);

                    Assert.Contains(nameof(AppSettings.ClickZoomPercent), Assert.Single(notices));
                    Assert.Equal(40, window.Settings.KeyboardZoomStepPercent);
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Import of an unusable file routes the error to the warning, not the repair notice")]
    public async Task ImportSettingsFrom_NotJson_ShowsWarningNotNotice()
    {
        var dir = Directory.CreateTempSubdirectory("pr-settings-imp-").FullName;
        try
        {
            var path = Path.Combine(dir, "bad.json");
            File.WriteAllText(path, "this is not json");
            await StaTestHost.RunAsync(() =>
            {
                var window = new SettingsWindow(new AppSettings { KeyboardZoomStepPercent = 33 });
                try
                {
                    var warnings = new List<string>();
                    window.InvalidSettingsWarning = warnings.Add;
                    window.ImportRepairsNotice = m => Assert.Fail("no repair notice for an unusable file: " + m);

                    window.ImportSettingsFrom(path);

                    Assert.Single(warnings);
                    Assert.Equal(33, window.Settings.KeyboardZoomStepPercent);
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
