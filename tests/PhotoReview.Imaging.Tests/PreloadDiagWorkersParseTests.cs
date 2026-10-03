namespace PhotoReview.Imaging.Tests;

/// <summary>The diagnostic preload worker-count override accepts only an integer in [0, 16].</summary>
public sealed class PreloadDiagWorkersParseTests
{
    [Theory(DisplayName = "ParseDiagWorkers accepts integers 0..16 and rejects everything else")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("-1", null)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("16", 16)]
    [InlineData("17", null)]
    [InlineData("8", 8)]
    [InlineData(" 4 ", 4)]
    [InlineData("+3", 3)]
    [InlineData("2147483648", null)]
    [InlineData("1.5", null)]
    public void ParseDiagWorkers_Value_ReturnsExpected(string? value, int? expected)
    {
        Assert.Equal(expected, PreloadScheduler.ParseDiagWorkers(value));
    }
}