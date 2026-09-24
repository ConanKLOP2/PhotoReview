using System.Globalization;
using System.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>
/// <see cref="LanguageLoader"/> against the in-memory file system. The loader always uses the embedded
/// English catalog, so these tests only rely on its <c>app.title</c> key.
/// </summary>
public sealed class LanguageLoaderTests
{
    private const string Key = "app.title";
    private const string ShippedDir = @"C:\App\Languages";
    private const string UserDir = @"C:\Users\u\AppData\Local\PhotoReview\Languages";

    private readonly InMemoryFileSystem _fs = new();

    private static string EnglishTitle => BuiltInCatalog.English.Entries[Key];

    private LanguageLoader CreateLoader(string? userDir = UserDir) => new(_fs, ShippedDir, userDir);

    private void AddCatalog(string dir, string fileName, string code, string nativeName, string? title = null, string plural = "one-other")
    {
        var entry = title is null ? string.Empty : $", \"{Key}\": \"{title}\"";
        _fs.AddFile(Path.Combine(dir, fileName),
            $$"""{ "_meta": { "code": "{{code}}", "name": "{{code}}", "nativeName": "{{nativeName}}", "plural": "{{plural}}" }{{entry}} }""");
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new LanguageLoader(null!, ShippedDir, UserDir));
        Assert.Throws<ArgumentNullException>(() => new LanguageLoader(_fs, null!, UserDir));
    }

    [Fact]
    public void DiscoverLanguages_MissingFolders_ReturnsEnglishOnly()
    {
        var languages = CreateLoader().DiscoverLanguages();

        var english = Assert.Single(languages);
        Assert.Equal("en", english.Code);
        Assert.False(english.IsUserProvided);
    }

    [Fact]
    public void DiscoverLanguages_NullUserDir_ListsShippedOnly()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt");

        var codes = CreateLoader(userDir: null).DiscoverLanguages().Select(l => l.Code);

        Assert.Equal(["en", "vi"], codes);
    }

    [Fact]
    public void DiscoverLanguages_ShippedAndUser_EnglishFirstEachCodeOnceSortedByNativeName()
    {
        AddCatalog(ShippedDir, "en.json", "en", "English (shipped)");
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt");
        AddCatalog(UserDir, "vi.json", "vi", "Tiếng Việt (user)");
        AddCatalog(UserDir, "de.json", "de", "Deutsch");
        AddCatalog(UserDir, "en.json", "en", "English (user)");

        var languages = CreateLoader().DiscoverLanguages();

        Assert.Equal(["en", "de", "vi"], languages.Select(l => l.Code));
        Assert.Equal(BuiltInCatalog.English.NativeName, languages[0].NativeName);
        Assert.False(languages[0].IsUserProvided);
        Assert.True(languages[1].IsUserProvided);
        Assert.False(languages[2].IsUserProvided); // shipped entry wins the listing
        Assert.Equal("Tiếng Việt", languages[2].NativeName);
    }

    [Fact]
    public void DiscoverLanguages_CodeTakenFromMetaNotFileName()
    {
        AddCatalog(UserDir, "my-translation.json", "fr", "Français");

        Assert.Contains(CreateLoader().DiscoverLanguages(), l => l.Code == "fr" && l.IsUserProvided);
    }

    [Fact]
    public void DiscoverLanguages_DottedFileNames_Skipped()
    {
        AddCatalog(ShippedDir, "en.notes.json", "zz", "Notes");
        AddCatalog(UserDir, "vi.backup.json", "yy", "Backup");

        Assert.Equal(["en"], CreateLoader().DiscoverLanguages().Select(l => l.Code));
    }

    [Fact]
    public void DiscoverLanguages_NonJsonFiles_Ignored()
    {
        AddCatalog(ShippedDir, "vi.txt", "vi", "Tiếng Việt");

        Assert.Equal(["en"], CreateLoader().DiscoverLanguages().Select(l => l.Code));
    }

    [Fact]
    public void DiscoverLanguages_BrokenFile_SkippedWithoutThrowing()
    {
        _fs.AddFile(Path.Combine(ShippedDir, "aa.json"), "{ not json");
        _fs.AddFile(Path.Combine(ShippedDir, "bb.json"), "[]");
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt");

        Assert.Equal(["en", "vi"], CreateLoader().DiscoverLanguages().Select(l => l.Code));
    }

    [Fact]
    public void Load_FileLargerThanMax_SkippedWithWarning()
    {
        var big = new byte[LanguageCatalog.MaxFileBytes + 1];
        Array.Fill(big, (byte)' ');
        _fs.AddFile(Path.Combine(UserDir, "vi.json"), big);

        var loader = CreateLoader();
        var localizer = loader.Load("vi");

        Assert.DoesNotContain(loader.DiscoverLanguages(), l => l.Code == "vi");
        Assert.Equal("en", localizer.Code);
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "larger than"));
    }

    [Fact]
    public void Load_FileExactlyMax_IsRead()
    {
        var json = $$"""{ "_meta": { "code": "vi" }, "{{Key}}": "Tiêu đề" }""";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var padded = new byte[LanguageCatalog.MaxFileBytes];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        _fs.AddFile(Path.Combine(UserDir, "vi.json"), padded);

        var localizer = CreateLoader().Load("vi");

        Assert.Equal("vi", localizer.Code);
        Assert.Empty(localizer.Warnings);
    }

    [Fact]
    public void Load_BrokenFile_ReportedInWarnings()
    {
        _fs.AddFile(Path.Combine(ShippedDir, "vi.json"), "{ \"_meta\": ");
        _fs.AddFile(Path.Combine(UserDir, "fr.json"), """{ "_meta": { "code": "?" } }""");

        var localizer = CreateLoader().Load("vi");

        Assert.Equal("en", localizer.Code);
        Assert.Equal(EnglishTitle, localizer.Get(Key));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "vi.json: invalid JSON"));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "fr.json: _meta.code is missing or invalid"));
    }

    [Fact]
    public void Load_UnreadableFile_ReportedInWarnings()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Tiêu đề");
        _fs.OpenReadHook = _ => new IOException("locked");

        var localizer = CreateLoader().Load("vi");

        Assert.Equal("en", localizer.Code);
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "cannot read (locked)"));
    }

    [Fact]
    public void Load_ShippedOnly_UsesShippedTranslation()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Tiêu đề", plural: "none");

        var localizer = CreateLoader().Load("vi");

        Assert.Equal("vi", localizer.Code);
        Assert.Equal("Tiếng Việt", localizer.NativeName);
        Assert.Equal(PluralRule.None, localizer.Plural);
        Assert.Equal("Tiêu đề", localizer.Get(Key));
        Assert.Empty(localizer.Warnings);
    }

    [Fact]
    public void Load_ShippedAndUser_UserWinsPerKey()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Shipped title");
        AddCatalog(UserDir, "vi.json", "vi", "Tiếng Việt", "User title");

        Assert.Equal("User title", CreateLoader().Load("vi").Get(Key));
    }

    [Fact]
    public void Load_UserFileWithoutKey_KeepsShippedValue()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Shipped title");
        AddCatalog(UserDir, "vi.json", "vi", "Tiếng Việt");

        Assert.Equal("Shipped title", CreateLoader().Load("vi").Get(Key));
    }

    [Fact]
    public void Load_UserOnlyLanguage_Loads()
    {
        AddCatalog(UserDir, "de.json", "de", "Deutsch", "Fotoprüfung");

        var localizer = CreateLoader().Load("DE");

        Assert.Equal("de", localizer.Code);
        Assert.Equal("Fotoprüfung", localizer.Get(Key));
    }

    [Fact]
    public void Load_UnknownCode_FallsBackToEnglish()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Tiêu đề");

        var localizer = CreateLoader().Load("xx");

        Assert.Equal("en", localizer.Code);
        Assert.Equal(EnglishTitle, localizer.Get(Key));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(null)]
    [InlineData("")]
    public void Load_AutoWithVietnameseCulture_ResolvesVietnamese(string? code)
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Tiêu đề");

        var localizer = CreateLoader().Load(code, CultureInfo.GetCultureInfo("vi-VN"));

        Assert.Equal("vi", localizer.Code);
        Assert.Equal("Tiêu đề", localizer.Get(Key));
    }

    [Fact]
    public void Load_AutoWithUnavailableCulture_FallsBackToEnglish()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt", "Tiêu đề");

        var localizer = CreateLoader().Load(LanguageLoader.AutoCode, CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("en", localizer.Code);
        Assert.Equal(EnglishTitle, localizer.Get(Key));
    }

    [Fact]
    public void Resolve_AutoWithRegionalCatalog_PrefersFullCultureName()
    {
        AddCatalog(ShippedDir, "pt.json", "pt", "Português");
        AddCatalog(ShippedDir, "pt-BR.json", "pt-BR", "Português (Brasil)");
        var loader = CreateLoader();

        Assert.Equal("pt-br", loader.Resolve(LanguageLoader.AutoCode, CultureInfo.GetCultureInfo("pt-BR")));
        Assert.Equal("pt", loader.Resolve(LanguageLoader.AutoCode, CultureInfo.GetCultureInfo("pt-PT")));
    }

    [Fact]
    public void Resolve_ExplicitCode_IgnoresCulture()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt");
        var loader = CreateLoader();

        Assert.Equal("en", loader.Resolve("en", CultureInfo.GetCultureInfo("vi-VN")));
        Assert.Equal("vi", loader.Resolve("vi", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Load_MissingFolders_ReturnsEnglishWithoutWarnings()
    {
        var localizer = new LanguageLoader(_fs, @"C:\Nope\Languages", null).Load("vi");

        Assert.Equal("en", localizer.Code);
        Assert.Empty(localizer.Warnings);
        Assert.Equal(EnglishTitle, localizer.Get(Key));
    }

    [Fact]
    public void Load_UserEnglishOverride_AppliesOnlyWhenEnglishSelected()
    {
        AddCatalog(ShippedDir, "vi.json", "vi", "Tiếng Việt");
        AddCatalog(UserDir, "en.json", "en", "English", "My Review");
        var loader = CreateLoader();

        var english = loader.Load("en");
        var vietnamese = loader.Load("vi");

        Assert.Equal("en", english.Code);
        Assert.Equal("My Review", english.Get(Key));
        Assert.Equal("vi", vietnamese.Code);
        Assert.Equal(EnglishTitle, vietnamese.Get(Key));
    }

    [Fact]
    public void Load_ShippedEnglishFile_NotUsedAsOverlay()
    {
        AddCatalog(ShippedDir, "en.json", "en", "English", "Shipped English");

        Assert.Equal(EnglishTitle, CreateLoader().Load("en").Get(Key));
    }

    [Fact]
    public void Load_TranslationWithUnknownKey_WarnsAndStillLoads()
    {
        _fs.AddFile(Path.Combine(UserDir, "vi.json"),
            $$"""{ "_meta": { "code": "vi" }, "{{Key}}": "Tiêu đề", "no.such.key": "x" }""");

        var localizer = CreateLoader().Load("vi");

        Assert.Equal("Tiêu đề", localizer.Get(Key));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "unknown key 'no.such.key'"));
    }
}
