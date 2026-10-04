using System.Collections.Generic;
using PhotoReview.App.Coordinators;
using PhotoReview.App.Tests.ViewModels;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): entries the scan itself skipped are reported, and a failure of a superseded load stays silent.</summary>
public sealed partial class FolderLoadCoordinatorTests
{
    /// <summary>The listing hands out the real files but also reports one entry it had to skip (as an interrupted directory read does).</summary>
    private sealed class SkippingScanFileSystem : MainViewModelFileActionTests.DelegatingFileSystem, IFileSystem
    {
        private readonly IFileSystem _wrapped;
        private readonly SkippedEntry _skippedEntry;

        public SkippingScanFileSystem(IFileSystem wrapped, SkippedEntry skippedEntry)
            : base(wrapped)
        {
            _wrapped = wrapped;
            _skippedEntry = skippedEntry;
        }

        public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped)
        {
            onSkipped(_skippedEntry);
            return _wrapped.EnumerateFilesWithStat(directory, include, onSkipped);
        }
    }

    [Fact]
    public async Task LoadAsync_ScanSkippedAnEntry_StillLoadsTheImagesAndReportsTheSkippedEntryOnce()
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img");
        var skipped = new SkippedEntry(@"C:\photos\locked.jpg", "access denied");
        using var coordinator = new FolderLoadCoordinator(
            _catalog, _genClock, _explorerOrder, new SkippingScanFileSystem(_fs, skipped), _sessionStore, _settingsStore, _sink);

        await coordinator.LoadAsync(folder);
        await coordinator.ReadabilityProbe;

        Assert.Equal([@"C:\photos\a.jpg"], _catalog.Paths);
        var call = Assert.Single(_sink.SkippedCalls);
        Assert.Equal(folder, call.Folder);
        Assert.Equal([skipped], call.Skipped);
        Assert.Empty(_sink.Failures);
    }

    [Fact]
    public async Task LoadAsync_SinkThrowsAfterTheFolderGenerationMovedOn_IsSwallowedNotReportedAsAFailure()
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img");
        _sink.OnCatalogReadyHook = _ =>
        {
            _genClock.NextFolder(); // another folder was opened meanwhile (the load token itself is not cancelled)
            throw new InvalidOperationException("sink failed");
        };
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(folder);

        Assert.Empty(_sink.Failures);
    }

    [Fact]
    public async Task LoadAsync_SinkThrowsWhileTheLoadIsStillCurrent_IsReportedAsAFailure()
    {
        const string folder = @"C:\photos";
        _fs.CreateDirectory(folder);
        _fs.WriteAllTextAtomic(@"C:\photos\a.jpg", "img");
        _sink.OnCatalogReadyHook = _ => throw new InvalidOperationException("sink failed");
        using var coordinator = CreateCoordinator();

        await coordinator.LoadAsync(folder);

        var failure = Assert.Single(_sink.Failures);
        Assert.Equal(folder, failure.Folder);
    }
}
