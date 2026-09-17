namespace PhotoReview.Core.Abstractions;

public readonly record struct MemorySnapshot(uint LoadPercent, ulong AvailableBytes);

/// <summary>
/// Trừu tượng hóa việc kiểm tra áp lực bộ nhớ RAM để điều tiết preload và cache.
/// </summary>
public interface IMemoryProbe
{
    /// <summary>Trả về true nếu bộ nhớ còn đủ không gian an toàn (load < maximumLoad và available >= reserveBytes).</summary>
    bool HasHeadroom(double maximumLoad, long reserveBytes);

    /// <summary>Lấy snapshot trạng thái bộ nhớ hiện tại.</summary>
    MemorySnapshot? GetSnapshot();

    /// <summary>Trả về true nếu áp lực bộ nhớ RAM vượt quá ngưỡng an toàn.</summary>
    bool IsMemoryPressureHigh();

    /// <summary>Số byte RAM vật lý còn khả dụng của hệ thống.</summary>
    long GetAvailableMemoryBytes();
}
