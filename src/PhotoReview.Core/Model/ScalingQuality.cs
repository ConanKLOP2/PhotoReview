using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chất lượng nội suy / thu phóng ảnh.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<ScalingQuality>))]
public enum ScalingQuality
{
    Linear = 0,
    HighQuality = 1
}
