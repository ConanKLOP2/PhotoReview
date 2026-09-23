using System;
using System.Runtime.InteropServices;
using PhotoReview.Imaging.TurboJpeg.Native;

namespace PhotoReview.Imaging.TurboJpeg;

/// <summary>
/// Probes whether the native <c>turbojpeg.dll</c> is present, loadable, and functional
/// before the backend is registered with <see cref="Decoding.IImageDecoderFactory"/>.
/// Without this probe, a missing or incompatible native library would make every decode
/// throw and get caught as a fallbackable exception on the hot path (see AR01).
/// </summary>
public static class TurboJpegAvailability
{
    private static readonly Lazy<(bool Available, string? Reason)> ProbeResult = new(RunProbe);

    /// <summary>
    /// Returns whether the TurboJPEG native backend can be used. The probe runs once and is cached.
    /// </summary>
    public static bool Probe(out string? reason)
    {
        (bool available, string? probeReason) = ProbeResult.Value;
        reason = probeReason;
        return available;
    }

    private static (bool, string?) RunProbe()
    {
        if (!NativeLibrary.TryLoad(TurboJpegNative.DllName, typeof(TurboJpegNative).Assembly, null, out nint handle))
        {
            return (false, FormattableString.Invariant($"Could not load {TurboJpegNative.DllName} (not found or incompatible with the current process architecture)."));
        }

        try
        {
            using var decompressor = TurboJpegNative.CreateDecompressor();
            return (true, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            return (false, FormattableString.Invariant($"{TurboJpegNative.DllName} loaded but failed to initialize: {ex.Message}"));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
