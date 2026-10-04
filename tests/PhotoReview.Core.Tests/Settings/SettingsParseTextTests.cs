using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// <see cref="SettingsStore.ParseText"/> is the one parse pipeline behind both start-up (<see cref="SettingsStore.Load"/>) and
/// Settings > Import: the valid part of a file is kept, the bad parts are reset to defaults and named.
/// </summary>
public sealed class SettingsParseTextTests
{
    private static readonly AppSettings Defaults = new();

    [Fact]
    public void ParseText_MistypedValue_KeepsTheRestAndNamesTheDefaultedOne()
    {
        var result = SettingsStore.ParseText("""
            {"ConfigVersion":3,"UiLanguage":"en","ClickZoomPercent":"abc","KeyboardZoomStepPercent":40,
             "Shortcuts":{"Next":"N"},"Actions":[{"Name":"Mine","Shortcut":"K"}]}
            """);

        Assert.Equal(Defaults.ClickZoomPercent, result.Settings.ClickZoomPercent);
        Assert.Contains(nameof(AppSettings.ClickZoomPercent), result.Repairs);
        Assert.Equal(40, result.Settings.KeyboardZoomStepPercent);
        Assert.Equal("N", result.Settings.Shortcuts.Next);
        Assert.Equal("Mine", Assert.Single(result.Settings.Actions).Name);
        Assert.NotNull(result.SalvageCause);
    }

    [Fact]
    public void ParseText_UnparsableEnum_ResetsToDefaultAndReportsIt()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"LoadingMode":"Bogus","KeyboardZoomStepPercent":40}""");

        Assert.Equal(Defaults.LoadingMode, result.Settings.LoadingMode);
        Assert.Contains(nameof(AppSettings.LoadingMode), result.Repairs);
        Assert.Equal(40, result.Settings.KeyboardZoomStepPercent);
    }

    [Fact]
    public void ParseText_OutOfRangeNumber_IsResetAndReported()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"ClickZoomPercent":999999}""");

        Assert.True(result.Settings.ClickZoomPercent <= AppSettings.MaxClickZoomPercent);
        Assert.Contains(nameof(AppSettings.ClickZoomPercent), result.Repairs);
        Assert.Empty(result.Salvaged);
        Assert.Null(result.SalvageCause);
    }

    [Fact]
    public void ParseText_NoConfigVersionKey_IsTreatedAsLegacyAndMigrated()
    {
        var result = SettingsStore.ParseText("""{"KeyboardZoomStepPercent":40}""");

        Assert.Equal("vi", result.Settings.UiLanguage);
        Assert.Equal(AppSettings.CurrentConfigVersion, result.Settings.ConfigVersion);
        Assert.NotNull(result.Settings.Actions);
        Assert.Equal(40, result.Settings.KeyboardZoomStepPercent);
    }

    [Fact]
    public void ParseText_ConfigVersion3_KeepsItsLanguage()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"UiLanguage":"en"}""");

        Assert.Equal("en", result.Settings.UiLanguage);
    }

    [Fact]
    public void ParseText_NewerConfigVersion_IsNotDowngradedOrRejected()
    {
        var newer = AppSettings.CurrentConfigVersion + 5;

        var result = SettingsStore.ParseText($$"""{"ConfigVersion":{{newer}},"UiLanguage":"en","FutureThing":1}""");

        Assert.Equal(newer, result.Settings.ConfigVersion);
        Assert.Equal("en", result.Settings.UiLanguage);
        Assert.Empty(result.Repairs);
    }

    [Fact]
    public void ParseText_ConflictingOptionalShortcut_IsDisabledAndReportedWithTheShortcutsPrefix()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"Actions":[{"Name":"Mine","Shortcut":"K"}]}""");

        Assert.Equal("", result.Settings.Shortcuts.ToggleKeepZoom); // default "K" collides with the action
        Assert.Contains("Shortcuts." + nameof(ShortcutMappings.ToggleKeepZoom), result.Repairs);
        Assert.Contains(nameof(ShortcutMappings.ToggleKeepZoom), result.DisabledShortcuts);
        Assert.DoesNotContain(nameof(ShortcutMappings.ToggleKeepZoom), result.ResetSettings);
    }

    [Fact]
    public void ParseText_MissingKeys_FallBackToDefaultsWhileThePresentOnesAreKept()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"UiLanguage":"en","KeyboardZoomStepPercent":40}""");

        Assert.Equal(40, result.Settings.KeyboardZoomStepPercent);
        Assert.Equal(Defaults.ClickZoomPercent, result.Settings.ClickZoomPercent);
        Assert.Equal(Defaults.ConfirmBeforeDelete, result.Settings.ConfirmBeforeDelete);
        Assert.Empty(result.Repairs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"ConfigVersion\":3")]
    public void ParseText_NotAJsonObject_ThrowsJsonException(string text)
    {
        Assert.ThrowsAny<JsonException>(() => SettingsStore.ParseText(text));
    }

    [Fact]
    public void ParseText_JsonNull_IsFlaggedSoImportCanRejectIt()
    {
        var result = SettingsStore.ParseText("null");

        Assert.True(result.IsJsonNull);
    }

    [Fact]
    public void ParseText_NullActionsAndShortcuts_AreFilledWithDefaults()
    {
        var result = SettingsStore.ParseText("""{"ConfigVersion":3,"Actions":null,"Shortcuts":null}""");

        Assert.NotEmpty(result.Settings.Actions);
        Assert.Equal(new ShortcutMappings().Next, result.Settings.Shortcuts.Next);
    }

    public static TheoryData<string> EquivalenceInputs() =>
    [
        """{"ConfigVersion":3,"UiLanguage":"en"}""",
        """{"ConfigVersion":3,"ClickZoomPercent":"abc","Shortcuts":{"Next":"N"},"Actions":[{"Name":"Mine","Shortcut":"K"}]}""",
        """{"ConfigVersion":3,"LoadingMode":"Bogus","ClickZoomPercent":999999}""",
        """{"KeyboardZoomStepPercent":40}""",
        """{"ConfigVersion":3,"Actions":[{"Name":"Mine","Shortcut":"K"}]}""",
        """{"ConfigVersion":3,"Actions":null,"Shortcuts":null}""",
        """{"ConfigVersion":99,"FutureThing":1}""",
        "null",
    ];

    [Theory]
    [MemberData(nameof(EquivalenceInputs))]
    public void ParseTextAndLoad_SameFileText_GiveIdenticalSettingsAndRepairs(string text)
    {
        var fs = new InMemoryFileSystem();
        const string path = @"C:\cfg\other.json"; // an explicit path: Load does not write repairs back
        fs.AddFile(path, text);
        var store = new SettingsStore(new AppPaths(@"C:\Users\test\AppData\Local"), fs, NullLog.Instance);

        var loaded = store.Load(path);
        var parsed = SettingsStore.ParseText(text);

        Assert.Equal(JsonSerializer.Serialize(loaded, AppSettingsJsonContext.Default.AppSettings),
            JsonSerializer.Serialize(parsed.Settings, AppSettingsJsonContext.Default.AppSettings));
        Assert.Equal(store.LastLoadRepairs, parsed.Repairs);
    }

    [Theory(DisplayName = "F07: a pre-v3 file that names a UI language keeps it; only an absent property gets the Vietnamese migration default")]
    [InlineData("""{"UiLanguage":"en"}""", "en")]
    [InlineData("""{"ConfigVersion":2,"UiLanguage":"en"}""", "en")]
    [InlineData("""{"ConfigVersion":2,"UiLanguage":"auto"}""", "auto")]
    [InlineData("""{"ConfigVersion":2}""", "vi")]
    [InlineData("""{"ConfigVersion":1,"KeyboardZoomStepPercent":40}""", "vi")]
    public void ParseText_PreV3_LanguageMigrationOnlyWhenAbsent(string json, string expected)
    {
        var result = SettingsStore.ParseText(json);

        Assert.Equal(expected, result.Settings.UiLanguage);
        Assert.Equal(AppSettings.CurrentConfigVersion, result.Settings.ConfigVersion);
    }
}
