using PhotoReview.Core.Abstractions;
using Xunit;

namespace PhotoReview.Core.Tests.Abstractions;

[Trait("Category", "HotPath")]
public class SystemClockTests
{
    [Fact]
    public void UtcNowReturnsCurrentUtcDateTime()
    {
        var clock = SystemClock.Instance;
        var before = DateTime.UtcNow;
        var clockTime = clock.UtcNow;
        var after = DateTime.UtcNow;

        Assert.True(clockTime >= before.AddMilliseconds(-100));
        Assert.True(clockTime <= after.AddMilliseconds(100));
        Assert.Equal(DateTimeKind.Utc, clockTime.Kind);
    }
}
