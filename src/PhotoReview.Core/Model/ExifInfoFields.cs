using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Which parts of the photo information line (under the status line) are shown. Written to config.json as
/// comma-separated names (<c>"FileName, Camera"</c>); numbers are accepted too, unknown bits are dropped and an
/// unreadable value falls back to <see cref="Default"/> instead of failing the whole config load.
/// </summary>
[Flags]
[JsonConverter(typeof(ExifInfoFieldsJsonConverter))]
public enum ExifInfoFields
{
    // Explicit, stable values: they are persisted in config.json (as names, but numbers are accepted too).
    None = 0,
    FileName = 1,
    DateTaken = 2,
    Dimensions = 4,
    Camera = 8,
    Lens = 16,
    Iso = 32,
    FocalLength = 64,
    Aperture = 128,
    ShutterSpeed = 256,
    /// <summary>File last-write time (not a camera EXIF value); rendered like <see cref="DateTaken"/>, right after it.</summary>
    ModifiedDate = 512,

    All = FileName | DateTaken | Dimensions | Camera | Lens | Iso | FocalLength | Aperture | ShutterSpeed | ModifiedDate,

    /// <summary>
    /// AppSettings default: every EXIF field except FileName and Dimensions, which the status line above this one
    /// already shows (avoids showing the same file name / W×H twice), and ModifiedDate, which is off until the user
    /// turns it on (added after the others; new users get it too since it is off by default for everyone).
    /// </summary>
    Default = All & ~(FileName | Dimensions | ModifiedDate),
}

/// <summary>Lenient JSON form of <see cref="ExifInfoFields"/> (see the type doc).</summary>
public sealed class ExifInfoFieldsJsonConverter : JsonConverter<ExifInfoFields>
{
    public override ExifInfoFields Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // A valid number is a real bitmask value (unknown high bits stripped against All), not an error case.
                return reader.TryGetInt64(out var number) ? (ExifInfoFields)(number & (long)ExifInfoFields.All) : ExifInfoFields.Default;
            case JsonTokenType.String:
                var text = reader.GetString();
                return !string.IsNullOrWhiteSpace(text) && Enum.TryParse<ExifInfoFields>(text, ignoreCase: true, out var parsed)
                    ? parsed & ExifInfoFields.All
                    : ExifInfoFields.Default;
            default:
                reader.Skip();
                return ExifInfoFields.Default;
        }
    }

    public override void Write(Utf8JsonWriter writer, ExifInfoFields value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue((value & ExifInfoFields.All).ToString());
    }
}
