using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

public class LanguageCatalogRobustnessTests
{
    [Fact]
    public void TryParse_LoneSurrogateEscape_ReportsWarningInsteadOfThrowing()
    {
        const string json = """{ "_meta": { "code": "xx" }, "key": "\ud800" }""";
        var warnings = new List<string>();

        var ok = LanguageCatalog.TryParse(json, "bad.json", out _, warnings);

        Assert.False(ok);
        Assert.Contains(warnings, w => w.Contains("bad.json", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "W2CM-03: blank or control-only name/nativeName fall back to the next name and finally the code")]
    [InlineData("\"\"", "\" \"", "xx", "xx")]
    [InlineData("\"French\"", "\" \\t\\n \"", "French", "French")]
    [InlineData("\"\"", "\"Fran\u00e7ais\"", "xx", "Français")]
    public void BlankDisplayNames_FallBack(string nameJson, string nativeJson, string expectedName, string expectedNative)
    {
        var warnings = new List<string>();
        var json = "{ \"_meta\": { \"code\": \"xx\", \"name\": " + nameJson + ", \"nativeName\": " + nativeJson + " } }";

        Assert.True(LanguageCatalog.TryParse(json, "t.json", out var catalog, warnings));

        Assert.Equal(expectedName, catalog.Name);
        Assert.Equal(expectedNative, catalog.NativeName);
    }

    [Fact(DisplayName = "W2CM-03: display names are trimmed, stripped of control characters and capped")]
    public void DisplayNames_AreCleaned()
    {
        var json = "{ \"_meta\": { \"code\": \"xx\", \"name\": \"  Eng\\u0007lish \", \"nativeName\": \"" + new string('n', 500) + "\" } }";

        Assert.True(LanguageCatalog.TryParse(json, "t.json", out var catalog, new List<string>()));

        Assert.Equal("Eng lish", catalog.Name);
        Assert.Equal(64, catalog.NativeName.Length);
    }

    [Fact(DisplayName = "W2CM-02: key names with line breaks cannot forge extra log lines through catalog warnings")]
    public void WarningText_HasNoRawLineBreaks()
    {
        var warnings = new List<string>();
        const string json = "{ \"_meta\": { \"code\": \"xx\", \"plural\": \"a\\nb\" }, \"k\\n2026-10-04 [ERROR] forged\": 5, \"d\\r\\ne\": \"1\", \"d\\r\\ne\": \"2\" }";

        LanguageCatalog.TryParse(json, "t.json", out _, warnings);

        Assert.True(warnings.Count >= 3);
        Assert.All(warnings, w => Assert.DoesNotContain(w, c => char.IsControl(c)));
    }

    [Fact(DisplayName = "W2CM-02: Localizer.Create warnings do not echo raw control characters from an overlay key")]
    public void LocalizerWarning_HasNoRawLineBreaks()
    {
        var overlay = LocTestCatalogs.Overlay("vi", "\"bad\\nkey\": \"x\"");

        var localizer = Localizer.Create(LocTestCatalogs.English, [overlay]);

        Assert.NotEmpty(localizer.Warnings);
        Assert.All(localizer.Warnings, w => Assert.DoesNotContain(w, c => char.IsControl(c)));
    }
}
