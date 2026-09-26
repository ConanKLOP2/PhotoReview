using System.IO;
using System.Text;
using System.Text.Json;
using PhotoReview.App.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests.Localization;

/// <summary>I18N L08 "Export strings to translate": content of &lt;code&gt;.todo.json. No global state is touched.</summary>
public sealed class TranslationExportTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Dispose();

    private static LanguageCatalog Catalog(string json)
    {
        Assert.True(LanguageCatalog.TryParse(json, "test", out var catalog, new List<string>()));
        return catalog;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> TextKeys(JsonElement root) =>
        [.. root.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith('_'))];

    private static List<string> MissingList(JsonElement root) =>
        [.. root.GetProperty(TranslationExport.MissingKey).EnumerateArray().Select(e => e.GetString()!)];

    private static List<string> EnglishKeysSorted(Func<string, bool> where) =>
        [.. BuiltInCatalog.English.Entries.Keys.Where(where).Order(StringComparer.Ordinal)];

    [Fact]
    public void BuildJson_ListsEveryKey_MissingFirstInEnglish_ThenCurrentTranslations_MetaAndNotes()
    {
        var partial = Catalog("""
            { "_meta": { "code": "xx", "name": "Test", "nativeName": "Testish", "plural": "one-other", "authors": ["A"] },
              "app.title": "Tst", "common.cancel": "Nope" }
            """);
        var notes = new Dictionary<string, string> { ["common.save"] = "Save button.", ["app.title"] = "Product name." };

        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "xx", [partial], notes));

        var meta = root.GetProperty("_meta");
        Assert.Equal("xx", meta.GetProperty("code").GetString());
        Assert.Equal("Testish", meta.GetProperty("nativeName").GetString());
        Assert.Equal("one-other", meta.GetProperty("plural").GetString());
        Assert.Equal("A", Assert.Single(meta.GetProperty("authors").EnumerateArray()).GetString());

        // Every English key once: the untranslated ones (sorted) first, then the translated ones (sorted).
        var missing = EnglishKeysSorted(k => k is not "app.title" and not "common.cancel");
        Assert.Equal([.. missing, "app.title", "common.cancel"], TextKeys(root));
        Assert.Equal(missing, MissingList(root));
        Assert.Equal(BuiltInCatalog.English.Entries["common.save"], root.GetProperty("common.save").GetString());
        Assert.Equal("Tst", root.GetProperty("app.title").GetString());
        Assert.Equal("Nope", root.GetProperty("common.cancel").GetString());

        // Notes for translated keys too, not only for missing ones.
        var exportedNotes = root.GetProperty("_notes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal("Save button.", exportedNotes["common.save"]);
        Assert.Equal("Product name.", exportedNotes["app.title"]);
        Assert.Equal(2, exportedNotes.Count);
        Assert.Contains("_help", root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void BuildJson_English_ListsEveryKeyWithItsText_NothingMissing()
    {
        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "en", [], new Dictionary<string, string>()));

        Assert.Equal(EnglishKeysSorted(_ => true), TextKeys(root));
        Assert.Empty(MissingList(root));
        Assert.Equal(BuiltInCatalog.English.Entries["common.save"], root.GetProperty("common.save").GetString());
        Assert.Equal("en", root.GetProperty("_meta").GetProperty("code").GetString());
    }

    [Fact]
    public void BuildJson_English_UserOverrideReplacesBuiltInText()
    {
        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "en",
            [Catalog("""{ "_meta": { "code": "en" }, "common.save": "Keep" }""")], new Dictionary<string, string>()));

        Assert.Equal("Keep", root.GetProperty("common.save").GetString());
        Assert.Equal(BuiltInCatalog.English.Entries.Count, TextKeys(root).Count);
    }

    [Fact]
    public void BuildJson_PluralNone_SkipsUntranslatedOneForms_KeepsTranslatedOnes()
    {
        var oneKeys = EnglishKeysSorted(k => k.EndsWith(".one", StringComparison.Ordinal)
            && BuiltInCatalog.English.Entries.ContainsKey(k[..^".one".Length] + ".other"));
        Assert.True(oneKeys.Count >= 2, "English must have plural pairs for this test");
        var kept = oneKeys[0];
        var translation = Catalog($$"""{ "_meta": { "code": "xx", "plural": "none" }, "{{kept}}": "One" }""");

        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "xx", [translation], new Dictionary<string, string>()));

        var keys = TextKeys(root);
        Assert.All(oneKeys.Skip(1), k => Assert.DoesNotContain(k, keys));
        Assert.Equal("One", root.GetProperty(kept).GetString());
        Assert.DoesNotContain(kept, MissingList(root));
        Assert.Contains(oneKeys[1][..^".one".Length] + ".other", MissingList(root)); // .other is still work to do
    }

    [Fact]
    public void BuildJson_PluralOneOther_ListsUntranslatedOneFormsAsMissing()
    {
        var oneKey = EnglishKeysSorted(k => k.EndsWith(".one", StringComparison.Ordinal))[0];

        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "xx",
            [Catalog("""{ "_meta": { "code": "xx", "plural": "one-other" } }""")], new Dictionary<string, string>()));

        Assert.Contains(oneKey, MissingList(root));
    }

    [Fact]
    public void BuildJson_DropsKeysEnglishDoesNotKnow()
    {
        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "xx",
            [Catalog("""{ "_meta": { "code": "xx" }, "no.such.key": "x", "app.title": "T" }""")], new Dictionary<string, string>()));

        Assert.DoesNotContain("no.such.key", TextKeys(root));
        Assert.Equal("T", root.GetProperty("app.title").GetString());
    }

    [Fact]
    public void BuildJson_OutputIsAValidCatalogForTheSameLanguage()
    {
        var json = TranslationExport.BuildJson(BuiltInCatalog.English, "xx",
            [Catalog("""{ "_meta": { "code": "xx" }, "app.title": "T" }""")], new Dictionary<string, string>());

        var catalog = Catalog(json); // renaming the file to xx.json must load cleanly
        Assert.Equal("xx", catalog.Code);
        var localizer = Localizer.Create(BuiltInCatalog.English, [catalog]);
        Assert.Empty(localizer.Warnings);
        Assert.Equal("T", catalog.Entries["app.title"]); // the existing translation survives the round trip
        Assert.Equal(BuiltInCatalog.English.Entries.Count, catalog.Entries.Count);
    }

    [Fact]
    public void Export_MergesShippedAndUserFiles_WritesTodoFileTheLoaderIgnores()
    {
        var shipped = _temp.Dir("shipped");
        var user = Path.Combine(_temp.Path, "user", "Languages"); // does not exist yet
        File.WriteAllText(Path.Combine(shipped, "xx.json"), """{ "_meta": { "code": "xx", "nativeName": "Shipped" }, "app.title": "T" }""");
        File.WriteAllText(Path.Combine(shipped, TranslationExport.NotesFileName), """{ "_meta": {}, "common.save": "Save button." }""");
        File.WriteAllText(Path.Combine(shipped, "yy.json"), """{ "_meta": { "code": "yy" }, "common.save": "other language" }""");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "xx.json"), """{ "_meta": { "code": "xx", "nativeName": "Mine" }, "common.cancel": "C", "app.title": "U" }""");

        var path = TranslationExport.Export("xx", shipped, user);

        Assert.Equal(Path.Combine(user, "xx.todo.json"), path);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 without BOM");
        var root = Parse(Encoding.UTF8.GetString(bytes));
        var missing = MissingList(root);
        Assert.Equal(BuiltInCatalog.English.Entries.Count, TextKeys(root).Count);
        Assert.DoesNotContain("app.title", missing);     // translated in both files
        Assert.DoesNotContain("common.cancel", missing); // translated in the user file
        Assert.Contains("common.save", missing);         // a translation of another language does not count
        Assert.Equal("U", root.GetProperty("app.title").GetString()); // user text wins over shipped
        Assert.Equal("C", root.GetProperty("common.cancel").GetString());
        Assert.Equal(BuiltInCatalog.English.Entries["common.save"], root.GetProperty("common.save").GetString());
        Assert.Equal("Mine", root.GetProperty("_meta").GetProperty("nativeName").GetString()); // user file wins
        Assert.Equal("Save button.", root.GetProperty("_notes").GetProperty("common.save").GetString());

        // The dotted name keeps the half-done file out of the language list and out of the loaded text.
        var loader = new LanguageLoader(new PhysicalFileSystem(), shipped, user);
        Assert.Single(loader.DiscoverLanguages(), l => l.Code == "xx");
        Assert.Equal("U", loader.Load("xx").Get("app.title"));
        Assert.Equal(BuiltInCatalog.English.Entries["common.save"], loader.Load("xx").Get("common.save"));
    }

    [Fact]
    public void Export_OverwritesAnEarlierExport()
    {
        var shipped = _temp.Dir("shipped2");
        var user = _temp.Dir("user2");
        File.WriteAllText(Path.Combine(user, "xx.todo.json"), "stale");

        var path = TranslationExport.Export("xx", shipped, user);

        Assert.StartsWith("{", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_KeepsTheEarlierExportAsBackup_SoHandEditsAreNotLost()
    {
        var shipped = _temp.Dir("shipped4");
        var user = _temp.Dir("user4");
        var existing = Path.Combine(user, "xx.todo.json");
        File.WriteAllText(existing, "my half-done translation");

        var path = TranslationExport.Export("xx", shipped, user);

        Assert.Equal(existing, path);
        Assert.Equal("my half-done translation", File.ReadAllText(existing + TranslationExport.BackupSuffix));
        Assert.StartsWith("{", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Export_SecondExport_KeepsHandEditsInAnotherBackup()
    {
        var shipped = _temp.Dir("shipped5");
        var user = _temp.Dir("user5");
        var existing = Path.Combine(user, "xx.todo.json");
        File.WriteAllText(existing, "first hand edit");
        TranslationExport.Export("xx", shipped, user);
        File.WriteAllText(existing, "second hand edit");

        TranslationExport.Export("xx", shipped, user);

        Assert.Equal("first hand edit", File.ReadAllText(existing + TranslationExport.BackupSuffix));
        Assert.Equal("second hand edit", File.ReadAllText(existing + TranslationExport.BackupSuffix + "2"));
    }

    [Fact]
    public void Export_UnchangedFile_NeedsNoBackup()
    {
        var shipped = _temp.Dir("shipped6");
        var user = _temp.Dir("user6");
        var path = TranslationExport.Export("xx", shipped, user);

        TranslationExport.Export("xx", shipped, user);

        Assert.False(File.Exists(path + TranslationExport.BackupSuffix));
    }

    [Fact]
    public void Export_RejectsInvalidCode()
    {
        Assert.Throws<ArgumentException>(() => TranslationExport.Export("..\\evil", _temp.Dir("s3"), _temp.Dir("u3")));
    }

    [Fact]
    public void LocalizationService_ExportTodo_ShippedVietnamese_ListsEveryKey_WithViText()
    {
        var service = new LocalizationService(new PathsStub(Path.Combine(_temp.Path, "cfg", "config.json")), new PhysicalFileSystem(), NullLog.Instance);

        var path = service.ExportTodo("vi");

        Assert.Equal(Path.Combine(service.UserLanguagesDir, "vi.todo.json"), path);
        var shippedVi = Catalog(File.ReadAllText(Path.Combine(LocalizationService.ShippedLanguagesDir, "vi.json")));
        var root = Parse(File.ReadAllText(path));
        var keys = TextKeys(root);
        // vi has no plural forms, so no key.one is work to do; every other English key is either missing or has vi text.
        var expected = BuiltInCatalog.English.Entries.Keys
            .Where(k => shippedVi.Entries.ContainsKey(k) || !k.EndsWith(".one", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, keys.Order(StringComparer.Ordinal).ToList());
        Assert.All(shippedVi.Entries.Where(p => BuiltInCatalog.English.Entries.ContainsKey(p.Key)),
            p => Assert.Equal(p.Value, root.GetProperty(p.Key).GetString()));
        Assert.All(MissingList(root), k => Assert.DoesNotContain(k, shippedVi.Entries.Keys));
        Assert.Equal("vi", root.GetProperty("_meta").GetProperty("code").GetString());
        Assert.Equal("none", root.GetProperty("_meta").GetProperty("plural").GetString());
        // Translator context comes from the shipped en.notes.json, and only for exported keys.
        var notes = root.GetProperty("_notes").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(notes, n => Assert.Contains(n, expected));
        Assert.Contains("app.title", notes);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("zz")]
    public void LocalizationService_ResolveCode_NeverReturnsAuto(string setting)
    {
        var service = new LocalizationService(new PathsStub(Path.Combine(_temp.Path, "cfg2", "config.json")), new PhysicalFileSystem(), NullLog.Instance);

        var code = service.ResolveCode(setting);

        Assert.NotEqual("auto", code);
        Assert.Contains(service.DiscoverLanguages(), l => l.Code == code);
    }

    private sealed class PathsStub(string configFile) : IAppPaths
    {
        public string ConfigFile => configFile;
        public string JournalFile => throw new NotSupportedException();
        public string SessionsDir => throw new NotSupportedException();
        public string LogFile => throw new NotSupportedException();
        public string PreviewCacheDir => throw new NotSupportedException();
        public string ThumbnailCacheDir => throw new NotSupportedException();
        public string WindowPlacementFile => throw new NotSupportedException();
    }
}
