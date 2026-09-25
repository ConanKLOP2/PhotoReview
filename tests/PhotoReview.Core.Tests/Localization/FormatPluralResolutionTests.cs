using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Tests.Localization;

public sealed class FormatPluralResolutionTests
{
    private static readonly LocArg Count1 = new("count", 1);

    [Fact(DisplayName = "FormatPlural picks .one only for exactly 1 and only under one-other; falls back to .other")]
    public void FormatPlural_PicksFormByCountAndRule()
    {
        var oneOther = Localizer.Create(LocTestCatalogs.English, []);
        Assert.Equal("1 file", oneOther.FormatPlural("files", 1, Count1));
        Assert.Equal("0 files", oneOther.FormatPlural("files", 0, new LocArg("count", 0)));
        Assert.Equal("2 files", oneOther.FormatPlural("files", 2, new LocArg("count", 2)));
        Assert.Equal("1 items", oneOther.FormatPlural("items", 1, Count1)); // no items.one: .other is used

        var none = Localizer.Create(LocTestCatalogs.English, [LocTestCatalogs.Overlay("xx", "", plural: "none")]);
        Assert.Equal("1 files", none.FormatPlural("files", 1, Count1));
    }

    [Fact(DisplayName = "FormatPlural on a transformed localizer resolves the transformed templates")]
    public void FormatPlural_UsesTransformedTemplates()
    {
        var shouting = Localizer.Create(LocTestCatalogs.English, [])
            .Transform("xx", "xx", PluralRule.OneOther, (_, t) => t.AppendLiteral("!"));

        Assert.Equal("1 file!", shouting.FormatPlural("files", 1, Count1));
        Assert.Equal("5 files!", shouting.FormatPlural("files", 5, new LocArg("count", 5)));
    }

    [Fact(DisplayName = "FormatPlural returns the missing .other key when the base key is unknown")]
    public void FormatPlural_UnknownBaseKey_ReturnsKey()
    {
        var localizer = Localizer.Create(LocTestCatalogs.English, []);
        Assert.Equal("nope.other", localizer.FormatPlural("nope", 3, new LocArg("count", 3)));
        Assert.Equal("nope.other", localizer.FormatPlural("nope", 1, Count1));
    }
}
