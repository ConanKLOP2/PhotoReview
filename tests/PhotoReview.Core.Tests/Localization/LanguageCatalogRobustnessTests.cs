using PhotoReview.Core.Localization;
using Xunit;

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
}
