using System.Numerics;

namespace PhotoReview.Imaging.Tests;

/// <summary>Overflow and monotonicity properties of the RAM-budget arithmetic against BigInteger references, over RAM sizes up to long.MaxValue.</summary>
[Trait("Category", "HotPath")]
public sealed class RamBudgetPropertyTests
{
    private static long RandomPhysical(Random rng) => rng.Next(6) switch
    {
        0 => rng.NextInt64(1, 1024),
        1 => rng.NextInt64(1L << 20, 1L << 34),
        2 => rng.NextInt64(1L << 34, 1L << 42),
        3 => long.MaxValue - rng.Next(0, 5),
        4 => rng.NextInt64(1L << 50, long.MaxValue),
        _ => 32L * 1024 * 1024 * 1024,
    };

    [Fact(DisplayName = "BytesForPercent equals exact big-integer percent arithmetic for every RAM size and percent 0..100, and never overflows")]
    public void BytesForPercent_IsExact()
    {
        var rng = new Random(303);
        for (var i = 0; i < 100_000; i++)
        {
            var physical = RandomPhysical(rng);
            var percent = rng.Next(-3, 101); // the documented domain; callers clamp to <= 90 first

            var actual = RamBudgetPolicy.BytesForPercent(percent, physical);

            var expected = percent <= 0 ? 0 : (long)(new BigInteger(physical) * percent / 100);
            Assert.True(actual == expected, $"physical={physical} percent={percent}: {actual} != {expected}");
        }
    }

    [Fact(DisplayName = "Clamps: the cache percent stays within [minimum, maximum] and is monotonic in the request; the preview budget is at least 1 byte and never above the share")]
    public void Clamps_HoldForAnyRam()
    {
        var rng = new Random(304);
        for (var i = 0; i < 50_000; i++)
        {
            var physical = RandomPhysical(rng);
            var requested = rng.Next(-50, 250);
            var source = rng.Next(3) == 0 ? 0 : rng.NextInt64(0, physical);

            var minimum = RamBudgetPolicy.MinimumCachePercent(physical);
            var clamped = RamBudgetPolicy.ClampCachePercent(requested, physical);
            Assert.InRange(clamped, minimum, PhotoReview.Core.Settings.PerformanceOptions.MaxImageCacheRamPercent);
            Assert.True(RamBudgetPolicy.ClampCachePercent(requested + 1, physical) >= clamped, $"not monotonic at {requested} on {physical}");

            var preview = RamBudgetPolicy.PreviewBytesForPercent(requested, physical, source);
            Assert.True(preview >= 1, $"preview budget {preview} for physical={physical} requested={requested} source={source}");
            Assert.True(preview <= RamBudgetPolicy.BytesForPercent(clamped, physical) || preview == 1);

            var sourceCapacity = RamBudgetPolicy.SourceBytesForPercent(rng.NextInt64(0, long.MaxValue), requested, physical);
            Assert.InRange(sourceCapacity, 0, (long)(physical * RamBudgetPolicy.MaxSourceBytesShare) + 1);
        }
    }
}
