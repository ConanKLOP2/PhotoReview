using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Which parts of the photo information line (under the status line) are shown. Written to config.json as
/// comma-separated names (<c>"FileName, Camera"</c>); numbers are accepted too, unknown bits are dropped and an
/// unreadable value falls back to <see cref="All"/> instead of failing the whole config load.
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

    All = FileName | DateTaken | Dimensions | Camera | Lens | Iso | FocalLength | Aperture | ShutterSpeed,
}

/// <summary>Lenient JSON form of <see cref="ExifInfoFields"/> (see the type doc).</summary>
public sealed class ExifInfoFieldsJsonConverter : JsonConverter<ExifInfoFields>
{
    public override ExifInfoFields Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) ? (ExifInfoFields)(number & (long)ExifInfoFields.All) : ExifInfoFields.All;
            case JsonTokenType.String:
                var text = reader.GetString();
                return !string.IsNullOrWhiteSpace(text) && Enum.TryParse<ExifInfoFields>(text, ignoreCase: true, out var parsed)
                    ? parsed & ExifInfoFields.All
                    : ExifInfoFields.All;
            default:
                reader.Skip();
                return ExifInfoFields.All;
        }
    }

    public override void Write(Utf8JsonWriter writer, ExifInfoFields value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue((value & ExifInfoFields.All).ToString());
    }
}
