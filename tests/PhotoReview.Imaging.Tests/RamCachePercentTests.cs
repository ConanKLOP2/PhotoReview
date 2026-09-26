using System.IO;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>RAM%: the user-configurable cache share of physical RAM (min decided by the preload window, max 90).</summary>
public sealed class RamCachePercentTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private const long Mib = 1024L * 1024;

    [Fact]
    public void MinimumPreviewWindowBytes_Is41UhdPreviewsAt4BytesPerPixel()
    {
        Assert.Equal(41, RamBudgetPolicy.PreloadWindowImageCount);
        Assert.Equal(41L * 3840 * 2160 * 4, RamBudgetPolicy.MinimumPreviewWindowBytes());
    }

    [Fact]
    public void MinimumPreviewWindowBytes_ForWindow_ScalesWithImageCount()
    {
        // feat/preload-window-setting: default (32/8 -> 41 previews) is unchanged; a (1,0) window needs only 2.
        Assert.Equal(RamBudgetPolicy.MinimumPreviewWindowBytes(), RamBudgetPolicy.MinimumPreviewWindowBytes(PreloadWindow.Default));
        Assert.Equal(2L * 3840 * 2160 * 4, RamBudgetPolicy.MinimumPreviewWindowBytes(new PreloadWindow(1, 0)));
    }

    [Theory]
    [InlineData(16 * Gib, 8)]
    [InlineData(32 * Gib, 4)]
    [InlineData(64 * Gib, 2)]
    [InlineData(128 * Gib, 1)]          // 0.99 % rounds up to 1
    [InlineData(1024 * Gib, 1)]         // never below 1
    [InlineData(1 * Gib, 90)]           // preload window does not fit: capped at the maximum
    [InlineData(17_003_520_000L, 8)]    // exactly 8 %: rounding up must not overshoot
    [InlineData(17_003_519_999L, 9)]    // one byte less: 8.0000001 % rounds up
    [InlineData(0, 1)]                  // unknown RAM
    [InlineData(-5, 1)]
    public void MinimumCachePercent_RoundsPreloadWindowUpWithinOneToNinety(long physical, int expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.MinimumCachePercent(physical));
    }

    [Theory]
    [InlineData(50, 32 * Gib, 50)]
    [InlineData(2, 16 * Gib, 8)]     // below the device minimum
    [InlineData(95, 32 * Gib, 90)]   // above the maximum
    [InlineData(90, 32 * Gib, 90)]
    [InlineData(0, 0, 1)]            // unknown RAM: absolute floor
    public void ClampCachePercent_ClampsToDeviceMinimumAndNinety(int requested, long physical, int expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.ClampCachePercent(requested, physical));
    }

    [Theory]
    [InlineData(50, 32 * Gib, 16 * Gib)]
    [InlineData(90, 101, 90)]        // floor of 90.9
    [InlineData(25, 400, 100)]
    [InlineData(33, 0, 0)]
    [InlineData(0, 32 * Gib, 0)]
    public void BytesForPercent_IsExactFloor(int percent, long physical, long expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.BytesForPercent(percent, physical));
    }

    [Fact]
    public void BytesForPercent_HugePhysical_DoesNotOverflow()
    {
        Assert.Equal(long.MaxValue / 100 * 90 + long.MaxValue % 100 * 90 / 100, RamBudgetPolicy.BytesForPercent(90, long.MaxValue));
    }

    [Theory]
    [InlineData(50, 32 * Gib, 0, 16 * Gib)]               // default on the dev box = today's 16 GiB
    [InlineData(50, 32 * Gib, 3 * Gib, 13 * Gib)]         // source-bytes cache takes its share out of the same budget
    [InlineData(75, 32 * Gib, 0, 24 * Gib)]
    [InlineData(200, 100 * Gib, 0, 90 * Gib)]             // clamped to 90 %
    [InlineData(50, 32 * Gib, 40 * Gib, 1)]               // nothing left: never zero (LRU rejects 0)
    public void PreviewBytesForPercent_PercentMinusSourceBytes(int percent, long physical, long source, long expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.PreviewBytesForPercent(percent, physical, source));
    }

    [Fact]
    public void PreviewBytesForPercent_BelowMinimum_StillHoldsPreloadWindow()
    {
        var bytes = RamBudgetPolicy.PreviewBytesForPercent(1, 16 * Gib, 0);

        Assert.Equal(RamBudgetPolicy.BytesForPercent(8, 16 * Gib), bytes);
        Assert.True(bytes >= RamBudgetPolicy.MinimumPreviewWindowBytes());
    }

    [Fact]
    public void PreviewBytesForPercent_UnknownPhysical_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RamBudgetPolicy.PreviewBytesForPercent(50, 0, 0));
    }

    [Theory]
    [InlineData(16 * Gib, 50, 32 * Gib, 6_871_947_673L)]  // the usual 20 % source-bytes cap wins
    [InlineData(1 * Gib, 50, 32 * Gib, 1 * Gib)]          // small request unchanged
    [InlineData(16 * Gib, 4, 32 * Gib, 14_107_934L)]      // 4 % leaves only (1.28 GiB - preload window) for source bytes
    [InlineData(16 * Gib, 50, 0, 16 * Gib)]               // unknown RAM: unchanged
    public void SourceBytesForPercent_LeavesPreloadWindowToPreviews(long requested, int percent, long physical, long expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.SourceBytesForPercent(requested, percent, physical));
    }

    [Theory]
    [InlineData(16 * Gib)]
    [InlineData(32 * Gib)]
    [InlineData(64 * Gib)]
    public void PreviewPlusSource_NeverExceedsChosenPercent_AndPreviewKeepsPreloadWindow(long physical)
    {
        for (var percent = 1; percent <= 100; percent++)
        {
            var source = RamBudgetPolicy.SourceBytesForPercent(long.MaxValue / 4, percent, physical);
            var preview = RamBudgetPolicy.PreviewBytesForPercent(percent, physical, source);
            var budget = RamBudgetPolicy.BytesForPercent(RamBudgetPolicy.ClampCachePercent(percent, physical), physical);

            Assert.True(source + preview <= budget, $"{percent}%: {source} + {preview} > {budget}");
            Assert.True(preview >= RamBudgetPolicy.MinimumPreviewWindowBytes(), $"{percent}%: preview {preview}");
        }
    }

    [Fact]
    public void ResolveCapacity_WithPercent_UsesPercentAndLogsShare()
    {
        var capacity = PreviewImageService.ResolveCapacity(PerformanceOptions.ImageCacheCapacityBytes, 60, 32 * Gib, 2 * Gib, out var line);

        Assert.Equal(RamBudgetPolicy.BytesForPercent(60, 32 * Gib) - 2 * Gib, capacity);
        Assert.StartsWith("Memory budgets:", line, StringComparison.Ordinal);
        Assert.Contains("cache share 60%", line, StringComparison.Ordinal);
        Assert.Contains("source-bytes cache 2048 MiB", line, StringComparison.Ordinal);
        Assert.DoesNotContain("clamped", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCapacity_OutOfRangePercent_LogsClamp()
    {
        var capacity = PreviewImageService.ResolveCapacity(1, 150, 32 * Gib, null, out var line);

        Assert.Equal(RamBudgetPolicy.BytesForPercent(90, 32 * Gib), capacity);
        Assert.Contains("requested 150% clamped to 90%; allowed 4-90%", line, StringComparison.Ordinal);
        Assert.Contains("source-bytes cache off", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCapacity_UnknownPhysical_FallsBackToByteBudget()
    {
        var capacity = PreviewImageService.ResolveCapacity(3 * Gib, 50, 0, null, out var line);

        Assert.Equal(3 * Gib, capacity);
        Assert.Contains("physical RAM unknown", line, StringComparison.Ordinal);
        Assert.Contains((3 * Gib / Mib).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MiB", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveCapacity_NoPercent_KeepsHalfOfPhysicalClamp()
    {
        var capacity = PreviewImageService.ResolveCapacity(64 * Gib, null, 32 * Gib, null, out var line);

        Assert.Equal(16 * Gib, capacity);
        Assert.Contains("to 50% of physical RAM", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewImageService_CacheRamPercent_SetsEffectiveCapacity()
    {
        var physical = RamBudgetPolicy.GetPhysicalMemoryBytes();
        Assert.True(physical > 0);
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-RamPercent-" + Guid.NewGuid().ToString("N"));
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 100,
            capacityBytes: 1 * Gib, diskCacheDirectory: dir, disableDiskCacheOverride: true, cacheRamPercent: 200);
        try
        {
            Assert.Equal(RamBudgetPolicy.BytesForPercent(90, physical), service.CapacityBytes);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }
}
