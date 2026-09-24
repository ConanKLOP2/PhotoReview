using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

public sealed class TranslatorModesTests
{
    private static Localizer English() => BuiltInCatalog.EnglishLocalizer;

    [Fact]
    public void CreatePseudo_PlainText_IsAccentedBracketedAndLonger()
    {
        var pseudo = TranslatorModes.CreatePseudo(English());

        var text = pseudo.Get(TrKeys.TestSample);

        Assert.StartsWith("[", text, StringComparison.Ordinal);
        Assert.EndsWith("]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Sample", text, StringComparison.Ordinal);
        Assert.True(text.Length > "Sample".Length * 1.3, text);
        Assert.Equal(TranslatorModes.PseudoCode, pseudo.Code);
    }

    [Fact]
    public void CreatePseudo_Placeholders_StillFormatted()
    {
        var pseudo = TranslatorModes.CreatePseudo(English());

        var text = pseudo.Format(TrKeys.TestGreeting, new LocArg("name", "Ann"));

        Assert.Contains("Ann", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{name}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePseudo_PluralKeys_KeepPlural()
    {
        var pseudo = TranslatorModes.CreatePseudo(English());

        Assert.Contains("2", pseudo.FormatPlural("test.items", 2, new LocArg("count", 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateShowKeys_EveryText_IsItsKey()
    {
        var keys = TranslatorModes.CreateShowKeys(English());

        Assert.Equal("[test.sample]", keys.Get(TrKeys.TestSample));
        Assert.Equal("[test.greeting]", keys.Format(TrKeys.TestGreeting, new LocArg("name", "Ann")));
    }

    [Fact]
    public void Accent_AsciiLetters_MappedOneToOne()
    {
        var accented = TranslatorModes.Accent("abc XYZ 1!");

        Assert.Equal(10, accented.Length);
        Assert.EndsWith(" 1!", accented, StringComparison.Ordinal);
        Assert.NotEqual("abc XYZ 1!", accented);
    }
}
