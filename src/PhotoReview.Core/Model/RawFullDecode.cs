using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Full decode behavior for RAW images (Q-RAW-02).
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<RawFullDecode>))]
public enum RawFullDecode
{
    Never = 0,
    OnZoom = 1
}
