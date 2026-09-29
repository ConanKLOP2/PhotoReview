using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Checks once whether the pinned native LibRaw runtime can load and initialize.</summary>
public static class LibRawAvailability
{
    private static readonly Lazy<(bool Available, string? Reason)> ProbeResult = new(RunProbe);

    public static bool Probe(out string? reason)
    {
        var result = ProbeResult.Value;
        reason = result.Reason;
        return result.Available;
    }

    private static (bool, string?) RunProbe()
    {
        if (!NativeLibrary.TryLoad(LibRawNativeMethods.LibraryName, typeof(LibRawAvailability).Assembly, null, out var libraryHandle))
        {
            return (false, $"Could not load {LibRawNativeMethods.LibraryName} (missing, incompatible architecture, or dependent runtime unavailable).");
        }

        try
        {
            var raw = LibRawNativeMethods.LibRawInit(0);
            if (raw == IntPtr.Zero) return (false, "libraw_init returned a null decoder handle.");
            LibRawNativeMethods.LibRawClose(raw);
            return (true, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            return (false, $"{LibRawNativeMethods.LibraryName} loaded but failed to initialize: {ex.Message}");
        }
        finally
        {
            NativeLibrary.Free(libraryHandle);
        }
    }
}
