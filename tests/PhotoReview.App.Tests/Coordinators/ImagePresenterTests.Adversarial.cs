using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// Adversarial review of <see cref="ImagePresenter"/>: stale results after rapid navigation, out-of-range
/// indexes, files that vanish or fail while presenting and a compare partner that is missing.
/// No fixed delays: every wait is a gate the test opens or a bounded <c>WaitAsync</c> guard.
/// </summary>
public sealed partial class ImagePresenterTests
{
    [Theory(DisplayName = "Out-of-range and empty-catalog presents are no-ops that touch nothing")]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task PresentAsync_IndexOutOfRange_IsANoOp(int index)
    {
        var presenter = CreatePresenter();

        await presenter.PresentAsync(index); // empty catalog

        Assert.Null(presenter.CurrentImage);
        Assert.Empty(_sink.Images);
        Assert.Empty(_sink.Statuses);
        Assert.Empty(_preloadController.NotifyNavigationCalls);
        Assert.Equal(0, _clock.CurrentNavigation);
    }

    [Fact(DisplayName = "Present of an index past the end of a one-image catalog leaves the shown image alone")]
    public async Task PresentAsync_SingleImage_IndexPastEnd_KeepsShownImage()
    {
        var f1 = CreateFakeImageFile("only.png");
        _catalog.Reset([f1]);
        var presenter = CreatePresenter();
        await presenter.PresentAsync(0);
        var shown = presenter.CurrentImage;
        var navigation = _clock.CurrentNavigation;

        await presenter.PresentAsync(1);
        await presenter.PresentAsync(-1);

        Assert.NotNull(shown);
        Assert.Same(shown, presenter.CurrentImage);
        Assert.Equal(navigation, _clock.CurrentNavigation);
        Assert.Equal(0, _catalog.CurrentIndex);
    }

    [Fact(DisplayName = "Re-presenting the same (last) image again and again, as a held Right key does, stays consistent")]
    public async Task PresentAsync_RepeatedOnSameIndex_StaysConsistent()
    {
        var f1 = CreateFakeImageFile("a.png");
        var f2 = CreateFakeImageFile("b.png");
        _catalog.Reset([f1, f2]);
        var presenter = CreatePresenter();

        for (var i = 0; i < 25; i++) await presenter.PresentAsync(1);

        Assert.Equal(1, _catalog.CurrentIndex);
        Assert.Equal(2, _catalog.Count);
        Assert.NotNull(presenter.CurrentImage);
        Assert.Equal(Enumerable.Repeat(f2, 25), _sink.PresentedPaths);
        Assert.DoesNotContain(_sink.Statuses, s => s.Contains("Lỗi", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "INV-1: the superseded navigation neither shows an image nor reports it presented")]
    public async Task PresentAsync_TokenSuperseded_ShowsNothingFromTheStaleNavigation()
    {
        var f1 = CreateFakeImageFile("stale.png");
        _catalog.Reset([f1]);
        var presenter = CreatePresenter();

        var stale = presenter.PresentAsync(0);
        _clock.NextNavigation(); // a newer navigation took over before the decode came back
        await stale.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(_sink.PresentedPaths);
        Assert.Null(presenter.CurrentImage);
        Assert.DoesNotContain(_sink.Statuses, s => s.Contains("Lỗi", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Rapid navigation burst: whatever order the decodes finish in, only the last navigation is shown")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task PresentAsync_BurstCompletingInRandomOrder_ShowsOnlyTheLastNavigation(int seed)
    {
        const int count = 8;
        var paths = Enumerable.Range(0, count).Select(i => CreateFakeImageFile($"burst{i}.png")).ToArray();
        _catalog.Reset(paths);
        var decoder = new PerPathGatedDecoder();
        var previewService = CreatePreviewService(decoder);
        var presenter = CreatePresenterWithServices(previewService, _thumbnailCache);

        var presents = new List<Task>();
        for (var i = 0; i < count; i++)
        {
            presents.Add(presenter.PresentAsync(i));
            // The viewer decode lane runs one decode at a time: the first one has really started (so it cannot be
            // dropped as "not yet started") before the burst supersedes it; the rest are cancelled while pending.
            if (i == 0) await decoder.Started(paths[0]).WaitAsync(TimeSpan.FromSeconds(20));
        }

        // Finish the decodes in a seeded random order, the last navigation somewhere in the middle.
        var rng = new Random(seed);
        var order = Enumerable.Range(0, count).OrderBy(_ => rng.Next()).ToList();
        foreach (var i in order) decoder.Open(paths[i]);

        await Task.WhenAll(presents).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal([paths[^1]], _sink.PresentedPaths);
        Assert.Equal(count - 1, _catalog.CurrentIndex);
        Assert.Single(_sink.Images, i => i is not null);
        Assert.Same(_sink.CurrentImage, presenter.CurrentImage);
        Assert.Equal(Path.GetFileName(paths[^1]), presenter.CurrentPhotoInfo?.FileName);
    }

    [Fact(DisplayName = "A missing compare partner keeps the (existing) current image in the catalog and on screen")]
    public async Task PresentAsync_ComparePartnerMissing_KeepsCurrentImageAndClearsCompare()
    {
        var current = CreateFakeImageFile("photo.png");
        var partner = Path.Combine(_tempDir, "photo (1).png"); // listed in the catalog but gone from disk
        _catalog.Reset([current, partner]);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(current, _catalog.Paths);
        Assert.NotNull(presenter.CurrentImage);
        Assert.False(_compareViewModel.IsVisible);
        Assert.Null(_compareViewModel.LeftPath);
        Assert.Contains("photo (1).png", presenter.StatusText, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A stale token can never remove a catalog entry")]
    public async Task RemoveMissingCatalogItemAsync_StaleToken_LeavesCatalogUntouched()
    {
        var f1 = Path.Combine(_tempDir, "gone.png");
        var f2 = CreateFakeImageFile("here.png");
        _catalog.Reset([f1, f2]);
        var presenter = CreatePresenter();
        var staleToken = _clock.NextNavigation();
        _clock.NextNavigation();

        await presenter.RemoveMissingCatalogItemAsync(f1, 0, staleToken);

        Assert.Equal([f1, f2], _catalog.Paths);
    }

    [Fact(DisplayName = "The last file vanishing while it is shown ends in the empty state without throwing")]
    public async Task PresentAsync_SingleImageDeletedExternally_EndsEmpty()
    {
        var f1 = CreateFakeImageFile("single.png");
        _catalog.Reset([f1]);
        var presenter = CreatePresenter();
        await presenter.PresentAsync(0);
        File.Delete(f1);

        await presenter.PresentAsync(0); // e.g. the user presses Home on the only image

        Assert.Equal(0, _catalog.Count);
        Assert.Null(presenter.CurrentImage);
        Assert.Null(presenter.CurrentPhotoInfo);
        Assert.Equal(0, presenter.CurrentOriginalWidth);
    }

    [Fact(DisplayName = "A file renamed away while another image is presented is dropped once it is reached, the next one is shown")]
    public async Task PresentAsync_FileRenamedExternally_IsSkippedWhenReached()
    {
        var f1 = CreateFakeImageFile("first.png");
        var f2 = CreateFakeImageFile("second.png");
        var f3 = CreateFakeImageFile("third.png");
        _catalog.Reset([f1, f2, f3]);
        var presenter = CreatePresenter();
        await presenter.PresentAsync(0);
        File.Move(f2, Path.Combine(_tempDir, "renamed.png"));

        await presenter.PresentAsync(1);

        Assert.Equal([f1, f3], _catalog.Paths);
        Assert.Equal(1, _catalog.CurrentIndex);
        Assert.Equal("third.png", presenter.CurrentPhotoInfo?.FileName);
    }

    /// <summary>Decoder whose <c>Decode</c> of each path blocks until <see cref="Open"/> is called for that path.</summary>
    private sealed class PerPathGatedDecoder : IImageDecoder
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.OrdinalIgnoreCase);

        public Task Started(string path) =>
            _started.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        private TaskCompletionSource Gate(string path) =>
            _gates.GetOrAdd(path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Open(string path) => Gate(path).TrySetResult();

        public IDecodedImage Decode(DecodeRequest request)
        {
            _started.GetOrAdd(request.Path, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            Gate(request.Path).Task.Wait(TimeSpan.FromSeconds(20));
            return new FakeDecodedImage();
        }

        public ImageInfo ReadInfo(string path) => new(1, 1);
    }
}
