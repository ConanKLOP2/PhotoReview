using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Imaging.LibRaw;
using PhotoReview.Imaging.TurboJpeg;

namespace PhotoReview.App.Composition;

/// <summary>
/// Builds the explicit set of <see cref="IImageDecoderFactory"/> providers for the App.
/// Replaces the reflection-based (<c>Type.GetType</c>/<c>Activator.CreateInstance</c>) lookup that
/// let the TurboJpeg backend silently disappear when the App did not reference its assembly (AR01).
/// TurboJpeg is registered only when <see cref="TurboJpegAvailability.Probe"/> confirms the native
/// library actually loads and initializes; otherwise the reason is logged once, forced past the
/// default logging-off setting so it is visible even without diagnostics enabled.
/// </summary>
internal static class DecoderProviders
{
    /// <param name="sourceReader">
    /// Q-R29 option C-2 seam: shared with every decoder this builds, so a perf-harness throttling
    /// decorator (never a production default) can see every decode's own source-file open.
    /// </param>
    /// <param name="isLibRawNeeded">
    /// Whether LibRaw is actually wanted right now (RAW support enabled, or the LibRaw backend selected). A missing
    /// libraw.dll is an error only then; with RAW support off it is an ordinary, expected state and is logged at info
    /// level (visible only with diagnostics on) instead of forcing an error entry into the log at every startup.
    /// Null = treated as needed (the previous behaviour).
    /// </param>
    public static List<(DecoderBackend Backend, Func<IImageDecoder> Factory)> Create(ISourceReader sourceReader,
        Func<bool>? isLibRawNeeded = null)
        => Create(sourceReader, isLibRawNeeded, LibRawAvailability.Probe, TurboJpegAvailability.Probe, App.LogStartupErrorForced, AppLog.Info);

    internal delegate bool LibRawProbe(out string? reason);

    internal delegate bool TurboJpegProbe(out string? reason);

    internal static List<(DecoderBackend Backend, Func<IImageDecoder> Factory)> Create(ISourceReader sourceReader,
        Func<bool>? isLibRawNeeded, LibRawProbe libRawProbe, TurboJpegProbe turboJpegProbe,
        Action<string, Exception> logForcedError, Action<string> logInfo)
    {
        var providers = new List<(DecoderBackend, Func<IImageDecoder>)>
        {
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder(sourceReader)),
            (DecoderBackend.WicDirect, () => new WicDirectDecoder(sourceReader))
        };

        if (turboJpegProbe(out string? reason))
        {
            Func<IImageDecoder> createTurboJpeg = () => new TurboJpegDecoder();
            providers.Add((DecoderBackend.TurboJpeg, createTurboJpeg));
        }
        else
        {
            logForcedError(
                $"TurboJPEG decoder backend is not available and will not be registered: {reason}",
                new InvalidOperationException(reason ?? "TurboJPEG probe failed."));
        }

        if (libRawProbe(out string? libRawReason))
        {
            providers.Add((DecoderBackend.LibRaw, () => new LibRawDecoder()));
        }
        else if (isLibRawNeeded?.Invoke() ?? true)
        {
            logForcedError(
                $"LibRaw decoder backend is not available and will not be registered: {libRawReason}",
                new InvalidOperationException(libRawReason ?? "LibRaw probe failed."));
        }
        else
        {
            logInfo($"LibRaw decoder backend is not available (RAW support is off, so it is not needed): {libRawReason}");
        }

        return providers;
    }
}
