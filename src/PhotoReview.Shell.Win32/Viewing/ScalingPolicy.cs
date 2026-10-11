using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP-21: chọn bộ lọc nội suy cho từng khung. Nền (NE-9 a, chưa xác nhận): <c>HighQuality</c> = cubic tổng hợp của D2D,
/// <c>Linear</c> = bilinear. Rủi ro đo ở WP-15: thu nhỏ ảnh 24 MP bằng cubic tốn 27-45 ms GPU mỗi khung, nên khi ảnh ĐANG
/// chuyển động (pan/zoom) mà phải thu nhỏ mạnh thì tạm dùng Linear (6-7 ms), rồi vẽ một khung cubic khi đã đứng yên
/// (<see cref="SettleMs"/>). Khung đứng yên, ảnh vừa thay, thu nhỏ nhẹ: luôn đúng bộ lọc người dùng chọn.
/// </summary>
internal static class ScalingPolicy
{
    /// <summary>Tỉ lệ diện tích (pixel nguồn / pixel đích trên màn hình) từ đó thu nhỏ cubic bị coi là nặng.</summary>
    internal const double HeavyDownscaleAreaRatio = 4.0;

    /// <summary>Đứng yên chừng này thì vẽ lại bằng bộ lọc chất lượng cao.</summary>
    internal const int SettleMs = 120;

    /// <summary>Bộ lọc dùng cho khung: <paramref name="requested"/> trừ khi nó là cubic, ảnh đang chuyển động và thu nhỏ nặng.</summary>
    internal static ImageInterpolation Choose(ImageInterpolation requested, int sourcePixelWidth, int sourcePixelHeight,
        double destinationPixelWidth, double destinationPixelHeight, bool inMotion)
    {
        if (requested != ImageInterpolation.HighQualityCubic || !inMotion)
        {
            return requested;
        }

        return IsHeavyDownscale(sourcePixelWidth, sourcePixelHeight, destinationPixelWidth, destinationPixelHeight)
            ? ImageInterpolation.Linear
            : requested;
    }

    /// <summary>True khi diện tích nguồn lớn hơn <see cref="HeavyDownscaleAreaRatio"/> lần diện tích đích.</summary>
    internal static bool IsHeavyDownscale(int sourcePixelWidth, int sourcePixelHeight, double destinationPixelWidth,
        double destinationPixelHeight)
    {
        if (sourcePixelWidth <= 0 || sourcePixelHeight <= 0 || destinationPixelWidth <= 0 || destinationPixelHeight <= 0
            || double.IsNaN(destinationPixelWidth) || double.IsNaN(destinationPixelHeight))
        {
            return false;
        }

        var ratio = (double)sourcePixelWidth * sourcePixelHeight / (destinationPixelWidth * destinationPixelHeight);
        return ratio > HeavyDownscaleAreaRatio;
    }
}
