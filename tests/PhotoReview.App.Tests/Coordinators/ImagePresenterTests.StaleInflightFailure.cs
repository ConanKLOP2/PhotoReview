using System.IO;
using System.Threading.Tasks;
using PhotoReview.App.Coordinators;
using PhotoReview.Core.Catalog;
using PhotoReview.Imaging.Caching;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Core.Model;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>
/// OC14 flake root cause: a preview decode that is still in flight is shared (joined) by every later request for the
/// same key (path + length + mtime). When that decode opened the file while it was moved away, it fails with
/// FileNotFound; a Move followed by Undo puts the file back with the SAME key, so the presenter joined the failing
/// decode and dropped a photo that is on disk again from the catalog.
/// </summary>
public sealed partial class ImagePresenterTests
{
    /// <summary>
    /// Real decoder, except that the FIRST decode of <c>watchedPath</c> that fails with FileNotFound holds its failure
    /// back (<see cref="Failed"/> is raised, then the exception is only thrown once <see cref="Release"/> is set):
    /// the deterministic stand-in for "the decode is slow to report that the file was not there".
    /// </summary>
    private sealed class HoldFirstFailureDecoder(string watchedPath) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        private int _held;

        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDecodedImage Decode(DecodeRequest request)
        {
            try
            {
                return _inner.Decode(request);
            }
            catch (FileNotFoundException) when (string.Equals(request.Path, watchedPath, StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _held, 1) == 0)
            {
                Failed.TrySetResult();
                Release.Task.GetAwaiter().GetResult();
                throw;
            }
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    /// <summary>A member whose first decode fails with FileNotFound (stale, the file is on disk) and whose retry fails with a generic error.</summary>
    private sealed class StaleThenBrokenDecoder(string memberPath) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        private int _memberCalls;

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (!string.Equals(request.Path, memberPath, StringComparison.OrdinalIgnoreCase)) return _inner.Decode(request);
            throw Interlocked.Increment(ref _memberCalls) == 1
                ? new FileNotFoundException("stale", request.Path)
                : new InvalidOperationException("broken member");
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    /// <summary>Every decode of the member fails with FileNotFound although the file is on disk: failures that all joined an older failed decode.</summary>
    private sealed class AlwaysStaleDecoder(string memberPath) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        public int MemberCalls;

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (!string.Equals(request.Path, memberPath, StringComparison.OrdinalIgnoreCase)) return _inner.Decode(request);
            Interlocked.Increment(ref MemberCalls);
            throw new FileNotFoundException("stale", request.Path);
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    [Fact(DisplayName = "OC14 under load: repeated stale FileNotFound failures never drop a file that is on disk from the catalog")]
    public async Task PresentAsync_StaleNotFoundKeepsFailingWhileTheFileExists_KeepsTheEntry()
    {
        var first = CreateFakeImageFile("first.png");
        var restored = CreateFakeImageFile("restored.png");
        var decoder = new AlwaysStaleDecoder(restored);
        var service = new PreviewImageService(
            _metrics,
            () => false,
            (Func<DecodeBox>)(() => new DecodeBox(1920, 0)),
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => DecoderBackend.Wpf,
            disableDiskCacheOverride: true,
            decoder: decoder);
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null);
        _catalog.Reset([first, restored]);

        await presenter.PresentAsync(1).WithTimeout(Wait.DefaultTimeout, "the presentation");

        Assert.True(File.Exists(restored));
        Assert.Equal([first, restored], _catalog.Paths); // before the fix the second failure removed it without asking the file system
        Assert.True(decoder.MemberCalls >= 2); // it was retried, not given up on at once
    }

    [Fact(DisplayName = "A generic failure of the retry pass after a stale FileNotFound restores the member that is really on screen")]
    public async Task PresentAsync_StaleNotFoundRetryThenGenericFailure_KeepsThePresentedPathOnTheMemberOnScreen()
    {
        var jpeg = CreateFakeImageFile("pair-shown.jpg");
        var member = CreateFakeImageFile("pair-shown-member.png");
        var service = new PreviewImageService(
            _metrics,
            () => false,
            (Func<DecodeBox>)(() => new DecodeBox(1920, 0)),
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => DecoderBackend.Wpf,
            disableDiskCacheOverride: true,
            decoder: new StaleThenBrokenDecoder(member));
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null);
        _catalog.Reset([new CatalogEntry(jpeg) { CaptureGroup = new CaptureGroup(jpeg, member) }], RawPairMode.Separate);
        await presenter.PresentAsync(0);
        Assert.Equal(jpeg, presenter.CurrentPresentedPath);

        await presenter.PresentAsync(0, allowCompare: false, pathOverride: member);

        Assert.Equal(jpeg, presenter.CurrentPresentedPath); // the JPEG is still what is displayed
    }

    [Fact(DisplayName = "OC14: joining an in-flight decode that failed while the file was away does not drop the restored photo")]
    public async Task PresentAsync_JoinedInflightDecodeFailedWhileFileWasAway_KeepsRestoredPhotoAndPresentsIt()
    {
        var first = CreateFakeImageFile("first.png");
        var moved = CreateFakeImageFile("moved.png");
        var away = Path.Combine(_tempDir, "away.png");
        var reader = new HoldFirstFailureDecoder(moved);
        var service = new PreviewImageService(
            _metrics,
            () => false,
            (Func<DecodeBox>)(() => new DecodeBox(1920, 0)),
            capacityBytes: 64 * 1024 * 1024,
            currentBackend: () => DecoderBackend.Wpf,
            disableDiskCacheOverride: true,
            decoder: reader);
        var presenter = new ImagePresenter(
            _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
            () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null);
        _catalog.Reset([first, moved]);

        // A preload/earlier present of 'moved' is in flight when the user Moves it away: its open fails, but the
        // failure is only reported later (it stays the shared in-flight decode for that key until then).
        var key = service.GetCurrentCacheKey(moved);
        File.Move(moved, away);
        var inflight = service.GetPreviewAsync(moved, key);
        await reader.Failed.Task.WithTimeout(Wait.DefaultTimeout, "the in-flight decode's failed open");

        // Undo: the file is back (a Move keeps length and mtime, so the cache key is identical) and is presented.
        File.Move(away, moved);
        var present = presenter.PresentAsync(1);
        await Wait.UntilAsync(() => _metrics.Snapshot().InflightJoins >= 1, "the presentation joining the in-flight decode");

        reader.Release.TrySetResult();
        await Assert.ThrowsAsync<FileNotFoundException>(() => inflight);
        await present.WithTimeout(Wait.DefaultTimeout, "the presentation");

        Assert.True(File.Exists(moved));
        Assert.Equal([first, moved], _catalog.Paths);
        Assert.Equal(1, _catalog.CurrentIndex);
        Assert.NotNull(presenter.CurrentImage);
    }
}
