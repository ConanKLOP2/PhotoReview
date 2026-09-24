using System.Globalization;
using System.IO;
using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

/// <summary>Shared helpers for the localization tests.</summary>
internal static class LocTestCatalogs
{
    /// <summary>A small English catalog with the placeholder shapes the tests need.</summary>
    public const string EnglishJson = """
        {
          "_meta": { "code": "en", "name": "English", "nativeName": "English", "plural": "one-other" },
          "plain": "Plain text",
          "greet": "Hello {name}",
          "move": "Moved {count} files to {folder}",
          "files.one": "{count} file",
          "files.other": "{count} files",
          "items.other": "{count} items"
        }
        """;

    public static LanguageCatalog English { get; } = Parse(EnglishJson);

    public static LanguageCatalog Parse(string json)
    {
        var warnings = new List<string>();
        Assert.True(LanguageCatalog.TryParse(json, "test", out var catalog, warnings), string.Join("; ", warnings));
        return catalog;
    }

    public static LanguageCatalog Overlay(string code, string entries, string plural = "one-other") =>
        Parse($$"""{ "_meta": { "code": "{{code}}", "name": "{{code}}", "plural": "{{plural}}" }{{(entries.Length == 0 ? "" : ", " + entries)}} }""");

    public static bool HasWarning(IEnumerable<string> warnings, string fragment) =>
        warnings.Any(w => w.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Repository root (the folder holding PhotoReview.slnx).</summary>
    public static string RepositoryRoot { get; } = ResolveRepositoryRoot();

    private static string ResolveRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("PhotoReview.slnx not found above " + AppContext.BaseDirectory);
    }
}

public sealed class LocTemplateTests
{
    private static LocTemplate Parse(string text)
    {
        Assert.True(LocTemplate.TryParse(text, out var template));
        return template;
    }

    [Fact]
    public void Render_LiteralText_ReturnsTextUnchanged()
    {
        var template = Parse("Just text.");

        Assert.False(template.HasPlaceholders);
        Assert.Equal("Just text.", template.Render([]));
        Assert.Equal("Just text.", template.Text);
    }

    [Fact]
    public void Render_EmptyText_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Parse(string.Empty).Render([]));
    }

    [Fact]
    public void Render_NamedPlaceholder_SubstitutesValue()
    {
        var template = Parse("Hello {name}!");

        Assert.Equal(["name"], template.PlaceholderNames);
        Assert.Equal("Hello Ann!", template.Render([new LocArg("name", "Ann")]));
    }

    [Fact]
    public void Render_MultiplePlaceholdersInAnyArgOrder_SubstitutesEach()
    {
        var template = Parse("{a}-{b}{c}");

        Assert.Equal("1-23", template.Render([new LocArg("c", "3"), new LocArg("a", "1"), new LocArg("b", "2")]));
    }

    [Fact]
    public void Render_RepeatedPlaceholder_SubstitutesEveryOccurrence()
    {
        var template = Parse("{x} and {x} again");

        Assert.Equal(["x", "x"], template.PlaceholderNames);
        Assert.Equal("7 and 7 again", template.Render([new LocArg("x", "7")]));
    }

    [Fact]
    public void Render_DoubledBraces_RendersLiteralBraces()
    {
        var template = Parse("{{literal}} {v} }}{{");

        Assert.Equal(["v"], template.PlaceholderNames);
        Assert.Equal("{literal} ok }{", template.Render([new LocArg("v", "ok")]));
    }

    [Fact]
    public void Render_MissingArgument_WritesPlaceholderBack()
    {
        var template = Parse("Moved {count} to {folder}");

        Assert.Equal("Moved 3 to {folder}", template.Render([new LocArg("count", "3")]));
    }

    [Fact]
    public void Render_ArgumentNameCaseDiffers_TreatedAsMissing()
    {
        Assert.Equal("{name}", Parse("{name}").Render([new LocArg("Name", "x")]));
    }

    [Fact]
    public void Render_NullValue_RendersEmpty()
    {
        Assert.Equal("[]", Parse("[{v}]").Render([new LocArg("v", null)]));
    }

    [Fact]
    public void Render_NonFormattableObject_UsesToString()
    {
        Assert.Equal("[path]", Parse("[{v}]").Render([new LocArg("v", new Token("path"))]));
    }

    [Fact]
    public void Render_FormattableValue_UsesCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var template = Parse("{n}");
            Assert.Equal("1234,5", template.Render([new LocArg("n", 1234.5)]));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("1234.5", template.Render([new LocArg("n", 1234.5)]));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Unbalanced {name")]
    [InlineData("Stray } brace")]
    [InlineData("Trailing }")]
    [InlineData("Leading {")]
    [InlineData("{1x}")]
    [InlineData("{}")]
    [InlineData("{with space}")]
    [InlineData("{dot.name}")]
    [InlineData("{a{b}")]
    [InlineData("{ñ}")]
    public void TryParse_InvalidTemplate_ReturnsFalse(string text)
    {
        Assert.False(LocTemplate.TryParse(text, out _));
    }

    [Theory]
    [InlineData("{a1}")]
    [InlineData("{ABC}")]
    [InlineData("{{}}")]
    [InlineData("{{0}}")]
    public void TryParse_ValidTemplate_ReturnsTrue(string text)
    {
        Assert.True(LocTemplate.TryParse(text, out _));
    }

    [Fact]
    public void Literal_TextWithBraces_RendersVerbatim()
    {
        var template = LocTemplate.Literal("{not a placeholder");

        Assert.False(template.HasPlaceholders);
        Assert.Equal("{not a placeholder", template.Render([new LocArg("not", "x")]));
    }

    private sealed class Token(string text)
    {
        public override string ToString() => text;
    }
}

public sealed class LanguageCatalogTests
{
    [Fact]
    public void TryParse_ValidFileWithMeta_ReadsMetaAndEntries()
    {
        const string json = """
            {
              "_meta": { "code": "VI", "name": "Vietnamese", "nativeName": "Tiếng Việt", "plural": "none",
                         "authors": ["A", 5, "B"], "formatVersion": 1 },
              "app.title": "Xem ảnh",
              "greet": "Chào {name}"
            }
            """;
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse(json, "vi.json", out var catalog, warnings));

        Assert.Empty(warnings);
        Assert.Equal("vi", catalog.Code);
        Assert.Equal("Vietnamese", catalog.Name);
        Assert.Equal("Tiếng Việt", catalog.NativeName);
        Assert.Equal(PluralRule.None, catalog.Plural);
        Assert.Equal(["A", "B"], catalog.Authors);
        Assert.Equal(2, catalog.Entries.Count);
        Assert.Equal("Chào {name}", catalog.Entries["greet"]);
    }

    [Fact]
    public void TryParse_PluralNotNone_DefaultsToOneOther()
    {
        Assert.Equal(PluralRule.OneOther, LocTestCatalogs.Overlay("fr", "", plural: "one-other").Plural);
        Assert.Equal(PluralRule.OneOther, LocTestCatalogs.Parse("""{ "_meta": { "code": "fr" } }""").Plural);
        Assert.Equal(PluralRule.None, LocTestCatalogs.Overlay("ja", "", plural: "NONE").Plural);
    }

    [Fact]
    public void TryParse_NamesMissing_FallBackToNameThenCode()
    {
        var codeOnly = LocTestCatalogs.Parse("""{ "_meta": { "code": "fr" } }""");
        var nameOnly = LocTestCatalogs.Parse("""{ "_meta": { "code": "fr", "name": "French" } }""");

        Assert.Equal("fr", codeOnly.Name);
        Assert.Equal("fr", codeOnly.NativeName);
        Assert.Equal("French", nameOnly.NativeName);
    }

    [Theory]
    [InlineData("""{ "k": "v" }""")]
    [InlineData("""{ "_meta": { "name": "No code" }, "k": "v" }""")]
    [InlineData("""{ "_meta": { "code": "" } }""")]
    [InlineData("""{ "_meta": { "code": "x" } }""")]
    [InlineData("""{ "_meta": { "code": "1a" } }""")]
    [InlineData("""{ "_meta": { "code": "v i" } }""")]
    [InlineData("""{ "_meta": { "code": "vi_VN" } }""")]
    [InlineData("""{ "_meta": { "code": "abcdefghijklmnopq" } }""")]
    [InlineData("""{ "_meta": { "code": 12 } }""")]
    public void TryParse_MissingOrInvalidCode_ReturnsFalseWithWarning(string json)
    {
        var warnings = new List<string>();

        Assert.False(LanguageCatalog.TryParse(json, "bad.json", out _, warnings));

        Assert.True(LocTestCatalogs.HasWarning(warnings, "_meta.code"), string.Join("; ", warnings));
        Assert.All(warnings, w => Assert.StartsWith("bad.json", w, StringComparison.Ordinal));
    }

    [Fact]
    public void TryParse_MetaNotObject_ReturnsFalseWithWarnings()
    {
        var warnings = new List<string>();

        Assert.False(LanguageCatalog.TryParse("""{ "_meta": "vi" }""", "m.json", out _, warnings));

        Assert.True(LocTestCatalogs.HasWarning(warnings, "_meta must be an object"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void TryParse_NonObjectRoot_ReturnsFalseWithWarning(string json)
    {
        var warnings = new List<string>();

        Assert.False(LanguageCatalog.TryParse(json, "root.json", out _, warnings));

        Assert.True(LocTestCatalogs.HasWarning(warnings, "root must be a JSON object"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{ \"_meta\": { \"code\": \"vi\" }, \"k\": }")]
    [InlineData("not json at all")]
    [InlineData("{ 'k': 'single quotes' }")]
    public void TryParse_InvalidJson_ReturnsFalseWithWarningInsteadOfThrowing(string json)
    {
        var warnings = new List<string>();

        Assert.False(LanguageCatalog.TryParse(json, "broken.json", out _, warnings));

        Assert.True(LocTestCatalogs.HasWarning(warnings, "broken.json: invalid JSON"));
    }

    [Fact]
    public void TryParse_NonStringValues_IgnoredWithWarning()
    {
        const string json = """
            { "_meta": { "code": "vi" }, "num": 1, "obj": { "a": "b" }, "arr": ["x"], "nil": null, "ok": "fine" }
            """;
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse(json, "v.json", out var catalog, warnings));

        Assert.Equal(["ok"], catalog.Entries.Keys);
        Assert.Equal(4, warnings.Count);
        Assert.True(LocTestCatalogs.HasWarning(warnings, "'num' is not a string"));
        Assert.True(LocTestCatalogs.HasWarning(warnings, "'nil' is not a string"));
    }

    [Fact]
    public void TryParse_UnderscoreKeys_SkippedSilently()
    {
        const string json = """
            { "_meta": { "code": "vi" }, "_comment": "note", "_comment2": 5, "_section": { "x": 1 }, "k": "v" }
            """;
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse(json, "v.json", out var catalog, warnings));

        Assert.Empty(warnings);
        Assert.Equal(["k"], catalog.Entries.Keys);
    }

    [Fact]
    public void TryParse_CommentsAndTrailingCommas_Accepted()
    {
        const string json = """
            {
              // line comment
              "_meta": { "code": "vi", },
              /* block comment */
              "k": "v",
            }
            """;
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse(json, "v.json", out var catalog, warnings));

        Assert.Empty(warnings);
        Assert.Equal("v", catalog.Entries["k"]);
    }

    [Fact]
    public void TryParse_DuplicateKey_LastValueWins()
    {
        var catalog = LocTestCatalogs.Parse("""{ "_meta": { "code": "vi" }, "k": "first", "k": "second" }""");

        Assert.Equal("second", catalog.Entries["k"]);
    }

    [Theory]
    [InlineData("vi", true)]
    [InlineData("pt-br", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("x", false)]
    [InlineData("-vi", false)]
    [InlineData("vi.json", false)]
    [InlineData("ví", false)]
    public void IsValidCode_VariousCodes_MatchesRule(string code, bool expected)
    {
        Assert.Equal(expected, LanguageCatalog.IsValidCode(code));
    }
}

public sealed class LocalizerTests
{
    private static readonly LanguageCatalog English = LocTestCatalogs.English;

    [Fact]
    public void Create_EnglishOnly_UsesEnglishWithoutWarnings()
    {
        var localizer = Localizer.Create(English, []);

        Assert.Equal("en", localizer.Code);
        Assert.Equal(PluralRule.OneOther, localizer.Plural);
        Assert.Empty(localizer.Warnings);
        Assert.Equal("Plain text", localizer.Get("plain"));
        Assert.Equal("Hello Ann", localizer.Format("greet", new LocArg("name", "Ann")));
        Assert.True(localizer.Contains("greet"));
        Assert.Equal(English.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal), localizer.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Create_Overlay_OverridesEnglishAndTakesOverlayMeta()
    {
        var vi = LocTestCatalogs.Parse("""
            { "_meta": { "code": "vi", "nativeName": "Tiếng Việt", "plural": "none" }, "greet": "Chào {name}" }
            """);

        var localizer = Localizer.Create(English, [vi]);

        Assert.Empty(localizer.Warnings);
        Assert.Equal("vi", localizer.Code);
        Assert.Equal("Tiếng Việt", localizer.NativeName);
        Assert.Equal(PluralRule.None, localizer.Plural);
        Assert.Equal("Chào Ann", localizer.Format("greet", new LocArg("name", "Ann")));
        Assert.Equal("Plain text", localizer.Get("plain")); // untranslated key falls back to English
    }

    [Fact]
    public void Create_UnknownKey_IgnoredWithWarning()
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"not.in.english\": \"x\"");

        var localizer = Localizer.Create(English, [vi]);

        Assert.False(localizer.Contains("not.in.english"));
        Assert.Equal("not.in.english", localizer.Get("not.in.english"));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "vi: unknown key 'not.in.english' ignored"));
    }

    [Fact]
    public void Create_TranslationWithUnknownPlaceholder_RejectedKeepsEnglishWithWarning()
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"greet\": \"Chào {nam}\"");

        var localizer = Localizer.Create(English, [vi]);

        Assert.Equal("Hello Ann", localizer.Format("greet", new LocArg("name", "Ann")));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "'greet' uses unknown placeholder {nam}"));
    }

    [Fact]
    public void Create_TranslationDroppingPlaceholder_AcceptedWithWarning()
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"move\": \"Đã chuyển {count} tệp\"");

        var localizer = Localizer.Create(English, [vi]);

        Assert.Equal("Đã chuyển 2 tệp", localizer.Format("move", new LocArg("count", 2), new LocArg("folder", "X")));
        Assert.Single(localizer.Warnings);
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "'move' does not use placeholder {folder}"));
    }

    [Fact]
    public void Create_TranslationReorderingPlaceholders_AcceptedWithoutWarning()
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"move\": \"{folder} <- {count}\"");

        var localizer = Localizer.Create(English, [vi]);

        Assert.Empty(localizer.Warnings);
        Assert.Equal("D <- 2", localizer.Format("move", new LocArg("count", 2), new LocArg("folder", "D")));
    }

    [Theory]
    [InlineData("Chào {name")]
    [InlineData("Chào } {name}")]
    [InlineData("Chào {}")]
    public void Create_TranslationWithBadBraces_RejectedKeepsEnglishWithWarning(string text)
    {
        var vi = LocTestCatalogs.Overlay("vi", $"\"greet\": \"{text}\"");

        var localizer = Localizer.Create(English, [vi]);

        Assert.Equal("Hello Ann", localizer.Format("greet", new LocArg("name", "Ann")));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "'greet' has unbalanced braces"));
    }

    [Fact]
    public void Create_EnglishWithBadBraces_UsesLiteralWithWarning()
    {
        var english = LocTestCatalogs.Parse("""{ "_meta": { "code": "en" }, "bad": "50% {off" }""");

        var localizer = Localizer.Create(english, []);

        Assert.Equal("50% {off", localizer.Get("bad"));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "en: 'bad' has invalid placeholders"));
    }

    [Fact]
    public void Create_LaterOverlay_WinsPerKey()
    {
        var shipped = LocTestCatalogs.Overlay("vi", "\"plain\": \"shipped plain\", \"greet\": \"shipped {name}\"");
        var user = LocTestCatalogs.Overlay("vi", "\"greet\": \"user {name}\"");

        var localizer = Localizer.Create(English, [shipped, user]);

        Assert.Equal("user A", localizer.Format("greet", new LocArg("name", "A")));
        Assert.Equal("shipped plain", localizer.Get("plain"));
    }

    [Fact]
    public void Create_RejectedUserEntry_KeepsEarlierOverlayValue()
    {
        // A rejected user entry does not undo a valid shipped translation of the same key.
        var shipped = LocTestCatalogs.Overlay("vi", "\"greet\": \"shipped {name}\"");
        var user = LocTestCatalogs.Overlay("vi", "\"greet\": \"user {bogus}\"");

        var localizer = Localizer.Create(English, [shipped, user]);

        Assert.Equal("shipped A", localizer.Format("greet", new LocArg("name", "A")));
        Assert.True(LocTestCatalogs.HasWarning(localizer.Warnings, "unknown placeholder {bogus}"));
    }

    [Fact]
    public void Create_PriorWarnings_KeptFirst()
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"nope\": \"x\"");

        var localizer = Localizer.Create(English, [vi], ["file: broken"]);

        Assert.Equal("file: broken", localizer.Warnings[0]);
        Assert.Equal(2, localizer.Warnings.Count);
    }

    [Fact]
    public void Get_MissingKey_ReturnsKey()
    {
        var localizer = Localizer.Create(English, []);

        Assert.Equal("missing.key", localizer.Get("missing.key"));
        Assert.Equal("missing.key", localizer.Format("missing.key", new LocArg("a", 1)));
    }

    [Fact]
    public void Get_TemplateWithPlaceholders_LeavesPlaceholdersVisible()
    {
        Assert.Equal("Hello {name}", Localizer.Create(English, []).Get("greet"));
    }

    [Theory]
    [InlineData(0, "0 files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(-1, "-1 files")]
    [InlineData(21, "21 files")]
    public void FormatPlural_OneOther_PicksOneOnlyForCountOne(long count, string expected)
    {
        var localizer = Localizer.Create(English, []);

        Assert.Equal(expected, localizer.FormatPlural("files", count, new LocArg("count", count)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void FormatPlural_PluralNone_AlwaysUsesOther(long count)
    {
        var vi = LocTestCatalogs.Overlay("vi", "\"files.one\": \"MỘT\", \"files.other\": \"{count} tệp\"", plural: "none");

        var localizer = Localizer.Create(English, [vi]);

        Assert.Equal($"{count} tệp", localizer.FormatPlural("files", count, new LocArg("count", count)));
    }

    [Fact]
    public void FormatPlural_OneFormAbsent_FallsBackToOther()
    {
        var localizer = Localizer.Create(English, []);

        Assert.Equal("1 items", localizer.FormatPlural("items", 1, new LocArg("count", 1)));
    }

    [Fact]
    public void FormatPlural_NoFormsAtAll_ReturnsOtherKey()
    {
        Assert.Equal("nothing.other", Localizer.Create(English, []).FormatPlural("nothing", 1));
    }

    [Fact]
    public void BuiltInEnglish_EmbeddedCatalog_IsValidAndComplete()
    {
        var english = BuiltInCatalog.English;

        Assert.Equal("en", english.Code);
        Assert.NotEmpty(english.Entries);
        Assert.Empty(BuiltInCatalog.EnglishLocalizer.Warnings);
        Assert.Equal("en", BuiltInCatalog.EnglishLocalizer.Code);
    }

    [Fact]
    public void ShippedEnglishFile_MatchesEmbeddedCatalog()
    {
        var path = Path.Combine(LocTestCatalogs.RepositoryRoot, "src", "PhotoReview.Core", "Localization", "Languages", "en.json");

        var fromDisk = LocTestCatalogs.Parse(File.ReadAllText(path));

        Assert.Equal(BuiltInCatalog.English.Entries.OrderBy(p => p.Key, StringComparer.Ordinal),
            fromDisk.Entries.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>Parity gate: the shipped Vietnamese catalog must validate cleanly against built-in English.</summary>
    [Fact]
    public void ShippedVietnamese_AgainstBuiltInEnglish_HasNoWarnings()
    {
        var path = Path.Combine(LocTestCatalogs.RepositoryRoot, "src", "PhotoReview.Core", "Localization", "Languages", "vi.json");
        var warnings = new List<string>();

        Assert.True(LanguageCatalog.TryParse(File.ReadAllText(path), path, out var vi, warnings), string.Join("; ", warnings));
        Assert.Empty(warnings);
        Assert.Equal("vi", vi.Code);
        Assert.Equal(PluralRule.None, vi.Plural);

        var localizer = Localizer.Create(BuiltInCatalog.English, [vi]);

        Assert.Empty(localizer.Warnings);
    }
}

/// <summary>Tests that read or replace the process-wide <see cref="Localizer.Current"/>.</summary>
[Collection("GlobalState")]
public sealed class LocalizerCurrentTests
{
    [Fact]
    public void Current_NotReplaced_IsBuiltInEnglish()
    {
        // Every test that swaps Current restores it, so the ambient value is still the default here.
        Assert.Same(BuiltInCatalog.EnglishLocalizer, Localizer.Current);
        Assert.Equal("en", Localizer.Current.Code);
    }

    [Fact]
    public void SetCurrent_NewLocalizer_ReplacesCurrentAndRaisesCurrentChanged()
    {
        var previous = Localizer.Current;
        var replacement = Localizer.Create(LocTestCatalogs.English, [LocTestCatalogs.Overlay("vi", "\"plain\": \"Chữ\"")]);
        var raised = 0;
        Localizer? seenInHandler = null;
        void Handler(object? sender, EventArgs e)
        {
            raised++;
            seenInHandler = Localizer.Current;
        }

        Localizer.CurrentChanged += Handler;
        try
        {
            Localizer.SetCurrent(replacement);

            Assert.Equal(1, raised);
            Assert.Same(replacement, seenInHandler);
            Assert.Same(replacement, Localizer.Current);
            Assert.Equal("Chữ", Localizer.Current.Get("plain"));
        }
        finally
        {
            Localizer.CurrentChanged -= Handler;
            Localizer.SetCurrent(previous);
        }
        Assert.Same(previous, Localizer.Current);
    }

    [Fact]
    public void SetCurrent_Null_ThrowsAndKeepsCurrent()
    {
        var previous = Localizer.Current;

        Assert.Throws<ArgumentNullException>(() => Localizer.SetCurrent(null!));

        Assert.Same(previous, Localizer.Current);
    }
}
