using PhotoReview.Core.Abstractions;
using Xunit;

namespace PhotoReview.Core.Tests.Abstractions;

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

    [Fact]
    public void TimestampReturnsNonZeroAndIncreases()
    {
        var clock = SystemClock.Instance;
        var t1 = clock.Timestamp;
        Thread.SpinWait(100);
        var t2 = clock.Timestamp;

        Assert.True(t1 > 0);
        Assert.True(t2 >= t1);
    }
}
