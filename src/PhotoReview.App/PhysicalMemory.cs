using PhotoReview.Core.Settings;
using PhotoReview.Core.Abstractions;
using PhotoReview.Platform.Windows;

namespace PhotoReview.App;

internal sealed class PhysicalMemory : IMemoryProbe
{
    public static readonly PhysicalMemory Instance = new();
    private readonly WindowsMemoryProbe _probe = WindowsMemoryProbe.Instance;

    public bool HasHeadroom(double maximumLoad, long reserveBytes) => _probe.HasHeadroom(maximumLoad, reserveBytes);

    public MemorySnapshot? GetSnapshot() => _probe.GetSnapshot();

    /// <param name="maximumLoad">A 0-1 fraction of physical memory load, not a percentage.</param>
    internal static bool HasHeadroom(double maximumLoad) => Instance.HasHeadroom(maximumLoad, PerformanceOptions.MemoryReserveBytes);
}
