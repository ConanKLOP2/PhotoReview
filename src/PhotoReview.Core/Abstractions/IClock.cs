namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa thời gian và bộ đếm thời gian hiệu năng cao.
/// </summary>
public interface IClock
{
    /// <summary>Thời gian hiện tại theo UTC.</summary>
    DateTime UtcNow { get; }
}
