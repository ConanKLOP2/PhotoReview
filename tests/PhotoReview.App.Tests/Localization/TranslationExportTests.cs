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

    [Fact]
    public void BuildJson_ListsOnlyMissingKeys_WithEnglishText_MetaAndNotes()
    {
        var partial = Catalog("""
            { "_meta": { "code": "xx", "name": "Test", "nativeName": "Testish", "plural": "none", "authors": ["A"] },
              "app.title": "Tst", "common.cancel": "Nope" }
            """);
        var notes = new Dictionary<string, string> { ["common.save"] = "Save button.", ["app.title"] = "Product name." };

        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "xx", [partial], notes));

        var meta = root.GetProperty("_meta");
        Assert.Equal("xx", meta.GetProperty("code").GetString());
        Assert.Equal("Testish", meta.GetProperty("nativeName").GetString());
        Assert.Equal("none", meta.GetProperty("plural").GetString());
        Assert.Equal("A", Assert.Single(meta.GetProperty("authors").EnumerateArray()).GetString());

        var keys = TextKeys(root);
        var expected = BuiltInCatalog.English.Entries.Keys.Where(k => k is not "app.title" and not "common.cancel")
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, keys); // every missing key, sorted, nothing else
        Assert.Equal(BuiltInCatalog.English.Entries["common.save"], root.GetProperty("common.save").GetString());

        // Notes only for keys that are exported.
        var exportedNotes = root.GetProperty("_notes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal("Save button.", Assert.Single(exportedNotes).Value);
        Assert.Contains("_help", root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void BuildJson_English_HasNothingToTranslate()
    {
        var root = Parse(TranslationExport.BuildJson(BuiltInCatalog.English, "en", [], new Dictionary<string, string>()));

        Assert.Empty(TextKeys(root));
        Assert.Equal("en", root.GetProperty("_meta").GetProperty("code").GetString());
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
        Assert.DoesNotContain("app.title", catalog.Entries.Keys);
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
        File.WriteAllText(Path.Combine(user, "xx.json"), """{ "_meta": { "code": "xx", "nativeName": "Mine" }, "common.cancel": "C" }""");

        var path = TranslationExport.Export("xx", shipped, user);

        Assert.Equal(Path.Combine(user, "xx.todo.json"), path);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 without BOM");
        var root = Parse(Encoding.UTF8.GetString(bytes));
        var keys = TextKeys(root);
        Assert.DoesNotContain("app.title", keys);     // translated in the shipped file
        Assert.DoesNotContain("common.cancel", keys); // translated in the user file
        Assert.Contains("common.save", keys);         // a translation of another language does not count
        Assert.Equal("Mine", root.GetProperty("_meta").GetProperty("nativeName").GetString()); // user file wins
        Assert.Equal("Save button.", root.GetProperty("_notes").GetProperty("common.save").GetString());

        // The dotted name keeps the half-done file out of the language list and out of the loaded text.
        var loader = new LanguageLoader(new PhysicalFileSystem(), shipped, user);
        Assert.Single(loader.DiscoverLanguages(), l => l.Code == "xx");
        Assert.Equal("T", loader.Load("xx").Get("app.title"));
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
    public void LocalizationService_ExportTodo_ShippedVietnamese_ListsExactlyTheKeysViLacks()
    {
        var service = new LocalizationService(new PathsStub(Path.Combine(_temp.Path, "cfg", "config.json")), new PhysicalFileSystem(), NullLog.Instance);

        var path = service.ExportTodo("vi");

        Assert.Equal(Path.Combine(service.UserLanguagesDir, "vi.todo.json"), path);
        var shippedVi = Catalog(File.ReadAllText(Path.Combine(LocalizationService.ShippedLanguagesDir, "vi.json")));
        var expected = BuiltInCatalog.English.Entries.Keys.Where(k => !shippedVi.Entries.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var root = Parse(File.ReadAllText(path));
        Assert.Equal(expected, TextKeys(root));
        Assert.Equal("vi", root.GetProperty("_meta").GetProperty("code").GetString());
        Assert.Equal("none", root.GetProperty("_meta").GetProperty("plural").GetString());
        // Translator context comes from the shipped en.notes.json, and only for exported keys.
        var notes = root.GetProperty("_notes").EnumerateObject().Select(p => p.Name).ToList();
        Assert.All(notes, n => Assert.Contains(n, expected));
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
