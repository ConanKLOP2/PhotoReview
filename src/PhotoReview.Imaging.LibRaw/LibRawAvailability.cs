using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PhotoReview.Imaging.LibRaw;

/// <summary>Checks once whether the pinned native LibRaw runtime can load, exposes every entry point we import, matches the pinned version and initializes.</summary>
public static class LibRawAvailability
{
    /// <summary>The LibRaw major.minor this build is pinned to (native/libraw.sha256 pins 0.22.2); patch releases are ABI compatible.</summary>
    internal const int RequiredMajor = 0;
    internal const int RequiredMinor = 22;

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
            return ProbeLoadedLibrary(libraryHandle, LibRawNativeMethods.LibraryName);
        }
        finally
        {
            NativeLibrary.Free(libraryHandle);
        }
    }

    /// <summary>Every native entry point declared in <see cref="LibRawNativeMethods"/> plus the version query the probe itself needs.</summary>
    internal static IReadOnlyList<string> RequiredExports { get; } = BuildRequiredExports();

    private static string[] BuildRequiredExports()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal) { "libraw_version" };
        foreach (var method in typeof(LibRawNativeMethods).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            var import = method.GetCustomAttribute<DllImportAttribute>();
            if (import is not null && !string.IsNullOrEmpty(import.EntryPoint)) names.Add(import.EntryPoint);
        }

        return [.. names];
    }

    /// <summary>Parses a LibRaw version string ("0.22.2") into major/minor; false when it does not start with two numeric components.</summary>
    internal static bool TryParseVersion(string? text, out int major, out int minor)
    {
        major = minor = 0;
        var parts = (text ?? string.Empty).Split('.');
        return parts.Length >= 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    /// <summary>
    /// Validates an already loaded native library: all required exports resolve (a missing one would otherwise surface as an
    /// <see cref="EntryPointNotFoundException"/> at decode time), <c>libraw_version()</c> is the pinned major.minor, and init/close work.
    /// The caller owns the handle.
    /// </summary>
    internal static unsafe (bool Available, string? Reason) ProbeLoadedLibrary(IntPtr libraryHandle, string displayName)
    {
        var missing = RequiredExports.Where(name => !NativeLibrary.TryGetExport(libraryHandle, name, out _)).ToArray();
        if (missing.Length > 0)
        {
            return (false, $"{displayName} does not export the required LibRaw entry point(s): {string.Join(", ", missing)} (wrong or stale library).");
        }

        try
        {
            var version = Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(libraryHandle, "libraw_version"))());
            if (!TryParseVersion(version, out var major, out var minor))
            {
                return (false, $"{displayName} reported an unreadable LibRaw version '{version}'.");
            }

            if (major != RequiredMajor || minor != RequiredMinor)
            {
                return (false, $"{displayName} is LibRaw {version} but this build requires {RequiredMajor}.{RequiredMinor}.x (run tools/fetch-libraw.ps1).");
            }

            var init = (delegate* unmanaged[Cdecl]<uint, IntPtr>)NativeLibrary.GetExport(libraryHandle, "libraw_init");
            var close = (delegate* unmanaged[Cdecl]<IntPtr, void>)NativeLibrary.GetExport(libraryHandle, "libraw_close");
            var raw = init(0);
            if (raw == IntPtr.Zero) return (false, "libraw_init returned a null decoder handle.");
            close(raw);
            return (true, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            return (false, $"{displayName} loaded but failed to initialize: {ex.Message}");
        }
    }
}
