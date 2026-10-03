using PhotoReview.Core.Abstractions;

namespace PhotoReview.TestSupport;

/// <summary>Fixed-answer <see cref="IMemoryProbe"/> so preload tests do not depend on the RAM the test machine has free.</summary>
public sealed class FakeMemoryProbe : IMemoryProbe
{
    private readonly bool _hasHeadroom;
    public FakeMemoryProbe(bool hasHeadroom = true) => _hasHeadroom = hasHeadroom;
    public bool HasHeadroom(double maximumLoad, long reserveBytes) => _hasHeadroom;
    public MemorySnapshot? GetSnapshot() => new(50, 16L * 1024 * 1024 * 1024);
}