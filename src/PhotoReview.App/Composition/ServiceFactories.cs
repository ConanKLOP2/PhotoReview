using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.Raw;

namespace PhotoReview.App.Composition;

/// <summary>
/// Testable seams for two wirings of the DI graph that no other test observes: the Recovery retry's live
/// permanent-delete setting and the RAW decoder's LibRaw last-resort decoder. App.xaml.cs calls these.
/// </summary>
internal static class ServiceFactories
{
    /// <summary>
    /// The retry re-reads <see cref="AppSettings.AllowPermanentDeleteWithoutRecycleBin"/> from the store on every call
    /// (a change made after construction applies), same rule as the first run of a permanent delete (Q-R8).
    /// <paramref name="fileActionGate"/> is the app's single <see cref="FileActionService"/>: a retry takes its INV-4 gate (RV-C08).
    /// </summary>
    public static RecoveryRetryService CreateRecoveryRetryService(OperationJournal journal, IFileSystem fileSystem,
        IClock clock, IRecycleBin recycleBin, SettingsStore settingsStore, FileActionService? fileActionGate = null)
        => new(journal, fileSystem, clock, recycleBin,
            () => settingsStore.Current.AllowPermanentDeleteWithoutRecycleBin, fileActionGate);

    /// <param name="codec">
    /// WP-05: null (the WPF app today) keeps LibRaw's last-resort decoder on the WPF <c>BitmapSource</c> path; the Win32 shell passes
    /// its codec so that decoder writes straight into a <c>PixelBuffer</c>.
    /// </param>
    public static RawDecoder CreateRawDecoder(IImageDecoder standardDecoder, ISourceReader sourceReader,
        SourceBytesCache? sourceBytesCache, IPlatformImageCodec? codec = null)
        => CreateRawDecoder(standardDecoder, sourceReader, sourceBytesCache,
            LibRawAvailability.Probe, () => new LibRawPreviewFallback(),
            () => codec is null ? new LibRawDecoder() : new LibRawDecoder(codec));

    internal static RawDecoder CreateRawDecoder(IImageDecoder standardDecoder, ISourceReader sourceReader,
        SourceBytesCache? sourceBytesCache, DecoderProviders.LibRawProbe libRawProbe,
        Func<IRawPreviewFallback> createPreviewFallback, Func<IImageDecoder> createLibRawDecoder)
    {
        IRawPreviewFallback? rawPreviewFallback = libRawProbe(out _) ? createPreviewFallback() : null;
        return new RawDecoder(standardDecoder, sourceReader, sourceBytesCache: sourceBytesCache,
            previewFallback: rawPreviewFallback,
            // A RAW with no embedded JPEG (Leica M8 DNG, some phone DNGs) is decoded by LibRaw instead of failing.
            noPreviewDecoder: rawPreviewFallback is null ? null : createLibRawDecoder());
    }

    /// <summary>
    /// Q-FMT-WEBP-HEIC: the decoder chain every WebP/HEIC path takes, whatever backend the user picked -- WicDirect (colour
    /// management, EXIF orientation, premultiplied alpha, DCT/codec pre-scaling) with the usual WPF fallback, both reading
    /// through the shared <paramref name="sourceReader"/>.
    /// </summary>
    public static IImageDecoder CreateWebpHeicDecoder(ISourceReader sourceReader, ILog? log, ReviewMetrics? metrics)
        => new FallbackImageDecoder(new WicDirectDecoder(WpfBitmapSourceCodec.Instance, sourceReader), DecoderBackend.WicDirect,
            new WpfBitmapImageDecoder(sourceReader), log, metrics);

    /// <summary>
    /// The preload's source-bytes prefetch. Q-FMT-WEBP-HEIC: a WebP/HEIC file the router will refuse (switch off, or no Windows
    /// codec) is not read at all -- no decode could use its bytes (AGENTS.md priority 1: no wasted disk reads).
    /// </summary>
    public static Func<string, CancellationToken, Task> CreateSourcePrefetch(SourceBytesCache cache, Func<bool> isWebpHeicEnabled,
        Func<WicCodecSupport> codecs)
    {
        ArgumentNullException.ThrowIfNull(cache);
        return (path, token) => WebpHeicRoutingDecoder.WillRefuse(path, isWebpHeicEnabled, codecs)
            ? Task.CompletedTask
            : Task.Run(() => cache.TryPrefetch(path), token);
    }
}
