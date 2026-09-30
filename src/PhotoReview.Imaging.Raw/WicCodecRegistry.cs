using System.IO;
using Microsoft.Win32;

namespace PhotoReview.Imaging.Raw;

/// <summary>One registered WIC decoder: its friendly name and the extensions it declares.</summary>
internal sealed record WicCodecRegistration(string? FriendlyName, string? FileExtensions);

/// <summary>Enumerates the WIC decoders registered on the machine (test seam over the registry).</summary>
internal interface IWicCodecRegistry
{
    IReadOnlyList<WicCodecRegistration> ReadDecoders();
}

/// <summary>
/// Reads the WIC decoder category (<c>HKCR\CLSID\{7ED96837-...}\Instance</c>). That key only holds each codec's CLSID and
/// FriendlyName; the extension list is the <c>FileExtensions</c> value of the codec's own class key
/// (<c>HKCR\CLSID\{codec-clsid}</c>).
/// </summary>
internal sealed class WindowsWicCodecRegistry : IWicCodecRegistry
{
    private const string DecoderCategoryPath = @"CLSID\{7ED96837-96F0-4812-B211-F13C24117ED3}\Instance";

    public static WindowsWicCodecRegistry Instance { get; } = new();

    public IReadOnlyList<WicCodecRegistration> ReadDecoders()
    {
        var result = new List<WicCodecRegistration>();
        using var category = Registry.ClassesRoot.OpenSubKey(DecoderCategoryPath);
        if (category is null) return result;

        foreach (var name in category.GetSubKeyNames())
        {
            using var instance = category.OpenSubKey(name);
            var clsid = instance?.GetValue("CLSID") as string ?? name;
            using var codec = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid);
            var friendlyName = codec?.GetValue("FriendlyName") as string ?? instance?.GetValue("FriendlyName") as string;
            var extensions = codec?.GetValue("FileExtensions") switch
            {
                string value => value,
                string[] values => string.Join(';', values),
                _ => null
            };
            result.Add(new WicCodecRegistration(friendlyName, extensions));
        }

        return result;
    }
}
