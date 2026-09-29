using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Handling mode for JPG+RAW pairs (Q-RAW-04).
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<RawPairMode>))]
public enum RawPairMode
{
    Separate = 0,
    PreferJpeg = 1,
    PreferRaw = 2
}
