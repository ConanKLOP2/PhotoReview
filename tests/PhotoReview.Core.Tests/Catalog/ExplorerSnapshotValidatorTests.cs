using System.IO;
using PhotoReview.Core.Catalog;
using Xunit;

namespace PhotoReview.Core.Tests.Catalog;

public sealed class ExplorerSnapshotValidatorTests
{
    private static readonly string Folder = OperatingSystem.IsWindows() ? @"C:\photos" : "/photos";
    private static readonly string FileA = Path.Combine(Folder, "a.jpg");
    private static readonly string FileB = Path.Combine(Folder, "b.jpg");

    private static ExplorerViewSnapshot CreateSnapshot(
        IReadOnlyList<string> ordered,
        ExplorerOrderStatus status = ExplorerOrderStatus.Available,
        string? reason = null) =>
        new(Folder, ordered, [], ExplorerGroupState.None, status, reason, DateTime.UtcNow);

    [Fact]
    public void TryValidateUnavailableStatusReturnsFalseWithReason()
    {
        var snapshot = CreateSnapshot([FileA], ExplorerOrderStatus.TimedOut, "Timeout occurred");

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA], out var ordered, out var reason);

        Assert.False(valid);
        Assert.Equal("Timeout occurred", reason);
        Assert.Empty(ordered);
    }

    [Fact]
    public void TryValidateValidSnapshotReturnsTrueAndOrderedList()
    {
        var snapshot = CreateSnapshot([FileB, FileA]);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA, FileB], out var ordered, out var reason);

        Assert.True(valid);
        Assert.Null(reason);
        Assert.Equal([FileB, FileA], ordered);
    }

    [Fact]
    public void TryValidateMissingExpectedFileReturnsFalse()
    {
        var snapshot = CreateSnapshot([FileA]);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA, FileB], out var ordered, out var reason);

        Assert.False(valid);
        Assert.NotNull(reason);
        Assert.Empty(ordered);
    }

    [Fact]
    public void TryValidateDuplicateItemReturnsFalse()
    {
        var snapshot = CreateSnapshot([FileA, FileA, FileB]);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA, FileB], out var ordered, out var reason);

        Assert.False(valid);
        Assert.Contains("duplicate", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ordered);
    }

    [Fact]
    public void TryValidatePathOutsideFolderReturnsFalse()
    {
        var outside = Path.Combine(Path.GetDirectoryName(Folder) ?? "C:\\", "other", "c.jpg");
        var snapshot = CreateSnapshot([FileA, outside]);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA], out var ordered, out var reason);

        Assert.False(valid);
        Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ordered);
    }

    [Fact]
    public void TryValidateEmptyCandidatePathReturnsFalse()
    {
        var snapshot = CreateSnapshot(["", FileA]);

        var valid = ExplorerSnapshotValidator.TryValidate(snapshot, [FileA], out var ordered, out var reason);

        Assert.False(valid);
        Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ordered);
    }

    [Fact]
    public void CanonicalizeFolderAndSamePathWorkCorrectly()
    {
        var canonical = ExplorerSnapshotValidator.CanonicalizeFolder(Folder);
        Assert.False(canonical.EndsWith(Path.DirectorySeparatorChar));

        Assert.True(ExplorerSnapshotValidator.SamePath(Folder, Folder + Path.DirectorySeparatorChar));
    }
}
