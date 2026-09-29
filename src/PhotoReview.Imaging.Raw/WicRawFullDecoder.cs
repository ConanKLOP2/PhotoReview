using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32;

namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Probes whether Windows Imaging Component has a registered RAW decoder for a container format.
/// Full-decode behavior is added on top of this cached per-format capability check.
/// </summary>
public sealed class WicRawFullDecoder
{
    private const string WicDecoderCategoryPath = @"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";
    private readonly Func<RawFormat, bool> _availabilityProbe;
    private readonly ConcurrentDictionary<RawFormat, Lazy<bool>> _availability = new();

    /// <summary>Creates a decoder capability probe. The optional delegate is a test seam.</summary>
    public WicRawFullDecoder(Func<RawFormat, bool>? availabilityProbe = null) =>
        _availabilityProbe = availabilityProbe ?? IsRegisteredInWic;

    /// <summary>Returns the cached codec-registration result for <paramref name="format"/>.</summary>
    public bool IsCodecAvailable(RawFormat format) =>
        _availability.GetOrAdd(format, key => new Lazy<bool>(() => _availabilityProbe(key))).Value;

    private static bool IsRegisteredInWic(RawFormat format)
    {
        var extension = ExtensionFor(format);
        if (extension is null) return false;

        try
        {
            using var category = Registry.ClassesRoot.OpenSubKey(WicDecoderCategoryPath);
            if (category is null) return false;

            foreach (var name in category.GetSubKeyNames())
            {
                using var codec = category.OpenSubKey(name);
                var friendlyName = codec?.GetValue("FriendlyName") as string;
                var extensions = codec?.GetValue("FileExtensions");
                var extensionText = extensions switch
                {
                    string value => value,
                    string[] values => string.Join(';', values),
                    _ => string.Empty
                };

                if (extensionText.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Microsoft registers its general-purpose RAW decoder without listing extensions.
                if (string.IsNullOrWhiteSpace(extensionText) &&
                    friendlyName?.Contains("raw", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }

    private static string? ExtensionFor(RawFormat format) => format switch
    {
        RawFormat.Cr2 => ".cr2",
        RawFormat.Cr3 => ".cr3",
        RawFormat.Nef => ".nef",
        RawFormat.Nrw => ".nrw",
        RawFormat.Arw => ".arw",
        RawFormat.Dng => ".dng",
        RawFormat.Raf => ".raf",
        RawFormat.Orf => ".orf",
        RawFormat.Rw2 => ".rw2",
        RawFormat.Pef => ".pef",
        _ => null
    };
}
