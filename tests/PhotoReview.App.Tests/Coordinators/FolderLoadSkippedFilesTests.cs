using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Session;
using PhotoReview.Core.Settings;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// IO05 (ADR 0007 section 3): unreadable files are skipped, counted and reported through
/// <see cref="IFolderLoadSink.OnFilesSkipped"/>; a normal folder reports nothing. Uses the real file
/// system in a temp directory (locks and ACLs cannot be faked).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class FolderLoadSkippedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoReviewIo05-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly ReviewCatalog _catalog = new();
    private readonly RecordingSink _sink = new();
    private readonly PhysicalFileSystem _fs = new();

    public FolderLoadSkippedFilesTests()
    {
        _folder = Path.Combine(_root, "photos");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    private sealed class RecordingSink : IFolderLoadSink
    {
        public List<(string Folder, IReadOnlyList<SkippedEntry> Skipped)> SkippedCalls { get; } = [];
        public int CatalogReadyCount { get; private set; }
        public int CatalogReadyCountAtSkipped { get; private set; } = -1;
        public List<Exception> Failures { get; } = [];
        public void ResetCaches() { }
        public void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session) => CatalogReadyCount++;
        public Task PresentAsync(int index, long presentationGeneration) => Task.CompletedTask;
        public void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session) { }
        public void OnOrderApplied(int count, int currentIndex, bool currentKept) { }
        public void OnFilesSkipped(string folder, IReadOnlyList<SkippedEntry> skipped)
        {
            CatalogReadyCountAtSkipped = CatalogReadyCount;
            SkippedCalls.Add((folder, skipped));
        }
        public void OnFailed(string folder, Exception exception) => Failures.Add(exception);
        public Task OnUnreadableRemovedAsync(IReadOnlyList<string> removedPaths, bool currentRemoved) => Task.CompletedTask;
    }

    private sealed class NoExplorerOrder : IExplorerOrderProvider
    {
        public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(string folder, TimeSpan timeout, CancellationToken cancellationToken) =>
            TryGetSnapshotProgressiveAsync(folder, timeout, cancellationToken: cancellationToken);

        public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
            string folder, TimeSpan timeout, IProgress<ExplorerQueryProgress>? progress = null,
            int progressiveBatchSize = 16, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerViewSnapshot(
                folder, [], [], ExplorerGroupState.None, ExplorerOrderStatus.NativeViewUnavailable, null, DateTime.UtcNow));

        public void Dispose() { }
    }

    private async Task LoadAsync()
    {
        var paths = new AppPaths(Path.Combine(_root, "appdata"));
        using var coordinator = new FolderLoadCoordinator(
            _catalog, new GenerationClock(), new NoExplorerOrder(), _fs,
            new SessionStore(paths, _fs), new SettingsStore(paths, _fs, NullLog.Instance), _sink);
        await coordinator.LoadAsync(_folder);
        // AR16: unreadable files are found by the background probe after the first frame; LoadAsync completes
        // when the Explorer order is settled, so the report is awaited separately.
        await coordinator.ReadabilityProbe;
    }

    private string Touch(string name, string? dir = null)
    {
        var path = Path.Combine(dir ?? _folder, name);
        File.WriteAllText(path, "img");
        return path;
    }

    [Fact]
    public async Task NormalFolder_ReportsNoSkippedFiles()
    {
        Touch("a.jpg");
        Touch("b.png");
        Touch("notes.txt");

        await LoadAsync();

        Assert.Equal(2, _catalog.Count);
        Assert.Empty(_sink.SkippedCalls);
        Assert.Empty(_sink.Failures);
    }

    [Fact]
    public async Task LockedFile_IsSkippedAndReported()
    {
        Touch("a.jpg");
        var locked = Touch("locked.jpg");
        Touch("c.jpg");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await LoadAsync();

        AssertOnlySkipped(locked, expectedCatalogCount: 2);
    }

    [Fact]
    public async Task AclDeniedFile_IsSkippedAndReported()
    {
        Touch("a.jpg");
        var denied = Touch("denied.jpg");
        Touch("c.jpg");
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
        try
        {
            var info = new FileInfo(denied);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException or InvalidOperationException)
        {
            return; // ACL manipulation not permitted here: skip gracefully
        }

        try
        {
            await LoadAsync();

            AssertOnlySkipped(denied, expectedCatalogCount: 2);
        }
        finally
        {
            var info = new FileInfo(denied);
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task UnreadableSubdirectory_DoesNotAffectFolderScanOrWarn()
    {
        Touch("a.jpg");
        var sub = Path.Combine(_folder, "private");
        Directory.CreateDirectory(sub);
        Touch("inner.jpg", sub);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var subInfo = new DirectoryInfo(sub);
        try
        {
            var acl = subInfo.GetAccessControl();
            acl.AddAccessRule(rule);
            subInfo.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException or InvalidOperationException)
        {
            return;
        }

        try
        {
            await LoadAsync();

            // Sub-folders are not part of the catalog (non-recursive scan): nothing is missing, so no warning.
            Assert.Equal(1, _catalog.Count);
            Assert.Empty(_sink.SkippedCalls);
            Assert.Empty(_sink.Failures);
        }
        finally
        {
            var acl = subInfo.GetAccessControl();
            acl.RemoveAccessRule(rule);
            subInfo.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task UnreadableFolderItself_FailsLoudlyInsteadOfShowingEmptyCatalog()
    {
        Touch("a.jpg");
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(_folder);
        try
        {
            var acl = info.GetAccessControl();
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException or InvalidOperationException)
        {
            return;
        }

        try
        {
            await LoadAsync();

            Assert.Single(_sink.Failures);
            Assert.Empty(_sink.SkippedCalls);
        }
        finally
        {
            var acl = info.GetAccessControl();
            acl.RemoveAccessRule(rule);
            info.SetAccessControl(acl);
        }
    }

    private void AssertOnlySkipped(string expectedPath, int expectedCatalogCount)
    {
        Assert.Equal(expectedCatalogCount, _catalog.Count);
        Assert.DoesNotContain(_catalog.Entries, e => string.Equals(e.Path, expectedPath, StringComparison.OrdinalIgnoreCase));
        var call = Assert.Single(_sink.SkippedCalls);
        var skipped = Assert.Single(call.Skipped);
        Assert.Equal(expectedPath, skipped.Path, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));
        Assert.Equal(1, _sink.CatalogReadyCountAtSkipped); // reported for this load (after its only OnCatalogReady)
        Assert.Empty(_sink.Failures);
    }
}
