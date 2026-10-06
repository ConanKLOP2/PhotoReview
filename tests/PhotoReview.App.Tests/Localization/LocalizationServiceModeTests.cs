using System.Globalization;
using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests.Localization;

/// <summary>
/// I18N L09 translator modes, the command-line switches that select them, the "unknown culture" tolerance of
/// <see cref="LocalizationService.Apply"/> and the way load warnings reach the log.
/// </summary>
// Switches the ambient localizer and the UI culture, so it must not run in parallel with tests that assert Vietnamese text.
[Collection("GlobalState")]
public sealed class LocalizationServiceModeTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        TestLocalization.UseVietnamese();
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
        _temp.Dispose();
    }

    private sealed class RecordingLog : ILog
    {
        public List<string> Warnings { get; } = [];
        public bool Enabled => true;
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message, Exception? ex = null) { }
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

    private LocalizationService CreateService(ILog? log = null) =>
        new(new PathsStub(Path.Combine(_temp.Path, "config.json")), new PhysicalFileSystem(), log ?? new RecordingLog());

    [Theory]
    [InlineData(TranslatorMode.None)]
    [InlineData(TranslatorMode.Keys, "--i18n-keys")]
    [InlineData(TranslatorMode.Keys, "--I18N-KEYS")]
    [InlineData(TranslatorMode.Pseudo, "--i18n-pseudo")]
    [InlineData(TranslatorMode.Pseudo, "--I18N-Pseudo")]
    [InlineData(TranslatorMode.Keys, "--i18n-pseudo", "--i18n-keys")] // keys wins when both are given
    [InlineData(TranslatorMode.None, "--i18n", "i18n-keys", "pseudo")] // near misses are not the switches
    [InlineData(TranslatorMode.Pseudo, "C:\\photos", "--i18n-pseudo")]
    public void ParseMode_ReadsTheTranslatorSwitchesFromTheCommandLine(TranslatorMode expected, params string[] args)
    {
        Assert.Equal(expected, LocalizationService.ParseMode(args));
    }

    [Fact]
    public void ParseMode_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => LocalizationService.ParseMode(null!));
    }

    [Fact]
    public void Load_InKeysMode_ShowsEachTextsCatalogKey()
    {
        var service = CreateService();
        service.Mode = TranslatorMode.Keys;

        var localizer = service.Load("en");

        Assert.Equal("[app.title]", localizer.Get("app.title"));
        Assert.Equal("en", localizer.Code); // the language itself is unchanged
    }

    [Fact]
    public void Load_InPseudoMode_AccentsAndBracketsTheTextUnderThePseudoCode()
    {
        var service = CreateService();
        var plain = service.Load("en").Get("app.title");
        service.Mode = TranslatorMode.Pseudo;

        var pseudo = service.Load("en");

        var text = pseudo.Get("app.title");
        Assert.StartsWith("[", text, StringComparison.Ordinal);
        Assert.EndsWith("]", text, StringComparison.Ordinal);
        Assert.NotEqual(plain, text);
        Assert.True(text.Length > plain.Length, "pseudo text is padded to look longer than the original");
        Assert.NotEqual("en", pseudo.Code);
    }

    [Fact]
    public void Load_WithoutAMode_ReturnsTheCatalogTextUnchanged()
    {
        var service = CreateService();

        var localizer = service.Load("en");

        Assert.Equal("en", localizer.Code);
        Assert.NotEqual("[app.title]", localizer.Get("app.title"));
        Assert.DoesNotContain("[", localizer.Get("app.title"), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_InPseudoMode_KeepsTheEnglishUiCultureWhateverTheLocalizersCode()
    {
        var service = CreateService();
        service.Mode = TranslatorMode.Pseudo;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");

        service.Switch("vi");

        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en", CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal("vi", service.RequestedLanguage);
    }

    [Fact]
    public void Apply_ALanguageCodeDotNetDoesNotKnow_StillPublishesTheTextAndKeepsTheCulture()
    {
        var service = CreateService();
        service.Switch("en");
        var before = CultureInfo.CurrentUICulture;
        var unknown = service.Load("en").Transform("not a culture!", "Community", PluralRule.OneOther, (_, t) => t);

        service.Apply("community", unknown);

        Assert.Same(unknown, Localizer.Current);
        Assert.Equal(before.Name, CultureInfo.CurrentUICulture.Name);
        Assert.Equal("community", service.RequestedLanguage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_ABlankLanguage_IsRecordedAsAuto(string? language)
    {
        var service = CreateService();
        service.Switch("vi");

        service.Apply(language, service.Load("en"));

        Assert.Equal(LanguageLoader.AutoCode, service.RequestedLanguage);
    }

    [Fact]
    public void Apply_NullLocalizer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CreateService().Apply("en", null!));
    }

    [Fact]
    public void Apply_LogsEveryLoadWarningWithTheI18nPrefix()
    {
        var log = new RecordingLog();
        var service = CreateService(log);
        Directory.CreateDirectory(service.UserLanguagesDir);
        var broken = Path.Combine(service.UserLanguagesDir, "vi.json");
        File.WriteAllText(broken, """{ "_meta": { "code": "vi" }, "app.title": "Duyệt ảnh" """); // missing '}'

        service.Switch("vi");

        var warning = Assert.Single(log.Warnings, w => w.Contains(broken, StringComparison.Ordinal));
        Assert.StartsWith("i18n: ", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCode_FollowsTheSettingAndFallsBackToEnglishForUnknownCodes()
    {
        var service = CreateService();

        Assert.Equal("vi", service.ResolveCode("vi"));
        Assert.Equal("en", service.ResolveCode("en"));
        Assert.Equal("en", service.ResolveCode("zz-not-shipped"));
    }

    [Fact]
    public void ExportTodo_WritesTheTodoFileForTheResolvedLanguageIntoTheUserFolder()
    {
        var service = CreateService();

        var path = service.ExportTodo("vi");

        Assert.Equal(Path.Combine(service.UserLanguagesDir, "vi.todo.json"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Constructor_NullPaths_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new LocalizationService(null!, new PhysicalFileSystem(), new RecordingLog()));
    }
}
