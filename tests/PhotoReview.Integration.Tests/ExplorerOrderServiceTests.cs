using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Integration")]
public sealed class ExplorerOrderServiceTests
{
    private sealed class MemoryLog : ILog
    {
        public bool Enabled => true;
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add($"INFO: {message}");
        public void Warn(string message) => Messages.Add($"WARN: {message}");
        public void Error(string message, Exception? ex = null) => Messages.Add($"ERROR: {message} {ex?.Message}");
    }

    [Fact(DisplayName = "TryGetSnapshotAsync for non-matching folder returns NoMatchingWindow status gracefully")]
    public async Task NonMatchingFolderReturnsNoMatchingWindowGracefully()
    {
        var log = new MemoryLog();
        using var service = new ExplorerOrderService(log);
        var tempFolder = Path.Combine(Path.GetTempPath(), "photo-review-explorer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        try
        {
            var snapshot = await service.TryGetSnapshotAsync(tempFolder, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(snapshot);
            Assert.True(snapshot.Status is ExplorerOrderStatus.NoMatchingWindow or ExplorerOrderStatus.NativeViewUnavailable);
            Assert.Empty(snapshot.OrderedPaths);
            Assert.Contains(log.Messages, m => m.Contains("Explorer query-start"));
        }
        finally
        {
            try { Directory.Delete(tempFolder, true); } catch { }
        }
    }

    [Fact(DisplayName = "TryGetSnapshotAsync with already-cancelled token returns Canceled status immediately")]
    public async Task CancelledTokenReturnsCanceledStatusImmediately()
    {
        using var service = new ExplorerOrderService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var snapshot = await service.TryGetSnapshotAsync(Path.GetTempPath(), TimeSpan.FromSeconds(5), cts.Token);
        Assert.NotNull(snapshot);
        Assert.Equal(ExplorerOrderStatus.Canceled, snapshot.Status);
    }

    [Fact(DisplayName = "TryGetSnapshotProgressiveAsync yields progress without crashing")]
    public async Task ProgressiveSnapshotRunsSafely()
    {
        using var service = new ExplorerOrderService();
        var progressList = new List<ExplorerQueryProgress>();
        var progress = new Progress<ExplorerQueryProgress>(p => progressList.Add(p));

        var snapshot = await service.TryGetSnapshotProgressiveAsync(
            Path.GetTempPath(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            progress,
            batchSize: 8);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Status is ExplorerOrderStatus.NoMatchingWindow or ExplorerOrderStatus.NativeViewUnavailable or ExplorerOrderStatus.Available);
    }
}
