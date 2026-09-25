using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Diagnostics;

/// <summary>R2-F-32: the analyzer reads the perf CSV line by line, so the writer must never emit a raw line break inside a field.</summary>
public sealed class PerfCsvEscapeTests
{
    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\rb", "a b")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void CsvEscape_FlattensLineBreaks(string input, string expected) =>
        Assert.Equal(expected, PerfCsvListener.CsvEscape(input));

    [Fact]
    public void CsvEscape_QuotesCommasAndDoublesQuotes_WithoutLineBreaks()
    {
        var escaped = PerfCsvListener.CsvEscape("x,\"y\"\nz");

        Assert.Equal("\"x,\"\"y\"\" z\"", escaped);
        Assert.DoesNotContain('\n', escaped);
    }
}
