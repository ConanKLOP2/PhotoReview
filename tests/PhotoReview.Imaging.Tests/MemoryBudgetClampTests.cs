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

    [Fact]
    public void SourceBytesCache_HugeRequest_IsClampedToHalfOfPhysical()
    {
        var physical = RamBudgetPolicy.GetPhysicalMemoryBytes();
        Assert.True(physical > 0);

        var cache = new SourceBytesCache(long.MaxValue / 2);

        Assert.Equal(RamBudgetPolicy.ClampToPhysicalMemory(long.MaxValue / 2, physical), cache.CapacityBytes);
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
