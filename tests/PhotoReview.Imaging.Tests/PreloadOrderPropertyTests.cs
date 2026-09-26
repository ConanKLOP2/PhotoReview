namespace PhotoReview.Imaging.Tests;

/// <summary>
/// Property test of PreloadOrderService.Build against an independent classification of every index into its priority group:
/// travel window, skipped burst lead, small window behind, far ahead, far behind (the existing example tests are Slow and
/// only pin two positions).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class PreloadOrderPropertyTests
{
    private const int Forward = PreloadOrderService.ForwardLookahead;
    private const int Backward = PreloadOrderService.BackwardLookahead;
    private static readonly int[] SkippedLead = [51, 52, 53];

    /// <summary>Priority group of <paramref name="offset"/> (signed distance in the direction of travel), or -1 for the centre.</summary>
    private static int GroupOf(int offset, int lead, bool fullFolder, int forward, int backward)
    {
        if (offset == 0) return -1;
        if (offset > lead + forward) return fullFolder ? 3 : int.MaxValue; // int.MaxValue: not queued at all
        if (offset > lead) return 0;
        if (offset > 0) return 1;
        if (offset >= -backward) return 2;
        return fullFolder ? 4 : int.MaxValue;
    }

    [Fact(DisplayName = "For random centre, count, direction, lead and full-folder flag the order is in range, duplicate-free, complete for the mode, and grouped by priority")]
    public void Build_SatisfiesPriorityInvariants() => AssertPriorityInvariants(PreloadWindow.Default);

    /// <summary>feat/preload-window-setting: the same invariants hold for a user-configured window, not just the historical 32/8 default.</summary>
    [Theory(DisplayName = "The priority invariants hold for a user-configured preload window, not just the 32/8 default")]
    [InlineData(1, 0)]
    [InlineData(5, 2)]
    [InlineData(32, 8)]
    [InlineData(100, 50)]
    public void Build_SatisfiesPriorityInvariants_ForConfiguredWindow(int forward, int backward) =>
        AssertPriorityInvariants(new PreloadWindow(forward, backward));

    private static void AssertPriorityInvariants(PreloadWindow window)
    {
        var rng = new Random(5150);
        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var count = rng.Next(0, 4) == 0 ? rng.Next(0, 6) : rng.Next(1, 300);
            var center = rng.Next(-2, count + 3);
            var direction = rng.Next(-2, 3);
            var lead = rng.Next(0, 4) == 0 ? rng.Next(-5, 400) : rng.Next(0, 30);
            var fullFolder = rng.Next(2) == 0;
            var context = $"count={count} center={center} dir={direction} lead={lead} full={fullFolder} window={window.Forward}/{window.Backward}";

            var order = PreloadOrderService.Build(center, count, fullFolder, direction, lead, window).ToArray();

            if (center < 0 || center >= count)
            {
                Assert.True(order.Length == 0, "out-of-range centre must queue nothing: " + context);
                continue;
            }

            var dir = direction < 0 ? -1 : 1;
            var effectiveLead = Math.Clamp(lead, 0, count);
            Assert.True(order.All(i => i >= 0 && i < count && i != center), "index out of range or the centre itself: " + context);
            Assert.True(order.Distinct().Count() == order.Length, "duplicate index: " + context);

            var expected = Enumerable.Range(0, count)
                .Where(i => GroupOf(dir * (i - center), effectiveLead, fullFolder, window.Forward, window.Backward) is >= 0 and not int.MaxValue)
                .ToHashSet();
            Assert.True(expected.SetEquals(order), $"wrong membership (missing {string.Join(",", expected.Except(order).Take(5))}, extra {string.Join(",", order.Except(expected).Take(5))}): " + context);

            var groups = order.Select(i => GroupOf(dir * (i - center), effectiveLead, fullFolder, window.Forward, window.Backward)).ToArray();
            for (var i = 1; i < groups.Length; i++)
                Assert.True(groups[i] >= groups[i - 1], $"group order broken at position {i}: " + context);

            // Inside every group the nearest image comes first.
            for (var i = 1; i < order.Length; i++)
            {
                if (groups[i] != groups[i - 1]) continue;
                var previous = Math.Abs(order[i - 1] - center);
                var current = Math.Abs(order[i] - center);
                Assert.True(current > previous, $"not nearest-first inside group {groups[i]} at position {i}: " + context);
            }
        }
    }

    [Fact(DisplayName = "The direction of travel decides which side gets the long window, and lead shifts it away from the current image")]
    public void Build_DirectionAndLead_ExamplePositions()
    {
        var next = PreloadOrderService.Build(50, 200, fullFolder: false, direction: 1, lead: 0).ToArray();
        Assert.Equal(51, next[0]);
        Assert.Equal(82, next[Forward - 1]);
        Assert.Equal(49, next[Forward]);

        var previous = PreloadOrderService.Build(50, 200, fullFolder: false, direction: -1, lead: 0).ToArray();
        Assert.Equal(49, previous[0]);
        Assert.Equal(18, previous[Forward - 1]);
        Assert.Equal(51, previous[Forward]);

        var burst = PreloadOrderService.Build(50, 200, fullFolder: false, direction: 1, lead: 3).ToArray();
        Assert.Equal(54, burst[0]);                    // starts lead + 1 ahead
        Assert.Equal(SkippedLead, burst.Skip(Forward).Take(3).ToArray()); // the skipped images follow the shifted window
    }
}
