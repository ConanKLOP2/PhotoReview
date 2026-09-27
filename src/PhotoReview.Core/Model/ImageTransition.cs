using System.Text.Json.Serialization;

namespace PhotoReview.Core.Model;

/// <summary>
/// Hiệu ứng chuyển cảnh khi ảnh ĐANG XEM thay đổi (điều hướng sang file khác). Không áp dụng cho các lần
/// nâng cấp cùng một ảnh (thumbnail -> preview -> gốc), luôn phải là thay thế tức thời như hiện tại.
/// </summary>
[JsonConverter(typeof(LenientEnumConverter<ImageTransition>))]
public enum ImageTransition
{
    /// <summary>Không hiệu ứng: cắt cứng (mặc định, chi phí bằng 0).</summary>
    None = 0,

    /// <summary>Ảnh cũ mờ dần (fade out) trong khi ảnh mới đã hiển thị bên dưới, không có khoảng tối.</summary>
    Fade = 1
}
