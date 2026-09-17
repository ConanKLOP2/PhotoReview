using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chế độ sắp xếp danh sách ảnh.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<ImageSortMode>))]
public enum ImageSortMode
{
    [JsonAlias("PortraitFirst")]
    Name = 0,

    [JsonAlias("Size")]
    SizeDescending = 1,

    SizeAscending = 2
}
