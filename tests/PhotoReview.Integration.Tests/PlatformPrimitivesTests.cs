using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PlatformPrimitivesTests
{
    [Fact(DisplayName = "WindowsMemoryProbe reports realistic OS memory metrics")]
    public void WindowsMemoryProbeReportsRealisticMetrics()
    {
        var probe = WindowsMemoryProbe.Instance;
        var snapshot = probe.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Value.LoadPercent <= 100);
        Assert.True(snapshot.Value.AvailableBytes > 0);
        Assert.True(probe.GetAvailableMemoryBytes() > 0);
        Assert.True(probe.HasHeadroom(1.0, 0));
    }

    [Fact(DisplayName = "WindowsNaturalComparer orders strings naturally with numbers")]
    public void WindowsNaturalComparerOrdersNaturally()
    {
        var comparer = WindowsNaturalComparer.Instance;

        Assert.True(comparer.Compare("photo1.jpg", "photo2.jpg") < 0);
        Assert.True(comparer.Compare("photo2.jpg", "photo10.jpg") < 0);
        Assert.True(comparer.Compare("photo10.jpg", "photo2.jpg") > 0);
        Assert.Equal(0, comparer.Compare("PHOTO1.JPG", "photo1.jpg"));
        Assert.True(comparer.Compare(null, "a") < 0);
        Assert.True(comparer.Compare("a", null) > 0);
        Assert.Equal(0, comparer.Compare(null, null));
    }

    [Fact(DisplayName = "InstanceLock detects concurrent ownership of the same key")]
    public void InstanceLockDetectsConcurrentOwnership()
    {
        var key = "test-key-" + Guid.NewGuid().ToString("N");
        using var lock1 = new InstanceLock(key);
        Assert.True(lock1.IsOwner);

        using var lock2 = new InstanceLock(key);
        Assert.False(lock2.IsOwner);
    }

    [Fact(DisplayName = "WindowsRecycleBin rejects invalid arguments")]
    public void WindowsRecycleBinRejectsInvalidArguments()
    {
        var bin = WindowsRecycleBin.Instance;

        Assert.Throws<ArgumentNullException>(() => bin.SendToRecycleBin(null!));
        Assert.Throws<ArgumentException>(() => bin.SendToRecycleBin(""));
        Assert.Throws<ArgumentNullException>(() => bin.TryRestore(null!, 0, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => bin.TryRestore("", 0, DateTime.UtcNow));

        var fakePath = "C:\\nonexistent-folder-xyz\\nonexistent-file-123.jpg";
        var restored = bin.TryRestore(fakePath, 1234, DateTime.UtcNow);
        Assert.False(restored);
    }
}
