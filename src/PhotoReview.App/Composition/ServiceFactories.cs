using System;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Settings;
using PhotoReview.Imaging.Decoding;
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
    /// </summary>
    public static RecoveryRetryService CreateRecoveryRetryService(OperationJournal journal, IFileSystem fileSystem,
        IClock clock, IRecycleBin recycleBin, SettingsStore settingsStore)
        => new(journal, fileSystem, clock, recycleBin,
            () => settingsStore.Current.AllowPermanentDeleteWithoutRecycleBin);

    public static RawDecoder CreateRawDecoder(IImageDecoder standardDecoder, ISourceReader sourceReader,
        SourceBytesCache? sourceBytesCache)
        => CreateRawDecoder(standardDecoder, sourceReader, sourceBytesCache,
            LibRawAvailability.Probe, () => new LibRawPreviewFallback(), () => new LibRawDecoder());

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
}
