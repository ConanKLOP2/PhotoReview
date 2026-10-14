using System.Text.Json;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>
/// FEAT-EXTERNAL-EDITORS (Q-R48 follow-up): the declared-editors list -- migration of the single path (config version 4), the
/// repair rules, the default lookup, the <c>{file}</c> command-line substitution and the RAW companion choice.
/// </summary>
public sealed class ExternalEditorsTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_paths, _fs, NullLog.Instance, (_, _) => { });

    // ---- BuildArguments: the {file} substitution ----

    [Theory]
    [InlineData("\"{file}\"", @"C:\Photos\My Trip\a b.jpg", "\"C:\\Photos\\My Trip\\a b.jpg\"")]
    [InlineData("{file}", @"C:\Photos\My Trip\a b.jpg", "\"C:\\Photos\\My Trip\\a b.jpg\"")]
    [InlineData("", @"C:\p\a.jpg", "\"C:\\p\\a.jpg\"")]
    [InlineData("   ", @"C:\p\a.jpg", "\"C:\\p\\a.jpg\"")]
    [InlineData("-open {file} -fast", @"C:\p\a.jpg", "-open \"C:\\p\\a.jpg\" -fast")]
    [InlineData("--file=\"{file}\" --x", @"C:\p\a.jpg", "--file=\"C:\\p\\a.jpg\" --x")]
    [InlineData("/edit", @"C:\p\a.jpg", "/edit \"C:\\p\\a.jpg\"")] // no token: the path is appended
    public void BuildArguments_ReplacesTheTokenAndQuotesTheWholePath(string template, string file, string expected) =>
        Assert.Equal(expected, ExternalEditors.BuildArguments(template, file));

    [Fact]
    public void BuildArguments_UnicodeAndBracketsPathsPassThroughUnchanged()
    {
        const string file = @"D:\Ảnh đẹp\Chuyến đi (2026) [final]\Hà Nội #1.jpg";

        Assert.Equal("\"" + file + "\"", ExternalEditors.BuildArguments(ExternalEditor.DefaultArguments, file));
    }

    [Fact]
    public void BuildArguments_EveryTokenIsReplaced_AndThePathIsNeverScannedAgain()
    {
        const string file = @"C:\p\{file}.jpg"; // a path that itself contains the token

        var result = ExternalEditors.BuildArguments("-a {file} -b \"{file}\"", file);

        Assert.Equal("-a \"C:\\p\\{file}.jpg\" -b \"C:\\p\\{file}.jpg\"", result);
    }

    [Fact]
    public void BuildArguments_AQuoteOrTrailingBackslashInThePathCannotBreakOutOfTheArgument()
    {
        Assert.Equal("\"C:\\p\\\\\\\"x.jpg\"", ExternalEditors.BuildArguments("\"{file}\"", "C:\\p\\\"x.jpg")); // C:\p\ + \" -> \\\"
        Assert.Equal("\"C:\\dir\\\\\"", ExternalEditors.BuildArguments("{file}", @"C:\dir\")); // trailing \ doubled before the closing quote
    }

    // ---- Reconcile: migration and repair ----

    [Fact]
    public void Reconcile_LegacyPathWithoutList_BecomesTheOnlyAndDefaultEditorNamedAfterTheFile()
    {
        var settings = new AppSettings { ExternalEditorPath = @"C:\Program Files\DxO\DxO PhotoLab.exe" };

        var repaired = ExternalEditors.Reconcile(settings);

        Assert.False(repaired); // the silent legacy migration is not a "repair"
        var editor = Assert.Single(settings.ExternalEditors);
        Assert.Equal("DxO PhotoLab", editor.Name);
        Assert.Equal(@"C:\Program Files\DxO\DxO PhotoLab.exe", editor.Path);
        Assert.Equal(ExternalEditor.DefaultArguments, editor.Arguments);
        Assert.Equal(editor.Id, settings.DefaultExternalEditorId);
        Assert.Equal(editor.Path, settings.ExternalEditorPath);
    }

    [Fact]
    public void Reconcile_NothingDeclared_StaysEmpty()
    {
        var settings = new AppSettings { DefaultExternalEditorId = "ghost" };

        ExternalEditors.Reconcile(settings);

        Assert.Empty(settings.ExternalEditors);
        Assert.Equal(string.Empty, settings.DefaultExternalEditorId);
        Assert.Equal(string.Empty, settings.ExternalEditorPath);
        Assert.Null(ExternalEditors.Default(settings));
    }

    [Fact]
    public void Reconcile_DropsBlankPathsAndRepeatedIds_AndFallsBackToTheFirstEditorWhenTheDefaultIsUnknown()
    {
        var settings = new AppSettings
        {
            ExternalEditors =
            [
                new ExternalEditor { Id = "a", Name = " Lightroom ", Path = @"C:\lr.exe", Arguments = "" },
                new ExternalEditor { Id = "b", Name = "Blank", Path = "   " },
                new ExternalEditor { Id = "a", Name = "Topaz", Path = @"C:\topaz.exe" },
            ],
            DefaultExternalEditorId = "no-such-id",
        };

        var repaired = ExternalEditors.Reconcile(settings);

        Assert.True(repaired);
        Assert.Equal(["Lightroom", "Topaz"], settings.ExternalEditors.Select(e => e.Name));
        Assert.Equal(2, settings.ExternalEditors.Select(e => e.Id).Distinct().Count());
        Assert.All(settings.ExternalEditors, e => Assert.Equal(ExternalEditor.DefaultArguments, e.Arguments));
        Assert.Equal("a", settings.DefaultExternalEditorId);
        Assert.Equal(@"C:\lr.exe", settings.ExternalEditorPath);
    }

    [Fact]
    public void Reconcile_ValidDefaultIsKept_AndThePathMirrorsIt()
    {
        var settings = new AppSettings
        {
            ExternalEditors =
            [
                new ExternalEditor { Id = "a", Path = @"C:\a.exe" },
                new ExternalEditor { Id = "b", Path = @"C:\b.exe" },
            ],
            DefaultExternalEditorId = "b",
            ExternalEditorPath = @"C:\stale.exe",
        };

        Assert.False(ExternalEditors.Reconcile(settings));

        Assert.Equal("b", settings.DefaultExternalEditorId);
        Assert.Equal(@"C:\b.exe", settings.ExternalEditorPath);
        Assert.Equal("b", ExternalEditors.Default(settings)!.Id);
    }

    [Fact]
    public void Reconcile_KeepsAtMostMaxEditors()
    {
        var settings = new AppSettings
        {
            ExternalEditors = [.. Enumerable.Range(0, ExternalEditors.MaxEditors + 5).Select(i => new ExternalEditor { Path = $@"C:\e{i}.exe" })],
        };

        Assert.True(ExternalEditors.Reconcile(settings));

        Assert.Equal(ExternalEditors.MaxEditors, settings.ExternalEditors.Count);
    }

    // ---- ApplyLegacyPath: the WPF window's single field ----

    [Fact]
    public void ApplyLegacyPath_ChangedPath_MovesTheDefaultEditor_ClearedPath_RemovesIt_NewPath_CreatesOne()
    {
        var settings = new AppSettings
        {
            ExternalEditors = [new ExternalEditor { Id = "a", Name = "Old", Path = @"C:\Old.exe" }, new ExternalEditor { Id = "b", Path = @"C:\b.exe" }],
            DefaultExternalEditorId = "a",
        };

        ExternalEditors.ApplyLegacyPath(settings, @"C:\New.exe");
        Assert.Equal(@"C:\New.exe", ExternalEditors.Find(settings, "a")!.Path);
        Assert.Equal("New", ExternalEditors.Find(settings, "a")!.Name); // an auto name follows the file
        Assert.Equal(2, settings.ExternalEditors.Count);

        ExternalEditors.ApplyLegacyPath(settings, "");
        Assert.Null(ExternalEditors.Find(settings, "a"));
        Assert.Equal("b", settings.DefaultExternalEditorId); // falls to the remaining editor
        Assert.Equal(@"C:\b.exe", settings.ExternalEditorPath);

        var empty = new AppSettings();
        ExternalEditors.ApplyLegacyPath(empty, @"C:\Z.exe");
        Assert.Equal(@"C:\Z.exe", Assert.Single(empty.ExternalEditors).Path);
        Assert.Equal(empty.ExternalEditors[0].Id, empty.DefaultExternalEditorId);
    }

    [Fact]
    public void Effective_LegacyPathOnly_IsOneImplicitEditor_AndAListWins()
    {
        var legacy = new AppSettings { ExternalEditorPath = @"C:\only.exe" };
        Assert.Equal(@"C:\only.exe", Assert.Single(ExternalEditors.Effective(legacy)).Path);
        Assert.Equal(@"C:\only.exe", ExternalEditors.EffectiveDefault(legacy)!.Path);
        Assert.Empty(ExternalEditors.Effective(new AppSettings()));

        legacy.ExternalEditors = [new ExternalEditor { Path = @"C:\listed.exe" }];
        Assert.Equal(@"C:\listed.exe", ExternalEditors.EffectiveDefault(legacy)!.Path);
    }

    // ---- RAW companion ----

    [Fact]
    public void ResolveFileToSend_RawOff_SendsTheShownFile()
    {
        var sent = ExternalEditors.ResolveFileToSend(@"C:\p\a.jpg", @"C:\p\a.CR3", sendRawOriginal: false, _ => true);

        Assert.Equal(@"C:\p\a.jpg", sent);
    }

    [Fact]
    public void ResolveFileToSend_PairedRawWins_ElseSameNameRawInTheFolder_ElseTheFile()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\a.CR3", @"C:\p\b.arw", @"C:\p\b.jpg", @"C:\p\c.jpg" };

        Assert.Equal(@"C:\p\a.CR3", ExternalEditors.ResolveFileToSend(@"C:\p\a.jpg", @"C:\p\a.CR3", true, existing.Contains)); // catalog pair
        Assert.Equal(@"C:\p\b.arw", ExternalEditors.ResolveFileToSend(@"C:\p\b.jpg", null, true, existing.Contains)); // sidecar by name
        Assert.Equal(@"C:\p\c.jpg", ExternalEditors.ResolveFileToSend(@"C:\p\c.jpg", null, true, existing.Contains)); // no RAW
        Assert.Equal(@"C:\p\a.JPG", ExternalEditors.ResolveFileToSend(@"C:\p\a.JPG", @"C:\p\gone.CR3", true, p => p == @"C:\p\a.JPG")); // pair missing on disk
        Assert.Equal(@"C:\p\a.dng", ExternalEditors.ResolveFileToSend(@"C:\p\a.dng", @"C:\p\a.CR3", true, existing.Contains)); // already RAW
    }

    // ---- Store: migration, round trip, normalizer ----

    [Fact]
    public void Load_ConfigVersion3WithLegacyPath_MigratesIntoTheListAndSavesAsVersion4()
    {
        _fs.AddFile(_paths.ConfigFile, """{ "ConfigVersion": 3, "UiLanguage": "en", "ExternalEditorPath": "C:\\Tools\\Affinity Photo.exe" }""");
        var store = NewStore();

        var loaded = store.Load();

        var editor = Assert.Single(loaded.ExternalEditors);
        Assert.Equal("Affinity Photo", editor.Name);
        Assert.Equal(@"C:\Tools\Affinity Photo.exe", editor.Path);
        Assert.Equal(editor.Id, loaded.DefaultExternalEditorId);
        Assert.Equal(AppSettings.CurrentConfigVersion, loaded.ConfigVersion);
        Assert.True(loaded.ExternalEditorSendsRawOriginal);
        Assert.Empty(store.LastLoadRepairs); // a migration is not a repair

        store.Save(loaded);
        using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.ConfigFile));
        Assert.Equal(4, doc.RootElement.GetProperty("ConfigVersion").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("ExternalEditors").GetArrayLength());
        Assert.Equal(@"C:\Tools\Affinity Photo.exe", doc.RootElement.GetProperty("ExternalEditorPath").GetString()); // the WPF build still reads it
    }

    [Fact]
    public void Migrate_InMemoryVersion3Settings_GetsTheEditorFromTheLegacyPath_AndIsStampedVersion4()
    {
        var settings = new AppSettings { ConfigVersion = 3, ExternalEditorPath = @"C:\Tools\Retouch.exe", UiLanguage = "en" };

        SettingsStore.Migrate(settings);

        Assert.Equal(AppSettings.CurrentConfigVersion, settings.ConfigVersion);
        Assert.Equal("Retouch", Assert.Single(settings.ExternalEditors).Name);
        Assert.Equal(settings.ExternalEditors[0].Id, settings.DefaultExternalEditorId);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheListTheDefaultTheArgumentsAndTheRawSwitch()
    {
        var settings = new AppSettings
        {
            ExternalEditors =
            [
                new ExternalEditor { Id = "lr", Name = "Lightroom", Path = @"C:\LR\lightroom.exe" },
                new ExternalEditor { Id = "topaz", Name = "Topaz AI", Path = @"C:\T\topaz.exe", Arguments = "--open \"{file}\"" },
            ],
            DefaultExternalEditorId = "topaz",
            ExternalEditorSendsRawOriginal = false,
        };
        settings.Shortcuts.OpenInDefaultEditor = "F6";
        var store = NewStore();

        store.Save(settings);
        var reloaded = NewStore().Load();

        Assert.Equal(["Lightroom", "Topaz AI"], reloaded.ExternalEditors.Select(e => e.Name));
        Assert.Equal("--open \"{file}\"", reloaded.ExternalEditors[1].Arguments);
        Assert.Equal("topaz", reloaded.DefaultExternalEditorId);
        Assert.Equal(@"C:\T\topaz.exe", reloaded.ExternalEditorPath);
        Assert.False(reloaded.ExternalEditorSendsRawOriginal);
        Assert.Equal("F6", reloaded.Shortcuts.OpenInDefaultEditor);
    }

    [Fact]
    public void Load_HandEditedList_IsRepairedAndReported()
    {
        _fs.AddFile(_paths.ConfigFile, """
            { "ConfigVersion": 4, "UiLanguage": "en",
              "ExternalEditors": [ { "Id": "x", "Name": "Ok", "Path": "C:\\ok.exe" }, { "Id": "y", "Path": "" } ],
              "DefaultExternalEditorId": "missing" }
            """);
        var store = NewStore();

        var loaded = store.Load();

        Assert.Equal("x", Assert.Single(loaded.ExternalEditors).Id);
        Assert.Equal("x", loaded.DefaultExternalEditorId);
        Assert.Contains(nameof(AppSettings.ExternalEditors), store.LastLoadRepairs);
    }

    [Fact]
    public void Defaults_NothingDeclared_RawOn_ShortcutEmptyAndOptional()
    {
        var settings = new AppSettings();

        Assert.Empty(settings.ExternalEditors);
        Assert.True(settings.ExternalEditorSendsRawOriginal);
        Assert.Equal(string.Empty, settings.Shortcuts.OpenInDefaultEditor);
        Assert.Contains(nameof(ShortcutMappings.OpenInDefaultEditor), ShortcutMappings.OptionalNames);
    }

    [Fact]
    public void ValidateShortcuts_DefaultEditorKeyOnAnActionKey_IsADuplicate_AndEmptyIsAllowed()
    {
        var validator = new SettingsValidator(new AlwaysValidKeyNameValidator());
        var settings = new AppSettings();
        Assert.Null(validator.ValidateShortcuts(settings)); // empty by default

        settings.Shortcuts.OpenInDefaultEditor = settings.Actions[0].Shortcut;
        Assert.NotNull(validator.ValidateShortcuts(settings));

        settings.Shortcuts.OpenInDefaultEditor = "F9";
        Assert.Null(validator.ValidateShortcuts(settings));
    }

    private sealed class AlwaysValidKeyNameValidator : PhotoReview.Core.Abstractions.IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => true;
    }
}
