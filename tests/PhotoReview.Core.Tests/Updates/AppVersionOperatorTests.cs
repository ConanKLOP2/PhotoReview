using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

public sealed class AppVersionOperatorTests
{
    private static AppVersion V(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var v), text);
        return v;
    }

    [Theory(DisplayName = "Relational operators agree with CompareTo for less, equal and greater")]
    [InlineData("2.0.92", "2.0.93", -1)]
    [InlineData("2.0.93", "2.0.93", 0)]
    [InlineData("2.0.94", "2.0.93", 1)]
    [InlineData("2.0.93-beta.1", "2.0.93", -1)]
    public void Operators_MatchOrdering(string left, string right, int sign)
    {
        var l = V(left);
        var r = V(right);

        Assert.Equal(sign < 0, l < r);
        Assert.Equal(sign > 0, l > r);
        Assert.Equal(sign <= 0, l <= r);
        Assert.Equal(sign >= 0, l >= r);
    }

    [Theory(DisplayName = "A number with a trailing NUL or other non-digit character is rejected (int.TryParse alone would accept a trailing NUL)")]
    [InlineData("1.2.3\0")]
    [InlineData("1.2\0.3")]
    [InlineData("1.2.3x")]
    [InlineData("1.2.-3")]
    [InlineData("+1.2.3")]
    [InlineData("-1.2.3")]
    public void TryParse_RejectsNonDigits(string text)
    {
        Assert.False(AppVersion.TryParse(text, out var version));
        Assert.Equal(default, version);
    }

    [Fact(DisplayName = "Empty components are rejected")]
    public void TryParse_RejectsEmptyComponents()
    {
        Assert.False(AppVersion.TryParse("1..3", out _));
        Assert.False(AppVersion.TryParse(".2.3", out _));
        Assert.False(AppVersion.TryParse("1.2.", out _));
    }
}
