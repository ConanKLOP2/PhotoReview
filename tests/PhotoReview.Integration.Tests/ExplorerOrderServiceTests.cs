using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Platform.Windows.Explorer;

namespace PhotoReview.Integration.Tests;

[Trait("Category", "Native")]
public sealed class ExplorerOrderServiceTests
{
    private sealed class MemoryLog : ILog
    {
        private readonly object _gate = new();
        private readonly List<string> _messages = [];
        public bool Enabled => true;
        public List<string> Messages { get { lock (_gate) return [.. _messages]; } }

        public void Info(string message) { lock (_gate) _messages.Add($"INFO: {message}"); }
        public void Warn(string message) { lock (_gate) _messages.Add($"WARN: {message}"); }
        public void Error(string message, Exception? ex = null) { lock (_gate) _messages.Add($"ERROR: {message} {ex?.Message}"); }
    }

    private static string CreateTempFolder(string tag)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"photo-review-explorer-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact(DisplayName = "perf(startup): a folder load joins the startup prefetch instead of querying Explorer again")]
    public async Task PrefetchForSameFolderIsJoined()
    {
        var log = new MemoryLog();
        using var service = new ExplorerOrderService(log);
        var folder = CreateTempFolder("prefetch");
        try
        {
            service.Prefetch(folder, TimeSpan.FromSeconds(5));
            var joined = await service.TryGetSnapshotProgressiveAsync(folder + Path.DirectorySeparatorChar, TimeSpan.FromSeconds(5));
            Assert.Equal(ExplorerSnapshotValidator.CanonicalizeFolder(folder), joined.Folder, ignoreCase: true);
            Assert.Single(log.Messages, m => m.Contains("Explorer query-start", StringComparison.Ordinal));

            // One-shot: the next request queries Explorer afresh.
            await service.TryGetSnapshotProgressiveAsync(folder, TimeSpan.FromSeconds(5));
            Assert.Equal(2, log.Messages.Count(m => m.Contains("Explorer query-start", StringComparison.Ordinal)));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact(DisplayName = "perf(startup): a request for another folder does not get the prefetched snapshot")]
    public async Task PrefetchForOtherFolderIsNotUsed()
    {
        using var service = new ExplorerOrderService(new MemoryLog());
        var prefetched = CreateTempFolder("prefetch-a");
        var requested = CreateTempFolder("prefetch-b");
        try
        {
            service.Prefetch(prefetched, TimeSpan.FromSeconds(5));
            var snapshot = await service.TryGetSnapshotProgressiveAsync(requested, TimeSpan.FromSeconds(5));
            Assert.Equal(ExplorerSnapshotValidator.CanonicalizeFolder(requested), snapshot.Folder, ignoreCase: true);
            Assert.NotEqual(ExplorerOrderStatus.TimedOut, snapshot.Status);
        }
        finally
        {
            try { Directory.Delete(prefetched, true); } catch { }
            try { Directory.Delete(requested, true); } catch { }
        }
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
            progress,
            batchSize: 8,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Status is ExplorerOrderStatus.NoMatchingWindow or ExplorerOrderStatus.NativeViewUnavailable or ExplorerOrderStatus.Available);
    }
}
