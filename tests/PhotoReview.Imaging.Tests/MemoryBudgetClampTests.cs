using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Preload;

namespace PhotoReview.Imaging.Tests;

/// <summary>IMG-11: preview and source-bytes budgets never exceed half of physical RAM.</summary>
public sealed class MemoryBudgetClampTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(16 * Gib, 32 * Gib, 16 * Gib)] // dev box: unchanged
    [InlineData(16 * Gib, 16 * Gib, 8 * Gib)]  // 16 GB machine: halved
    [InlineData(4 * Gib, 16 * Gib, 4 * Gib)]   // already smaller: unchanged
    [InlineData(16 * Gib, 0, 16 * Gib)]        // unknown physical size: unchanged
    public void ClampToPhysicalMemory_LimitsToHalfOfPhysical(long requested, long physical, long expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.ClampToPhysicalMemory(requested, physical));
    }

    [Theory]
    [InlineData(16 * Gib, 16 * Gib, 0, 8 * Gib)]        // no source cache: preview keeps the 50% share
    [InlineData(16 * Gib, 16 * Gib, 3 * Gib, 5 * Gib)]  // source cache present: the two together stay within 50%
    [InlineData(2 * Gib, 16 * Gib, 3 * Gib, 2 * Gib)]   // already fits
    [InlineData(16 * Gib, 32 * Gib, 6 * Gib, 10 * Gib)]
    [InlineData(16 * Gib, 0, 6 * Gib, 16 * Gib)]        // unknown physical size: unchanged
    public void ClampPreviewToPhysicalMemory_LeavesRoomForSourceBytes(long requested, long physical, long source, long expected)
    {
        Assert.Equal(expected, RamBudgetPolicy.ClampPreviewToPhysicalMemory(requested, physical, source));
    }

    [Fact]
    public void ClampSourceBytesToPhysicalMemory_LimitsToTwentyPercent()
    {
        Assert.Equal((long)(16 * Gib * 0.2), RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(16 * Gib, 16 * Gib));
        Assert.Equal(1 * Gib, RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(1 * Gib, 16 * Gib));
    }

    [Fact]
    public async Task PreviewService_CapacityBytes_ReportsEffectiveClampedBudget()
    {
        var physical = RamBudgetPolicy.GetPhysicalMemoryBytes();
        Assert.True(physical > 0);
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Clamp-" + Guid.NewGuid().ToString("N"));
        var source = new SourceBytesCache(long.MaxValue / 2);
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 100,
            capacityBytes: long.MaxValue / 2, diskCacheDirectory: dir, disableDiskCacheOverride: true, sourceBytesCache: source);

        try
        {
            Assert.True(service.CapacityBytes + source.CapacityBytes <= physical / 2 + 1);
            Assert.True(service.CapacityBytes > 0);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    [Fact]
    public void SourceBytesCache_HugeRequest_IsClampedToHalfOfPhysical()
    {
        var physical = RamBudgetPolicy.GetPhysicalMemoryBytes();
        Assert.True(physical > 0);

        var cache = new SourceBytesCache(long.MaxValue / 2);

        Assert.Equal(RamBudgetPolicy.ClampSourceBytesToPhysicalMemory(long.MaxValue / 2, physical), cache.CapacityBytes);
        Assert.True(cache.CapacityBytes <= physical / 2);
    }

    [Fact]
    public async Task PreviewImageService_HugeRequest_LogsClampedEffectiveBudget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoReview-Clamp-" + Guid.NewGuid().ToString("N"));
        var log = new CapturingLog();
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 100,
            capacityBytes: long.MaxValue / 2, diskCacheDirectory: dir, disableDiskCacheOverride: true, log: log);
        try
        {
            var line = Assert.Single(log.Infos, m => m.StartsWith("Memory budgets:", StringComparison.Ordinal));
            Assert.Contains("clamped", line, StringComparison.Ordinal);
            Assert.Contains("source-bytes cache off", line, StringComparison.Ordinal);
        }
        finally
        {
            await service.ShutdownPersistWorkersAsync();
        }
    }

    private sealed class CapturingLog : ILog
    {
        public List<string> Infos { get; } = [];
        public bool Enabled => true;
        public void Info(string message) => Infos.Add(message);
        public void Warn(string message) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
