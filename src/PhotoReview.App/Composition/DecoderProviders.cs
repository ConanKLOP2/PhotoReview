using System;
using System.Collections.Generic;
using PhotoReview.Core.Model;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
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
    public static List<(DecoderBackend Backend, Func<IImageDecoder> Factory)> Create()
    {
        var providers = new List<(DecoderBackend, Func<IImageDecoder>)>
        {
            (DecoderBackend.Wpf, () => new WpfBitmapImageDecoder()),
            (DecoderBackend.WicDirect, () => new WicDirectDecoder())
        };

        if (TurboJpegAvailability.Probe(out string? reason))
        {
            Func<IImageDecoder> createTurboJpeg = () => new TurboJpegDecoder();
            providers.Add((DecoderBackend.TurboJpeg, createTurboJpeg));
        }
        else
        {
            App.LogStartupErrorForced(
                $"TurboJPEG decoder backend is not available and will not be registered: {reason}",
                new InvalidOperationException(reason ?? "TurboJPEG probe failed."));
        }

        return providers;
    }
}
