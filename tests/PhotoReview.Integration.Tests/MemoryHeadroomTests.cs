using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

public sealed class MemoryHeadroomTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact(DisplayName = "EvaluateHeadroom fails closed when the snapshot is unavailable (CORE-11)")]
    public void EvaluateHeadroom_NullSnapshot_ReportsNoHeadroom() =>
        Assert.False(WindowsMemoryProbe.EvaluateHeadroom(null, 1.0, 0));

    [Fact(DisplayName = "EvaluateHeadroom accepts load below the limit with enough available bytes (CORE-11)")]
    public void EvaluateHeadroom_LowLoadAndEnoughAvailable_ReportsHeadroom() =>
        Assert.True(WindowsMemoryProbe.EvaluateHeadroom(new MemorySnapshot(50, (ulong)(8 * Gb)), 0.85, 2 * Gb));

    [Theory(DisplayName = "EvaluateHeadroom rejects load at or above the limit (CORE-11)")]
    [InlineData(85u)] // strictly-below rule: exactly at the limit is no headroom
    [InlineData(99u)]
    public void EvaluateHeadroom_LoadAtOrAboveLimit_ReportsNoHeadroom(uint load) =>
        Assert.False(WindowsMemoryProbe.EvaluateHeadroom(new MemorySnapshot(load, (ulong)(8 * Gb)), 0.85, 2 * Gb));

    [Fact(DisplayName = "EvaluateHeadroom rejects available bytes below the reserve but accepts exactly the reserve (CORE-11)")]
    public void EvaluateHeadroom_ReserveBoundary()
    {
        Assert.False(WindowsMemoryProbe.EvaluateHeadroom(new MemorySnapshot(10, (ulong)(2 * Gb - 1)), 0.85, 2 * Gb));
        Assert.True(WindowsMemoryProbe.EvaluateHeadroom(new MemorySnapshot(10, (ulong)(2 * Gb)), 0.85, 2 * Gb));
    }
}
