using System.IO;
using System.Runtime.InteropServices;
using PhotoReview.App.Coordinators;
using PhotoReview.App.ViewModels;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using Xunit;

namespace PhotoReview.App.Tests.Coordinators;

/// <summary>Stryker round 1 (App): <see cref="ImagePresenter"/> behaviours the existing tests left unpinned.</summary>
public sealed partial class ImagePresenterTests
{
    private sealed class ThrowingInfoDecoder(Func<Exception> failure) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();
        public IDecodedImage Decode(DecodeRequest request) => _inner.Decode(request);
        public ImageInfo ReadInfo(string path) => throw failure();
    }

    private sealed class FakeExternalException() : ExternalException("external");

    private sealed class OnDecodeDecoder(string watchedPath, Action onDecode) : IImageDecoder
    {
        private readonly WpfBitmapImageDecoder _inner = new();

        public IDecodedImage Decode(DecodeRequest request)
        {
            if (string.Equals(request.Path, watchedPath, StringComparison.OrdinalIgnoreCase)) onDecode();
            return _inner.Decode(request);
        }

        public ImageInfo ReadInfo(string path) => _inner.ReadInfo(path);
    }

    private ImagePresenter PresenterFor(PreviewImageService service, Action<string>? onPresented = null) => new(
        _catalog, _clock, service, _thumbnailCache, _preloadController, _compareViewModel, _hashService, _metrics,
        () => _settings, _sessionStore, _sink, fileSystem: null, getSession: () => null, onPresentedHook: onPresented);

    public static TheoryData<string> ExpectedDimensionLookupFailures() =>
    [
        nameof(IOException),
        nameof(UnauthorizedAccessException),
        nameof(NotSupportedException),
        nameof(FormatException),
        nameof(InvalidOperationException),
        nameof(InvalidDataException),
        nameof(ExternalException),
        nameof(DecoderBusyException),
    ];

    private static Exception MakeFailure(string typeName) => typeName switch
    {
        nameof(IOException) => new IOException("io"),
        nameof(UnauthorizedAccessException) => new UnauthorizedAccessException("denied"),
        nameof(NotSupportedException) => new NotSupportedException("nope"),
        nameof(FormatException) => new FormatException("format"),
        nameof(InvalidOperationException) => new InvalidOperationException("invalid"),
        nameof(InvalidDataException) => new InvalidDataException("data"),
        nameof(ExternalException) => new FakeExternalException(),
        nameof(DecoderBusyException) => new DecoderBusyException(),
        _ => throw new ArgumentOutOfRangeException(nameof(typeName)),
    };

    [Theory]
    [MemberData(nameof(ExpectedDimensionLookupFailures))]
    public async Task PresentAsync_DimensionLookupFailsWithAnExpectedHeaderReadFailure_KeepsTheImageWithoutDimensions(string failureType)
    {
        var file = CreateFakeImageFile("dims.png");
        _catalog.Reset([file]);
        var service = CreateServiceWith(new ThrowingInfoDecoder(() => MakeFailure(failureType)));
        var presenter = PresenterFor(service, _ => service.ClearCache());

        await presenter.PresentAsync(0).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(presenter.CurrentImage);
        Assert.False(presenter.StatusNeedsAttention);
        Assert.Equal(StatusFormatter.Ready(0, 1, new FileInfo(file).Length, "dims.png"), presenter.StatusText);
    }

    [Fact]
    public async Task PresentAsync_StaleNotFoundKeepsFailingWhileTheFileExists_RetriesExactlyThreeTimesThenReportsTheError()
    {
        var first = CreateFakeImageFile("first.png");
        var restored = CreateFakeImageFile("restored.png");
        var decoder = new AlwaysStaleDecoder(restored);
        var presenter = PresenterFor(CreateServiceWith(decoder));
        _catalog.Reset([first, restored]);

        await presenter.PresentAsync(1).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(4, decoder.MemberCalls); // the first decode plus MaxStaleNotFoundRetries (3) re-presents
        Assert.True(presenter.StatusNeedsAttention);
        Assert.Equal([first, restored], _catalog.Paths);
    }

    [Fact]
    public async Task PresentAsync_CaptureMemberOverride_DoesNotOverwriteTheRepresentativeMetadata()
    {
        var representative = Path.Combine(_tempDir, "rep.png");
        var member = Path.Combine(_tempDir, "member.png");
        File.WriteAllBytes(representative, CreatePng(1, 1));
        File.WriteAllBytes(member, CreatePng(5, 4));
        var repInfo = new FileInfo(representative);
        Assert.NotEqual(repInfo.Length, new FileInfo(member).Length);
        _catalog.Reset(
            [new CatalogEntry(representative) { CaptureGroup = new CaptureGroup(representative, member) }
                .WithMetadata(repInfo.Length, repInfo.LastWriteTimeUtc)],
            RawPairMode.Separate);
        var presenter = CreatePresenter();

        await presenter.PresentAsync(0, allowCompare: false, pathOverride: member);

        var entry = _catalog.Find(representative);
        Assert.NotNull(entry);
        Assert.Equal(repInfo.Length, entry!.Length);
        Assert.Equal(repInfo.LastWriteTimeUtc, entry.LastWriteUtc);
        Assert.Equal(5, presenter.CurrentOriginalWidth);
    }

    [Fact]
    public async Task PresentAsync_EarlierEntryRemovedWhileDecoding_PreloadAndStatusUseTheShiftedIndex()
    {
        var earlier = CreateFakeImageFile("earlier.png");
        var shown = CreateFakeImageFile("shown.png");
        var service = CreateServiceWith(new OnDecodeDecoder(shown, () => _catalog.Remove(earlier)));
        var presenter = PresenterFor(service);
        _catalog.Reset([earlier, shown]);

        await presenter.PresentAsync(1).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([shown], _catalog.Paths);
        Assert.Equal([0], _preloadController.PreloadAroundCalls);
        Assert.Equal(StatusFormatter.WithDimensions(0, 1, new FileInfo(shown).Length, 1, 1, "shown.png"), presenter.StatusText);
    }
}
