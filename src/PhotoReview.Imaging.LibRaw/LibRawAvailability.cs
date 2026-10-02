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

    /// <summary>The exact patch release whose libraw_data_t layout the raw struct write was derived from and verified against (0.22.2).</summary>
    internal const int RequiredPatch = 2;

    private static readonly Lazy<bool> ExactVersionResult = new(() =>
    {
        try
        {
            return Probe(out _) && CheckExactVersion(LibRawNativeMethods.GetVersionString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // do not let the Lazy cache a throwing exception
        }
    });

    /// <summary>True when the loaded runtime is exactly the pinned 0.22.2 (the only version the raw struct write is allowed on).</summary>
    internal static bool IsExactPinnedVersion => ExactVersionResult.Value;

    /// <summary>
    /// True only for the exact pinned major.minor.patch (a suffix such as "-Release" is ignored). Other patch releases are
    /// ABI compatible for the C API but their struct layout is not proven, so raw memory writes must be skipped for them.
    /// </summary>
    internal static bool CheckExactVersion(string? version)
    {
        if (!TryParseVersion(version, out var major, out var minor) || major != RequiredMajor || minor != RequiredMinor) return false;
        var parts = (version ?? string.Empty).Split('.');
        if (parts.Length < 3) return false;
        var digits = new string(parts[2].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var patch) && patch == RequiredPatch;
    }

    private static readonly Lazy<(bool Available, string? Reason)> ProbeResult = new(RunProbe);

    public static bool Probe(out string? reason)
    {
        var result = ProbeResult.Value;
        reason = result.Reason;
        return result.Available;
    }

    private static (bool, string?) RunProbe()
    {
        // Lazy caches a thrown exception for the process lifetime: turn any probe failure (not memory exhaustion) into a reason.
        try
        {
            return RunProbeCore();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, $"{LibRawNativeMethods.LibraryName} probe failed: {ex.Message}");
        }
    }

    private static (bool, string?) RunProbeCore()
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

    /// <summary>Null when <paramref name="version"/> is the pinned major.minor; otherwise the reason the library must be rejected. Runs before init and before any raw struct write.</summary>
    internal static string? CheckVersion(string? version, string displayName)
    {
        if (!TryParseVersion(version, out var major, out var minor))
            return $"{displayName} reported an unreadable LibRaw version '{version}'.";
        return major != RequiredMajor || minor != RequiredMinor
            ? $"{displayName} is LibRaw {version} but this build requires {RequiredMajor}.{RequiredMinor}.x (run tools/fetch-libraw.ps1)."
            : null;
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
            if (CheckVersion(version, displayName) is { } versionProblem) return (false, versionProblem);

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
