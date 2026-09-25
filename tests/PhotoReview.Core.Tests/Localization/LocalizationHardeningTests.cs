using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>Translator-facing warnings and last-line defences that had no test.</summary>
[Collection("GlobalState")] // switches Localizer.Current
public sealed class LocalizationHardeningTests
{
    [Fact(DisplayName = "A duplicated key in a translation file warns (the last value still wins)")]
    public void DuplicateKey_Warns()
    {
        var warnings = new List<string>();

        var ok = LanguageCatalog.TryParse("""{ "_meta": {"code":"xx"}, "a": "first", "a": "second", "b": "x" }""", "dup.json", out var catalog, warnings);

        Assert.True(ok);
        Assert.Equal("second", catalog.Entries["a"]);
        Assert.Single(warnings, w => w.Contains("dup.json", StringComparison.Ordinal) && w.Contains("'a'", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "An unknown _meta.plural value warns and falls back to one-other; the two valid values do not warn")]
    [InlineData("none", PluralRule.None, false)]
    [InlineData("NONE", PluralRule.None, false)]
    [InlineData("one-other", PluralRule.OneOther, false)]
    [InlineData("other", PluralRule.OneOther, true)]
    [InlineData("", PluralRule.OneOther, true)]
    [InlineData("zero-one-few-many", PluralRule.OneOther, true)]
    public void UnknownPlural_Warns(string value, PluralRule expected, bool warns)
    {
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse("{ \"_meta\": {\"code\":\"xx\", \"plural\": \"" + value + "\"} }", "p.json", out var catalog, warnings));

        Assert.Equal(expected, catalog.Plural);
        Assert.Equal(warns, warnings.Any(w => w.Contains("plural", StringComparison.Ordinal)));
    }

    /// <summary>File system whose stat is unknown (null), like a share that cannot report sizes.</summary>
    private sealed class NoStatFileSystem(InMemoryFileSystem inner) : IFileSystem
    {
        public FileStat? GetFileStat(string path) => null;
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppendDurable(string path) => inner.OpenAppendDurable(path);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> ReadLines(string path) => inner.ReadLines(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    [Fact(DisplayName = "An oversized translation file is skipped even when the file system cannot report its size")]
    public void OversizedFile_WithoutStat_Skipped()
    {
        var fs = new InMemoryFileSystem();
        var padding = new string('x', (int)LanguageCatalog.MaxFileBytes);
        fs.AddFile(@"C:\App\Languages\big.json", "{ \"_meta\": {\"code\":\"bg\"}, \"pad\": \"" + padding + "\" }");
        fs.AddFile(@"C:\App\Languages\ok.json", "{ \"_meta\": {\"code\":\"ok\"} }");
        var loader = new LanguageLoader(new NoStatFileSystem(fs), @"C:\App\Languages", null);

        var codes = loader.DiscoverLanguages().Select(l => l.Code).ToList();
        var localizer = loader.Load("bg");

        Assert.Contains("ok", codes);
        Assert.DoesNotContain("bg", codes);
        Assert.Contains(localizer.Warnings, w => w.Contains("larger than", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UserFacingError.Describe falls back to the exception message when the sentence factory throws")]
    public void Describe_ThrowingFactory_FallsBackToMessage()
    {
        var ex = UserFacingError.Localized(new IOException("os message"), () => throw new InvalidOperationException("catalog broke"));

        Assert.True(UserFacingError.IsLocalized(ex));
        Assert.Equal("os message", UserFacingError.Describe(ex));
    }

    [Fact(DisplayName = "UserFacingError follows the current language at display time")]
    public void Describe_FollowsLanguageAtDisplayTime()
    {
        var english = BuiltInCatalog.EnglishLocalizer;
        var pseudo = TranslatorModes.CreatePseudo(english);
        var key = english.Keys.First(k => english.Get(k).Length > 3);
        var ex = UserFacingError.Localized(new IOException("os"), () => Localizer.Current.Get(key));
        var previous = Localizer.Current;
        try
        {
            Localizer.SetCurrent(english);
            var a = UserFacingError.Describe(ex);
            Localizer.SetCurrent(pseudo);
            var b = UserFacingError.Describe(ex);

            Assert.Equal(english.Get(key), a);
            Assert.Equal(pseudo.Get(key), b);
        }
        finally
        {
            Localizer.SetCurrent(previous);
        }
    }

    [Fact(DisplayName = "ReviewMetrics ignores negative durations and byte counts instead of shrinking its totals")]
    public void Metrics_NegativeInputs_Clamped()
    {
        var metrics = new ReviewMetrics();
        metrics.RecordSourceRead(100, 10);

        metrics.RecordSourceRead(-50, -20);
        metrics.RecordPresented(30);
        metrics.RecordPresented(-5);
        var snapshot = metrics.Snapshot();

        Assert.Equal(100, snapshot.SourceBytesRead);
        Assert.Equal(10, snapshot.DecodeMilliseconds);
        Assert.Equal(2, snapshot.SourceReads);
        Assert.Equal(30, snapshot.PresentMilliseconds);
        Assert.Equal(2, snapshot.PresentedImages);
        Assert.Equal(1, snapshot.PresentHistogram.Single(b => b.Label == "<=8").Count);
    }
}
