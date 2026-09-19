using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;

namespace PhotoReview.Core.Tests.Services;

/// <summary>Explorer snapshot validation and provider fakeability.</summary>
public sealed class ExplorerSnapshotValidatorTests : IDisposable
{
    private readonly TempRoot _root = new("explorer-order");
    private readonly string _folder;
    private readonly string _a;
    private readonly string _b;
    private readonly ExplorerViewSnapshot _snapshot;

    public ExplorerSnapshotValidatorTests()
    {
        _folder = _root.Dir("explorer-order");
        _a = Path.Combine(_folder, "a.jpg");
        _b = Path.Combine(_folder, "b.jpg");
        _snapshot = new ExplorerViewSnapshot(_folder, [_b, _a], [], ExplorerGroupState.None,
            ExplorerOrderStatus.Available, null, DateTime.UtcNow);
    }

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "Explorer snapshot accepts a complete native order")]
    public void ExplorerSnapshotAcceptsCompleteNativeOrder() =>
        Assert.True(ExplorerSnapshotValidator.TryValidate(_snapshot, [_a, _b], out var nativeOrder, out _)
            && nativeOrder.SequenceEqual(new[] { _b, _a }, StringComparer.OrdinalIgnoreCase));

    [Fact(DisplayName = "Explorer snapshot rejects missing images")]
    public void ExplorerSnapshotRejectsMissingImages() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(_snapshot with { OrderedPaths = [_a] },
            [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot rejects duplicate paths")]
    public void ExplorerSnapshotRejectsDuplicatePaths() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(_snapshot with { OrderedPaths = [_a, _a, _b] },
            [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot rejects paths outside the folder")]
    public void ExplorerSnapshotRejectsPathsOutsideFolder() =>
        Assert.False(ExplorerSnapshotValidator.TryValidate(
            _snapshot with { OrderedPaths = [_a, _root.Combine("outside.jpg")] }, [_a, _b], out _, out _));

    [Fact(DisplayName = "Explorer snapshot exposes provider fallback reason")]
    public void ExplorerSnapshotExposesProviderFallbackReason() =>
        Assert.True(!ExplorerSnapshotValidator.TryValidate(
                _snapshot with { Status = ExplorerOrderStatus.TimedOut, Reason = "timeout" },
                [_a, _b], out _, out var unavailableReason)
            && unavailableReason == "timeout");

    [Fact(DisplayName = "Explorer provider contract is fakeable without COM")]
    public async Task ExplorerProviderContractIsFakeableWithoutCom()
    {
        IExplorerOrderProvider provider = new FakeExplorerOrderProvider(_snapshot);
        var result = await provider.TryGetSnapshotAsync(_folder, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(_b, result.OrderedPaths[0]);
    }

    private sealed class FakeExplorerOrderProvider(ExplorerViewSnapshot snapshot) : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(snapshot);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(string folder, TimeSpan timeout,
            CancellationToken cancellationToken, IProgress<ExplorerQueryProgress>? progress = null, int batchSize = 16)
            => Task.FromResult(snapshot);

        public void Dispose() { }
    }
}

