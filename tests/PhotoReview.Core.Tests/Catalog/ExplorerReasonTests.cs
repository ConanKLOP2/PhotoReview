using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class ExplorerReasonTests
{
    [Fact]
    public void Format_WithoutDetails_IsTheBareCode() =>
        Assert.Equal("timeout", ExplorerReason.Format(ExplorerReason.Timeout));

    [Fact]
    public void FormatThenParse_RoundTripsCodeAndDetails()
    {
        var reason = ExplorerReason.Format(ExplorerReason.ComCallFailed, "IFolderView2.GetItem(3)", "0x80004005");

        var (code, details) = ExplorerReason.Parse(reason);

        Assert.Equal(ExplorerReason.ComCallFailed, code);
        Assert.Equal(["IFolderView2.GetItem(3)", "0x80004005"], details);
    }

    [Fact]
    public void Parse_TextWithoutSeparator_IsItsOwnCodeWithNoDetails()
    {
        var (code, details) = ExplorerReason.Parse("Something legacy");

        Assert.Equal("Something legacy", code);
        Assert.Empty(details);
    }

    [Fact]
    public void Codes_AreLanguageNeutral_NoProse()
    {
        var codes = typeof(ExplorerReason).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, c => Assert.Matches("^[a-z]+(-[a-z]+)*$", c));
    }
}
