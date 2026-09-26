using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

public sealed class LocalizerPartialOverlayTests
{
    private static readonly LanguageCatalog English = LocTestCatalogs.English;

    private static readonly LanguageCatalog ShippedVi = LocTestCatalogs.Parse("""
        { "_meta": { "code": "vi", "nativeName": "Tiếng Việt", "plural": "none" }, "files.other": "{count} tệp" }
        """);

    [Fact(DisplayName = "A user overlay with only _meta.code keeps the shipped nativeName and plural rule (docs/TRANSLATING.md partial override)")]
    public void Create_PartialUserOverlay_KeepsShippedMeta()
    {
        var userVi = LocTestCatalogs.Parse("""{ "_meta": { "code": "vi" }, "greet": "Chào {name}" }""");

        var localizer = Localizer.Create(English, [ShippedVi, userVi]);

        Assert.Equal("vi", localizer.Code);
        Assert.Equal("Tiếng Việt", localizer.NativeName);
        Assert.Equal(PluralRule.None, localizer.Plural);
        Assert.Equal("1 tệp", localizer.FormatPlural("files", 1, new LocArg("count", 1)));
        Assert.Equal("Chào Ann", localizer.Format("greet", new LocArg("name", "Ann")));
    }

    [Fact(DisplayName = "A user overlay that declares plural / nativeName still overrides the shipped values")]
    public void Create_UserOverlayDeclaringMeta_Wins()
    {
        var userVi = LocTestCatalogs.Parse("""{ "_meta": { "code": "vi", "nativeName": "VN", "plural": "one-other" } }""");

        var localizer = Localizer.Create(English, [ShippedVi, userVi]);

        Assert.Equal("VN", localizer.NativeName);
        Assert.Equal(PluralRule.OneOther, localizer.Plural);
    }

    [Fact(DisplayName = "A lone overlay of a different language without plural/nativeName gets its own defaults, not English's")]
    public void Create_DifferentLanguageOverlay_UsesItsOwnDefaults()
    {
        var fr = LocTestCatalogs.Parse("""{ "_meta": { "code": "fr" } }""");

        var localizer = Localizer.Create(English, [fr]);

        Assert.Equal("fr", localizer.NativeName);
        Assert.Equal(PluralRule.OneOther, localizer.Plural);
    }
}
