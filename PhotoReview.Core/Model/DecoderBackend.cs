using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Backend giải mã ảnh được sử dụng.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<DecoderBackend>))]
public enum DecoderBackend
{
    Wpf = 0,
    WicDirect = 1,
    TurboJpeg = 2
}
