using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chế độ hiển thị ban đầu khi mở một ảnh.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<InitialViewMode>))]
public enum InitialViewMode
{
    Fit = 0,

    [JsonAlias("100%")]
    Percent100 = 1,

    [JsonAlias("200%")]
    Percent200 = 2,

    [JsonAlias("400%")]
    Percent400 = 3
}
