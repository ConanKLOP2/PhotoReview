using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.IO;
using PhotoReview.Core.Model;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// FEAT-RENAME PR2: <see cref="ImagePresenter.RetargetPath"/> re-keys what the presenter holds by path after a rename WITHOUT
/// presenting again (no sink image update, no stat, no decode) and re-arms zoom detail under the new name.
/// </summary>
public sealed partial class ImagePresenterTests
{
    private sealed class StatCountingFileSystem(IFileSystem inner) : IFileSystem
    {
        private int _stats;
        public int StatCount => Volatile.Read(ref _stats);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileStat? GetFileStat(string path)
        {
            Interlocked.Increment(ref _stats);
            return inner.GetFileStat(path);
        }
        public void Move(string source, string destination) => inner.Move(source, destination);
        public void Copy(string source, string destination) => inner.Copy(source, destination);
        public void Delete(string path) => inner.Delete(path);
        public Stream OpenReadShared(string path, int bufferSize = 65536) => inner.OpenReadShared(path, bufferSize);
        public Stream OpenAppend(string path, bool durable) => inner.OpenAppend(path, durable);
        public void WriteAllTextAtomic(string path, string text, bool durable = true) => inner.WriteAllTextAtomic(path, text, durable);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*") => inner.EnumerateFiles(directory, pattern);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
            inner.EnumerateFilesWithStat(directory, include, onSkipped);
        public bool TryProbeReadable(string path, out string? failure) => inner.TryProbeReadable(path, out failure);
        public IEnumerable<(string Path, FileStat? Stat)> EnumerateReadableFilesWithStat(string directory, Func<string, bool> include, Action<SkippedEntry> onSkipped) =>
            inner.EnumerateReadableFilesWithStat(directory, include, onSkipped);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
    }

    /// <summary>Bounded decodes return a downscaled preview; unbounded ones (zoom detail) a full-size image and are recorded.</summary>
    private sealed class RetargetDecoder : IImageDecoder
    {
        private readonly ConcurrentQueue<string> _originals = new();
        public ConcurrentQueue<string> OriginalPaths => _originals;
        public SemaphoreSlim? OriginalGate { get; set; }

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (!request.Box.IsUnbounded) return new DownscaledFakeDecodedImage(100, 100, 2000, 2000);
            _originals.Enqueue(request.Path);
            OriginalGate?.Wait();
            return new FakeDecodedImage { PixelWidth = 2000, PixelHeight = 2000 };
        }

        public ImageInfo ReadInfo(string path) => new(2000, 2000);
    }

    private (ImagePresenter Presenter, StatCountingFileSystem Fs, RetargetDecoder Decoder) CreateRetargetPresenter()
    {
        var decoder = new RetargetDecoder();
        var fs = new StatCountingFileSystem(new PhysicalFileSystem());
        var presenter = new ImagePresenter(
            _catalog, _clock, CreatePreviewService(decoder), _thumbnailCache, _preloadController, _compareViewModel,
            _hashService, _metrics, () => _settings, _sessionStore, _sink, fileSystem: fs, getSession: () => null);
        return (presenter, fs, decoder);
    }

    // The rename as the controller leaves it: file moved on disk (length/mtime kept) and the catalog entry re-keyed in place.
    private string RenameOnDiskAndCatalog(string path, string newName)
    {
        var to = Path.Combine(Path.GetDirectoryName(path)!, newName);
        File.Move(path, to);
        Assert.True(_catalog.RenameEntries(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = to }));
        return to;
    }

    [Fact]
    public async Task RetargetPath_ShownFileRenamed_RekeysPathInfoAndStatusWithoutPresentingStatOrDecode()
    {
        var a = CreateFakeImageFile("a.png");
        var c = CreateFakeImageFile("c.png");
        _catalog.Reset([a, c]);
        var (presenter, fs, decoder) = CreateRetargetPresenter();
        await presenter.PresentAsync(0);
        Assert.EndsWith("a.png", presenter.StatusText, StringComparison.OrdinalIgnoreCase);
        var image = presenter.CurrentImage;
        var images = _sink.Images.Count;
        var presented = _sink.PresentedPaths.Count;
        var stats = fs.StatCount;
        var notifies = _preloadController.NotifyNavigationCalls.Count;
        var decodes = decoder.OriginalPaths.Count;
        var navigation = _clock.CurrentNavigation;
        var b = RenameOnDiskAndCatalog(a, "b.png");

        var kept = presenter.RetargetPath(new Dictionary<string, string> { [a] = b });

        Assert.True(kept);
        Assert.Equal(b, presenter.CurrentPresentedPath);
        Assert.Equal("b.png", presenter.CurrentPhotoInfo?.FileName);
        Assert.EndsWith("b.png", presenter.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a.png", presenter.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Same(image, presenter.CurrentImage);
        Assert.Equal(images, _sink.Images.Count); // sink.SetCurrentImage NOT called
        Assert.Equal(presented, _sink.PresentedPaths.Count);
        Assert.Equal(stats, fs.StatCount); // no extra stat
        Assert.Equal(notifies, _preloadController.NotifyNavigationCalls.Count);
        Assert.Equal(decodes, decoder.OriginalPaths.Count);
        Assert.Equal(navigation, _clock.CurrentNavigation); // the presenter does not bump the generation itself
    }

    [Fact]
    public async Task RetargetPath_ThenPresentSameEntry_IsNotReportedAsAFileChange()
    {
        var a = CreateFakeImageFile("a.png");
        var c = CreateFakeImageFile("c.png");
        _catalog.Reset([a, c]);
        var (presenter, _, _) = CreateRetargetPresenter();
        await presenter.PresentAsync(0);
        var b = RenameOnDiskAndCatalog(a, "b.png");
        presenter.RetargetPath(new Dictionary<string, string> { [a] = b });
        _sink.FileChangeFlags.Clear();

        await presenter.PresentAsync(0);

        Assert.NotEmpty(_sink.FileChangeFlags);
        Assert.All(_sink.FileChangeFlags, flag => Assert.False(flag)); // same file: no cross-fade
        _sink.FileChangeFlags.Clear();
        await presenter.PresentAsync(1);
        Assert.Contains(true, _sink.FileChangeFlags); // control: a different file IS a file change
    }

    [Fact]
    public async Task RetargetPath_AfterStopForAction_ZoomDecodesTheOriginalViaTheNewPath()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, _, decoder) = CreateRetargetPresenter();
        using var gate = new SemaphoreSlim(0);
        decoder.OriginalGate = gate;
        await presenter.PresentAsync(0);
        _clock.StopForAction(); // what RenameAsync does before the I/O
        var b = RenameOnDiskAndCatalog(a, "b.png");

        Assert.True(presenter.RetargetPath(new Dictionary<string, string> { [a] = b }));
        presenter.SetViewerZoom(2.0);
        var load = presenter.ZoomDetail.PendingLoad;

        Assert.NotNull(load);
        gate.Release();
        await load!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([b], decoder.OriginalPaths);
        Assert.NotNull(presenter.ZoomDetail.HeldOriginal);
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
    }

    [Fact]
    public async Task RetargetPath_WithoutRetarget_StopForActionLeavesZoomDetailDead()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, _, decoder) = CreateRetargetPresenter();
        await presenter.PresentAsync(0);
        _clock.StopForAction();

        presenter.SetViewerZoom(2.0);

        Assert.Null(presenter.ZoomDetail.PendingLoad); // the premise the retarget fixes
        Assert.Empty(decoder.OriginalPaths);
    }

    [Fact]
    public async Task RetargetPath_EmptyMap_IsAPureReArmThatKeepsNamesAndPresentsNothing()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, fs, decoder) = CreateRetargetPresenter();
        using var gate = new SemaphoreSlim(0);
        decoder.OriginalGate = gate;
        await presenter.PresentAsync(0);
        var images = _sink.Images.Count;
        var stats = fs.StatCount;
        var status = presenter.StatusText;
        _clock.StopForAction();

        var kept = presenter.RetargetPath(new Dictionary<string, string>());
        presenter.SetViewerZoom(2.0);
        var load = presenter.ZoomDetail.PendingLoad;

        Assert.True(kept);
        Assert.Equal(a, presenter.CurrentPresentedPath);
        Assert.Equal(status, presenter.StatusText);
        Assert.Equal(images, _sink.Images.Count);
        Assert.Equal(stats, fs.StatCount);
        Assert.NotNull(load); // zoom detail is alive again under the same path
        gate.Release();
        await load!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([a], decoder.OriginalPaths);
    }

    [Fact]
    public async Task RetargetPath_HeldOriginalAndShownState_SurviveWithoutNewDecodeOrImageUpdate()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, _, decoder) = CreateRetargetPresenter();
        using var gate = new SemaphoreSlim(0);
        decoder.OriginalGate = gate;
        await presenter.PresentAsync(0);
        presenter.SetViewerZoom(2.0);
        var firstLoad = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(firstLoad);
        gate.Release();
        await firstLoad!.WaitAsync(TimeSpan.FromSeconds(10));
        var held = presenter.ZoomDetail.HeldOriginal;
        var shown = presenter.CurrentImage;
        var images = _sink.Images.Count;
        _clock.StopForAction();
        var b = RenameOnDiskAndCatalog(a, "b.png");

        Assert.True(presenter.RetargetPath(new Dictionary<string, string> { [a] = b }));

        Assert.NotNull(held);
        Assert.Same(held, presenter.ZoomDetail.HeldOriginal);
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
        Assert.Same(shown, presenter.CurrentImage);
        Assert.Equal(images, _sink.Images.Count);
        Assert.Single(decoder.OriginalPaths);
        Assert.Equal("b.png", presenter.CurrentPhotoInfo?.FileName);
    }

    [Fact]
    public async Task RetargetPath_OriginalDecodeInFlightForOldName_IsDroppedAndTheNewNameDecodes()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, _, decoder) = CreateRetargetPresenter();
        using var gate = new SemaphoreSlim(0);
        decoder.OriginalGate = gate;
        await presenter.PresentAsync(0);
        presenter.SetViewerZoom(2.0);
        var oldLoad = presenter.ZoomDetail.PendingLoad;
        Assert.NotNull(oldLoad);
        _clock.StopForAction();
        var b = RenameOnDiskAndCatalog(a, "b.png");

        Assert.True(presenter.RetargetPath(new Dictionary<string, string> { [a] = b }));
        gate.Release(2);
        await oldLoad!.WaitAsync(TimeSpan.FromSeconds(10));
        var newLoad = presenter.ZoomDetail.PendingLoad;
        if (newLoad is not null) await newLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([a, b], decoder.OriginalPaths);
        Assert.NotNull(presenter.ZoomDetail.HeldOriginal);
        Assert.True(presenter.ZoomDetail.IsShowingOriginal);
    }

    [Fact]
    public async Task RetargetPath_PresentInFlight_ReturnsFalseSoTheCallerPresentsAgain()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        using var decoder = new GatedDecoder(new FakeDecodedImage());
        var presenter = CreatePresenterWithServices(CreatePreviewService(decoder), _thumbnailCache);
        var present = presenter.PresentAsync(0);
        Assert.True(presenter.IsPresentInFlight);
        var images = _sink.Images.Count;

        var kept = presenter.RetargetPath(new Dictionary<string, string>());

        Assert.False(kept);
        Assert.Equal(images, _sink.Images.Count);
        decoder.Release();
        await present.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(presenter.IsPresentInFlight);
    }

    [Fact]
    public async Task IsPresentInFlight_AfterPresentCompleted_IsFalseAndRetargetKeeps()
    {
        var a = CreateFakeImageFile("a.png");
        _catalog.Reset([a]);
        var (presenter, _, _) = CreateRetargetPresenter();
        Assert.False(presenter.IsPresentInFlight);

        await presenter.PresentAsync(0);

        Assert.False(presenter.IsPresentInFlight);
        Assert.True(presenter.RetargetPath(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task RetargetPath_CompareVisible_ClearsCompareAndReturnsFalseButRekeysFileIdentity()
    {
        var f1 = CreateFakeImageFile("photo.jpg", 1000);
        var f2 = CreateFakeImageFile("photo (1).jpg", 2000);
        _catalog.Reset([f1, f2]);
        _settings.CompareSizeEnabled = true;
        _settings.CompareHashEnabled = false;
        var presenter = CreatePresenter();
        await presenter.PresentAsync(0);
        Assert.True(_compareViewModel.IsVisible); // precondition
        var renamed = Path.Combine(_tempDir, "renamed.jpg");
        File.Move(f1, renamed);
        Assert.True(_catalog.RenameEntries(new Dictionary<string, string> { [f1] = renamed }));

        var kept = presenter.RetargetPath(new Dictionary<string, string> { [f1] = renamed });

        Assert.False(kept);
        Assert.False(_compareViewModel.IsVisible);
        Assert.False(presenter.IsCompareVisible);
        Assert.Null(_compareViewModel.LeftPath);
    }

    [Fact]
    public void RetargetPath_NothingPresentedYet_IsANoOpThatKeeps()
    {
        var presenter = CreatePresenter();

        var kept = presenter.RetargetPath(new Dictionary<string, string> { ["x.png"] = "y.png" });

        Assert.True(kept);
        Assert.Null(presenter.CurrentPresentedPath);
        Assert.Empty(_sink.Images);
    }

    [Fact]
    public async Task RetargetPath_MapDoesNotListTheShownFile_KeepsShownNames()
    {
        var a = CreateFakeImageFile("a.png");
        var c = CreateFakeImageFile("c.png");
        _catalog.Reset([a, c]);
        var (presenter, _, _) = CreateRetargetPresenter();
        await presenter.PresentAsync(0);
        var status = presenter.StatusText;

        var kept = presenter.RetargetPath(new Dictionary<string, string> { [c] = Path.Combine(_tempDir, "d.png") });

        Assert.True(kept);
        Assert.Equal(a, presenter.CurrentPresentedPath);
        Assert.Equal("a.png", presenter.CurrentPhotoInfo?.FileName);
        Assert.Equal(status, presenter.StatusText);
    }

    [Fact]
    public void RetargetPath_NullMap_Throws()
    {
        var presenter = CreatePresenter();

        Assert.Throws<ArgumentNullException>(() => presenter.RetargetPath(null!));
    }
}
