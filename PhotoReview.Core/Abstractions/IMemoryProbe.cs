namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc kiểm tra áp lực bộ nhớ RAM để điều tiết preload và cache.
/// </summary>
public interface IMemoryProbe
{
    /// <summary>Trả về true nếu áp lực bộ nhớ RAM vượt quá ngưỡng an toàn.</summary>
    bool IsMemoryPressureHigh();

    /// <summary>Số byte RAM vật lý còn khả dụng của hệ thống.</summary>
    long GetAvailableMemoryBytes();
}
