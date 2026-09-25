using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Settings;
using PhotoReview.TestSupport;
using Xunit;

namespace PhotoReview.App.Tests.ViewModels;

/// <summary>ZoomActualSize (ADR 0008) on <see cref="ViewerState"/> and the info overlay state (<see cref="InfoOverlayViewModel"/>).</summary>
[Trait("Category", "HotPath")]
public sealed class ViewerQuickFeaturesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Separator = "     ";

    // --- ZoomActualSize ---

    [Fact]
    public void ZoomToActualSize_FromFit_SetsOneSourcePixelPerDevicePixel()
    {
        var state = new ViewerState { DpiScale = 1.5 };
        state.SetSourceSize(6000, 4000);
        state.ResetFit(1000, 800); // Fit zoom = min(1000*1.5/6000, 800*1.5/4000) = 0.25
        var modeChanges = 0;
        state.ZoomModeChanged += (_, _) => modeChanges++;

        state.ZoomToActualSize();

        Assert.False(state.IsFit);
        Assert.Equal(ViewerStretchMode.None, state.Stretch);
        Assert.Equal(1.0, state.Zoom);
        Assert.Equal(1.0, state.EffectiveZoom); // the presenter decodes the original for any non-null zoom
        Assert.Equal(6000 / 1.5, state.ImageWidth, 6);
        Assert.Equal(4000 / 1.5, state.ImageHeight, 6);
        Assert.Equal(1, modeChanges);
    }

    [Fact]
    public void ZoomToActualSize_FromAnotherZoom_ReturnsTo100Percent()
    {
        var state = new ViewerState();
        state.SetSourceSize(800, 600);
        state.SetZoom(2.5);

        state.ZoomToActualSize();

        Assert.Equal(1.0, state.Zoom);
        Assert.Equal(800, state.ImageWidth, 6);
    }

    // --- Info overlay visibility ---

    private sealed class Finder
    {
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentDictionary<string, CancellationToken> Tokens { get; } = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);

        /// <summary>Completes when the search for <paramref name="folder"/> is running on the pool (a real signal, no polling).</summary>
        public Task Started(string folder) => _started.GetOrAdd(folder, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public Dictionary<string, SiblingImageFolders> Results { get; } = new(StringComparer.Ordinal);

        public ManualResetEventSlim Block(string folder) => _gates.GetOrAdd(folder, _ => new ManualResetEventSlim(false));

        public SiblingImageFolders Find(string folder, CancellationToken cancellationToken)
        {
            Calls.Enqueue(folder);
            Tokens[folder] = cancellationToken;
            _started.GetOrAdd(folder, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            // Simulates a slow listing that does not observe cancellation, so a late result really arrives.
            if (_gates.TryGetValue(folder, out var gate) && !gate.Wait(Timeout, CancellationToken.None)) throw new TimeoutException("gate " + folder);
            return Results.TryGetValue(folder, out var result) ? result : default;
        }
    }

    private static string Name(string path) => Path.GetFileName(path);

    private static string Expected(string? previous, string current, string? next, string previousKey = "PageUp", string nextKey = "PageDown")
    {
        var parts = new List<string>();
        if (previous is not null) parts.Add(Tr.MainFolderInfoPrevious(previousKey, Name(previous)));
        parts.Add(Tr.MainFolderInfoCurrent(Name(current)));
        if (next is not null) parts.Add(Tr.MainFolderInfoNext(nextKey, Name(next)));
        return string.Join(Separator, parts);
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, false, false)] // master switch hides both
    [InlineData(true, false, true, false, true)]
    [InlineData(true, true, false, true, false)]
    public void Visibility_FollowsMasterSwitchAndPerBlockSwitches(bool overlay, bool file, bool folder, bool fileVisible, bool folderVisible)
    {
        var settings = new AppSettings { ShowInfoOverlay = overlay, ShowFileInfo = file, ShowFolderInfo = folder };
        var finder = new Finder();
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);
        vm.SetFolder(@"C:\photos\b");

        Assert.Equal(fileVisible, vm.IsFileInfoVisible);
        Assert.Equal(folderVisible, vm.IsFolderInfoVisible);
    }

    [Fact]
    public void FolderInfo_WithoutFolder_IsHidden()
    {
        var finder = new Finder();
        var vm = new InfoOverlayViewModel(() => new AppSettings(), finder.Find);

        vm.SetFolder(null);

        Assert.False(vm.IsFolderInfoVisible);
        Assert.Equal("", vm.FolderInfoText);
        Assert.Empty(finder.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FolderInfo_WhenHidden_NeverSearchesSiblings_UntilShown(bool showInfoOverlay, bool showFolderInfo)
    {
        var settings = new AppSettings { ShowInfoOverlay = showInfoOverlay, ShowFolderInfo = showFolderInfo };
        var finder = new Finder();
        var folder = @"C:\photos\b";
        finder.Results[folder] = new SiblingImageFolders(@"C:\photos\a", @"C:\photos\c");
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);

        vm.SetFolder(folder);
        await vm.PendingSiblings.WithTimeout(Timeout, "hidden overlay");

        Assert.Empty(finder.Calls);
        Assert.Equal("", vm.FolderInfoText);

        settings.ShowInfoOverlay = true;
        settings.ShowFolderInfo = true;
        vm.Refresh();
        await vm.PendingSiblings.WithTimeout(Timeout, "sibling search after showing");

        Assert.Equal(new[] { folder }, finder.Calls);
        Assert.Equal(Expected(@"C:\photos\a", folder, @"C:\photos\c"), vm.FolderInfoText);
    }

    [Fact]
    public async Task FolderInfo_ShowsPlaceholderWhileSearching_ThenSiblingsWithConfiguredKeys()
    {
        var settings = new AppSettings();
        settings.Shortcuts.PreviousFolder = "F9";
        var finder = new Finder();
        var folder = @"C:\photos\2024-05-02";
        finder.Results[folder] = new SiblingImageFolders(@"C:\photos\2024-05-01", @"C:\photos\2024-05-03");
        var gate = finder.Block(folder);
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);

        vm.SetFolder(folder);

        Assert.Equal(Tr.MainFolderInfoCurrent("2024-05-02") + Separator + Tr.MainFolderInfoPending, vm.FolderInfoText);
        gate.Set();
        await vm.PendingSiblings.WithTimeout(Timeout, "sibling search");
        Assert.Equal(Expected(@"C:\photos\2024-05-01", folder, @"C:\photos\2024-05-03", previousKey: "F9"), vm.FolderInfoText);
    }

    [Fact]
    public async Task FolderInfo_OmitsSideWithoutSiblingOrWithEmptyShortcut()
    {
        var settings = new AppSettings();
        var finder = new Finder();
        var folder = @"C:\photos\b";
        finder.Results[folder] = new SiblingImageFolders(null, @"C:\photos\c");
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);

        vm.SetFolder(folder);
        await vm.PendingSiblings.WithTimeout(Timeout, "sibling search");
        Assert.Equal(Expected(null, folder, @"C:\photos\c"), vm.FolderInfoText);

        settings.Shortcuts.NextFolder = "";
        vm.Refresh(); // re-render only: same folder, no new search
        Assert.Equal(Expected(null, folder, null), vm.FolderInfoText);
        Assert.Single(finder.Calls);
    }

    [Fact]
    public async Task FolderInfo_ResultForAFolderAlreadyLeft_IsIgnored()
    {
        var settings = new AppSettings();
        var finder = new Finder();
        const string first = @"C:\photos\a";
        const string second = @"C:\photos\b";
        finder.Results[first] = new SiblingImageFolders(@"C:\photos\old-prev", @"C:\photos\old-next");
        finder.Results[second] = new SiblingImageFolders(@"C:\photos\a", @"C:\photos\c");
        var firstGate = finder.Block(first);
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);

        vm.SetFolder(first);
        var firstSearch = vm.PendingSiblings;
        await finder.Started(first).WithTimeout(Timeout, "first search started");
        vm.SetFolder(second);
        await vm.PendingSiblings.WithTimeout(Timeout, "second search");
        Assert.Equal(Expected(@"C:\photos\a", second, @"C:\photos\c"), vm.FolderInfoText);
        Assert.True(finder.Tokens[first].IsCancellationRequested);

        firstGate.Set(); // the stale search now completes with the first folder's siblings
        await firstSearch.WithTimeout(Timeout, "stale search");

        Assert.Equal(Expected(@"C:\photos\a", second, @"C:\photos\c"), vm.FolderInfoText);
    }

    [Fact]
    public async Task FolderInfo_HidingWhileSearching_CancelsAndDropsTheResult()
    {
        var settings = new AppSettings();
        var finder = new Finder();
        const string folder = @"C:\photos\b";
        finder.Results[folder] = new SiblingImageFolders(@"C:\photos\a", null);
        var gate = finder.Block(folder);
        var vm = new InfoOverlayViewModel(() => settings, finder.Find);

        vm.SetFolder(folder);
        var search = vm.PendingSiblings;
        await finder.Started(folder).WithTimeout(Timeout, "search started");
        settings.ShowInfoOverlay = false;
        vm.Refresh();
        gate.Set();
        await search.WithTimeout(Timeout, "cancelled search");

        Assert.True(finder.Tokens[folder].IsCancellationRequested);
        Assert.Equal("", vm.FolderInfoText);
        Assert.False(vm.IsFolderInfoVisible);
    }

    // --- The real sibling search (same one PageUp/PageDown use), on temp folders ---

    [Fact]
    public void FindSiblingImageFolders_SkipsFoldersWithoutImages_BothDirections()
    {
        using var root = new TempRoot("sibling-info");
        root.File(@"parent\a\1.jpg", 1);
        root.File(@"parent\b\1.txt", 1); // no image: skipped
        var current = root.File(@"parent\c\1.png", 1);
        root.Dir(@"parent\d");            // empty: skipped
        root.File(@"parent\e\1.jpeg", 1);
        var navigator = new SiblingFolderNavigator(
            new PhotoReview.Core.Catalog.GenerationClock(),
            new PhotoReview.Core.Catalog.ReviewCatalog(),
            new PhotoReview.Core.IO.PhysicalFileSystem(),
            new NullSink(),
            () => null);

        var result = navigator.FindSiblingImageFolders(Path.GetDirectoryName(current)!, CancellationToken.None);

        Assert.Equal(root.Combine("parent", "a"), result.Previous);
        Assert.Equal(root.Combine("parent", "e"), result.Next);
    }

    [Fact]
    public void FindSiblingImageFolders_CancelledToken_Throws()
    {
        using var root = new TempRoot("sibling-info-cancel");
        root.File(@"parent\a\1.jpg", 1);
        var navigator = new SiblingFolderNavigator(
            new PhotoReview.Core.Catalog.GenerationClock(),
            new PhotoReview.Core.Catalog.ReviewCatalog(),
            new PhotoReview.Core.IO.PhysicalFileSystem(),
            new NullSink(),
            () => null);

        Assert.Throws<OperationCanceledException>(() =>
            navigator.FindSiblingImageFolders(root.Combine("parent", "a"), new CancellationToken(canceled: true)));
    }

    private sealed class NullSink : ISiblingNavigatorSink
    {
        public void SetStatusText(string status) { }
        public Task OpenFolderAsync(string folder, string? initialPath = null) => Task.CompletedTask;
        public void NotifyNavigationStateChanged() { }
    }
}
