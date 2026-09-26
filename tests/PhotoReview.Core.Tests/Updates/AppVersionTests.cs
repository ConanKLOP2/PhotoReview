using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

public sealed class AppVersionTests
{
    [Theory(DisplayName = "Version comparison: latest vs current decides whether an update exists")]
    [InlineData("2.0.93", "2.0.93", 0)]
    [InlineData("2.0.94", "2.0.93", 1)]
    [InlineData("2.0.92", "2.0.93", -1)]
    [InlineData("2.1.0", "2.0.99", 1)]
    [InlineData("3.0.0", "2.99.99", 1)]
    [InlineData("2.0.100", "2.0.99", 1)]            // numeric, not textual ("100" < "99" as text)
    [InlineData("v2.0.94", "2.0.93", 1)]            // leading v
    [InlineData("V2.0.93", "2.0.93", 0)]
    [InlineData("2.0.93", "2.0.93+1a2b3c4d", 0)]     // build metadata ignored
    [InlineData("2.0.94", "2.0.93+1a2b3c4d.dirty", 1)]
    [InlineData("2.0.93", "2.0.93-beta.1", 1)]      // release beats its own pre-release
    [InlineData("2.0.93-beta.1", "2.0.93", -1)]
    [InlineData("2.0.94-rc.1", "2.0.93", 1)]        // pre-release of a higher number is still higher
    public void CompareTo_OrdersVersionsNumerically(string left, string right, int expectedSign)
    {
        Assert.True(AppVersion.TryParse(left, out var l));
        Assert.True(AppVersion.TryParse(right, out var r));
        Assert.Equal(expectedSign, Math.Sign(l.CompareTo(r)));
    }

    [Theory(DisplayName = "Unparsable version text is rejected, never thrown")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("2.0")]
    [InlineData("2.0.93.1")]
    [InlineData("2.x.93")]
    [InlineData("2..93")]
    [InlineData("-1.0.0")]
    [InlineData("2.0.93-")]
    [InlineData("99999999999.0.0")]
    [InlineData("v")]
    public void TryParse_Garbage_ReturnsFalse(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact(DisplayName = "Pre-release suffix is flagged and stripped from the numbers")]
    public void TryParse_Prerelease_IsFlagged()
    {
        Assert.True(AppVersion.TryParse("v2.1.0-beta.2+abc", out var v));
        Assert.True(v.IsPrerelease);
        Assert.Equal("2.1.0", v.ToString());
        Assert.True(AppVersion.TryParse("2.1.0", out var stable));
        Assert.False(stable.IsPrerelease);
    }
}
