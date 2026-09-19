using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Chế độ tải ảnh khi hiển thị hoặc duyệt thư mục.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<LoadingMode>))]
public enum LoadingMode
{
    Fast = 0,
    Preview = 1,
    Original = 2
}
