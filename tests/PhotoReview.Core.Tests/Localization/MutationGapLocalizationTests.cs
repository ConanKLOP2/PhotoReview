using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Localization;

public sealed class MutationGapLocalizationTests
{
    private static Localizer OnlyOneForm(string plural = "one-other") =>
        Localizer.Create(LocTestCatalogs.Parse("""
            { "_meta": { "code": "en", "name": "English", "nativeName": "English", "plural": "one-other" },
              "solo.one": "single {count}" }
            """), plural == "one-other" ? [] : [LocTestCatalogs.Overlay("xx", "", plural)]);

    [Fact(DisplayName = "FormatPlural: a base key with only .one still resolves .one for count 1 and reports the missing .other key otherwise")]
    public void FormatPlural_OnlyOneForm_OneOther()
    {
        var localizer = OnlyOneForm();
        var one = new LocArg("count", 1);
        var five = new LocArg("count", 5);

        Assert.Equal("single 1", localizer.FormatPlural("solo", 1, one));
        Assert.Equal("solo.other", localizer.FormatPlural("solo", 5, five));
    }

    [Fact(DisplayName = "FormatPlural: under the 'none' rule count 1 never selects .one, even when only .one exists")]
    public void FormatPlural_OnlyOneForm_NoneRule()
    {
        var localizer = OnlyOneForm("none");

        Assert.Equal("solo.other", localizer.FormatPlural("solo", 1, new LocArg("count", 1)));
    }

    [Fact(DisplayName = "TranslatorModes.Accent maps the first alphabet letter (index 0) too")]
    public void Accent_MapsFirstLetter()
    {
        Assert.Equal("á", TranslatorModes.Accent("a"));
        Assert.Equal("Á", TranslatorModes.Accent("A"));
        Assert.Equal("1-á", TranslatorModes.Accent("1-a"));
    }

    [Fact(DisplayName = "Pseudo-locale pads by ceil(0.35 x length) tildes and adds nothing to an empty text")]
    public void CreatePseudo_PaddingLength()
    {
        var english = Localizer.Create(LocTestCatalogs.Parse("""
            { "_meta": { "code": "en", "name": "English", "nativeName": "English", "plural": "one-other" },
              "ten": "abcdefghij", "empty": "" }
            """), []);

        var pseudo = TranslatorModes.CreatePseudo(english);

        Assert.Equal("[" + TranslatorModes.Accent("abcdefghij") + " ~~~~]", pseudo.Get("ten"));
        Assert.Equal("[]", pseudo.Get("empty"));
    }

    [Fact(DisplayName = "JsonAliasAttribute with a null alias array exposes an empty (not null) Aliases")]
    public void JsonAlias_NullArray_IsEmpty()
    {
        var attribute = new JsonAliasAttribute(null!);

        Assert.NotNull(attribute.Aliases);
        Assert.Empty(attribute.Aliases);
    }
}
