using System.IO;
using PhotoReview.Core.Abstractions;
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

    [Fact(DisplayName = "Explorer provider contract is fakeable without COM")]
    public async Task ProviderContractIsFakeableWithoutCom()
    {
        IExplorerOrderProvider provider = new FakeExplorerOrderProvider(CreateSnapshot([FileB, FileA]));

        var result = await provider.TryGetSnapshotAsync(Folder, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(FileB, result.OrderedPaths[0]);
    }

    private sealed class FakeExplorerOrderProvider(ExplorerViewSnapshot snapshot) : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(snapshot);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
            IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16, CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public void Dispose() { }
    }
}
