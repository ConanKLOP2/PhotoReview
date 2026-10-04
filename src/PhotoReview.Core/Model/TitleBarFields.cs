using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Which parts are shown in the main window's title bar (in this fixed order, joined with " · "), on top of the
/// "Photo Review" app name / " · LOG" logging marker. Written to config.json as comma-separated names
/// (<c>"FolderName, FileName"</c>); numbers are accepted too, unknown bits are dropped and an unreadable value falls
/// back to <see cref="Default"/> instead of failing the whole config load. Same lenient-JSON approach as
/// <see cref="ExifInfoFields"/>.
/// </summary>
[Flags]
[JsonConverter(typeof(TitleBarFieldsJsonConverter))]
public enum TitleBarFields
{
    // Explicit, stable values: they are persisted in config.json (as names, but numbers are accepted too).
    None = 0,
    FolderName = 1,
    FolderPath = 2,
    IndexCount = 4,
    FileName = 8,
    FileSize = 16,
    Dimensions = 32,
    ModifiedDate = 64,
    DateTaken = 128,
    Camera = 256,
    Lens = 512,
    Iso = 1024,
    FocalLength = 2048,
    Aperture = 4096,
    ShutterSpeed = 8192,

    All = FolderName | FolderPath | IndexCount | FileName | FileSize | Dimensions | ModifiedDate |
          DateTaken | Camera | Lens | Iso | FocalLength | Aperture | ShutterSpeed,

    /// <summary>AppSettings default: the folder's own name only (short, matches the pre-feature title closely).</summary>
    Default = FolderName,
}

/// <summary>Lenient JSON form of <see cref="TitleBarFields"/> (see the type doc).</summary>
public sealed class TitleBarFieldsJsonConverter : JsonConverter<TitleBarFields>
{
    public override TitleBarFields Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return FlagEnumJson.Read(ref reader, TitleBarFields.All, TitleBarFields.Default);
    }

    public override void Write(Utf8JsonWriter writer, TitleBarFields value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(FlagEnumJson.Format(value, TitleBarFields.All)); // single flag names, never the All/Default aliases
    }
}
