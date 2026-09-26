using System.ComponentModel;
using System.Globalization;
using System.IO;
using PhotoReview.App.Localization;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.App.Tests.Localization;

// Switches the ambient localizer, so it must not run in parallel with tests that assert Vietnamese text.
[Collection("GlobalState")]
public sealed class LocalizationServiceTests : IDisposable
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

    private LocalizationService CreateService() =>
        new(new PathsStub(Path.Combine(_temp.Path, "config.json")), new PhysicalFileSystem(), NullLog.Instance);

    [Fact]
    public void Switch_ToEnglish_PublishesLocalizerAndUiCulture()
    {
        var service = CreateService();

        service.Switch("en");

        Assert.Equal("en", Localizer.Current.Code);
        Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        Assert.Equal("en", service.RequestedLanguage);
    }

    [Fact]
    public void Switch_ToVietnamese_UsesShippedCatalog()
    {
        var service = CreateService();

        service.Switch("vi");

        Assert.Equal("vi", Localizer.Current.Code);
        Assert.Equal("Tiếng Việt", Localizer.Current.NativeName);
    }

    [Fact]
    public void Switch_RaisesIndexerChangeOnLocalizationSource()
    {
        var service = CreateService();
        var changed = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => changed.Add(e.PropertyName);
        LocalizationSource.Instance.PropertyChanged += handler;
        try
        {
            service.Switch("en");
        }
        finally
        {
            LocalizationSource.Instance.PropertyChanged -= handler;
        }

        Assert.Contains("Item[]", changed);
        Assert.Equal(Localizer.Current.Get("app.title"), LocalizationSource.Instance["app.title"]);
    }

    [Fact]
    public void Reload_PicksUpEditedUserFile()
    {
        var service = CreateService();
        service.Switch("vi");
        Directory.CreateDirectory(service.UserLanguagesDir);
        File.WriteAllText(Path.Combine(service.UserLanguagesDir, "vi.json"),
            """{ "_meta": { "code": "vi" }, "app.title": "Duyệt ảnh" }""");

        var problems = service.Reload();

        Assert.Equal("Duyệt ảnh", Localizer.Current.Get("app.title"));
        Assert.Empty(problems);
    }

    [Fact]
    public void Reload_BrokenUserFile_ReturnsTheProblem_AndKeepsShippedText()
    {
        var service = CreateService();
        service.Switch("vi");
        var shippedTitle = Localizer.Current.Get("app.title");
        Directory.CreateDirectory(service.UserLanguagesDir);
        var broken = Path.Combine(service.UserLanguagesDir, "vi.json");
        File.WriteAllText(broken, """{ "_meta": { "code": "vi" }, "app.title": "Duyệt ảnh" """); // missing '}'

        var problems = service.Reload();

        Assert.Contains(problems, p => p.Contains(broken, StringComparison.Ordinal) && p.Contains("invalid JSON", StringComparison.Ordinal));
        Assert.Equal(shippedTitle, Localizer.Current.Get("app.title")); // the whole file is skipped
    }

    [Fact]
    public void Reload_BadPlaceholder_ReturnsTheProblemForThatKey()
    {
        var service = CreateService();
        service.Switch("vi");
        Directory.CreateDirectory(service.UserLanguagesDir);
        File.WriteAllText(Path.Combine(service.UserLanguagesDir, "vi.json"),
            """{ "_meta": { "code": "vi" }, "main.skipped.warning.other": "Bỏ qua {soLuong} tệp", "app.title": "Duyệt ảnh" }""");

        var problems = service.Reload();

        Assert.Contains(problems, p => p.Contains("'main.skipped.warning.other'", StringComparison.Ordinal));
        Assert.Equal("Duyệt ảnh", Localizer.Current.Get("app.title")); // the other entries still load
    }

    [Fact]
    public void ProblemsMessage_NoProblems_SaysSo()
    {
        TestLocalization.UseVietnamese();

        Assert.Equal(Tr.DialogReloadTranslationsOk, TranslationProblems.Message([]));
        Assert.False(TranslationProblems.HasProblems([]));
    }

    [Fact]
    public void ProblemsMessage_ListsEachProblem_AndCountsTheRest()
    {
        TestLocalization.UseVietnamese();
        var warnings = Enumerable.Range(1, TranslationProblems.MaxListed + 3).Select(i => $"problem-{i:D2}").ToList();

        var message = TranslationProblems.Message(warnings);

        Assert.True(TranslationProblems.HasProblems(warnings));
        Assert.Contains((TranslationProblems.MaxListed + 3).ToString(CultureInfo.InvariantCulture), message, StringComparison.Ordinal);
        Assert.All(warnings.Take(TranslationProblems.MaxListed), w => Assert.Contains("• " + w, message, StringComparison.Ordinal));
        Assert.DoesNotContain(warnings[TranslationProblems.MaxListed], message, StringComparison.Ordinal);
        Assert.EndsWith(Tr.DialogReloadTranslationsMore(3), message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProblemsMessage_FewProblems_ListsAllWithoutMoreLine()
    {
        TestLocalization.UseVietnamese();

        var message = TranslationProblems.Message(["a.json: invalid JSON"]);

        Assert.EndsWith("• a.json: invalid JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverLanguages_ListsEnglishFirstAndShippedVietnamese()
    {
        var languages = CreateService().DiscoverLanguages();

        Assert.Equal("en", languages[0].Code);
        Assert.Contains(languages, l => l.Code == "vi" && !l.IsUserProvided);
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
