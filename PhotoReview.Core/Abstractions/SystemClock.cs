using System.Diagnostics;

namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Triển khai mặc định của <see cref="IClock"/> dựa trên đồng hồ hệ điều hành.
/// </summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public long Timestamp => Stopwatch.GetTimestamp();
}
