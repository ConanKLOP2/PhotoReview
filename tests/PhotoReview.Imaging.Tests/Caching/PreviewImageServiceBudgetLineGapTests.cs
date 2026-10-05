using System.IO;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Imaging.Decoding;

namespace PhotoReview.Imaging.Tests.Caching;

/// <summary>Mutation-gap tests for the "Memory budgets" start-up line (exact MiB figures) and the diagnostic source pre-read lane.</summary>
[Collection("GlobalState")]
public sealed class PreviewImageServiceBudgetLineGapTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private readonly TempRoot _root = new("preview-budget-gap");

    public void Dispose() => _root.Dispose();

    [Fact(DisplayName = "The percent budget line reports the exact preview, physical and share figures in MiB")]
    public void ResolveCapacity_PercentLine_HasExactMiBFigures()
    {
        var capacity = PreviewImageService.ResolveCapacity(1, 60, 32 * Gib, 2 * Gib, out var line);

        Assert.Equal(60L * 32 * Gib / 100 - 2 * Gib, capacity);
        // The figures are the contract; the surrounding wording may change without breaking this test.
        Assert.Contains("preview cache 17612 MiB", line, StringComparison.Ordinal);
        Assert.Contains("60%", line, StringComparison.Ordinal);
        Assert.Contains("32768 MiB physical RAM", line, StringComparison.Ordinal);
        Assert.Contains("19660 MiB", line, StringComparison.Ordinal);
        Assert.Contains("source-bytes cache 2048 MiB", line, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The clamped byte-budget line reports the requested size in MiB")]
    public void ResolveCapacity_ClampedLine_HasExactMiBFigures()
    {
        PreviewImageService.ResolveCapacity(64 * Gib, null, 32 * Gib, null, out var line);

        // The figures are the contract; the surrounding wording may change without breaking this test.
        Assert.Contains("preview cache 16384 MiB", line, StringComparison.Ordinal);
        Assert.Contains("65536 MiB", line, StringComparison.Ordinal);
        Assert.Contains("50%", line, StringComparison.Ordinal);
        Assert.Contains("source-bytes cache off", line, StringComparison.Ordinal);
    }

    // ---- diagnostic pre-read goes through the injected source reader ----

    private sealed class RecordingReader : ISourceReader
    {
        public int Opens;
        public Stream OpenSource(string path, SourceReadPriority priority, int bufferSize = 1024 * 1024)
        {
            Interlocked.Increment(ref Opens);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
    }

    private sealed class FakeDecoder : IImageDecoder
    {
        public IDecodedImage Decode(DecodeRequest request) => new FakeImage();
        public ImageInfo ReadInfo(string path) => new(1, 1);
    }

    private sealed class FakeImage : IDecodedImage
    {
        public int PixelWidth => 1;
        public int PixelHeight => 1;
        public bool Downscaled => false;
        public int Orientation => 1;
        public long EstimatedBytes => 4;
        public object PlatformImage { get; } = new();
    }

    [Fact(DisplayName = "With PHOTOREVIEW_DIAG_PREREAD=1 the service pre-reads the source through the injected ISourceReader")]
    public async Task DiagPreRead_UsesTheInjectedSourceReader()
    {
        const string name = "PHOTOREVIEW_DIAG_PREREAD";
        var previous = Environment.GetEnvironmentVariable(name);
        var reader = new RecordingReader();
        var path = _root.File(Path.Combine("images", "a.jpg"), 1, 2, 3, 4);
        var service = new PreviewImageService(new ReviewMetrics(), () => false, () => 256, capacityBytes: 64L * 1024 * 1024,
            diskCacheDirectory: _root.Dir("cache"), disableDiskCacheOverride: true, decoder: new FakeDecoder(), sourceReader: reader);
        try
        {
            Environment.SetEnvironmentVariable(name, "1");

            await service.GetPreviewAsync(path).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, reader.Opens);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
            await service.ShutdownPersistWorkersAsync();
        }
    }
}
