namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Trừu tượng hóa việc điều phối thực thi tác vụ lên luồng giao diện UI.
/// </summary>
public interface IUiScheduler
{
    /// <summary>Đẩy một hành động lên hàng đợi UI (bất đồng bộ, không đợi).</summary>
    void Post(Action action);

    /// <summary>Thực thi một hành động trên luồng UI và đợi hoàn thành.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Nhường quyền điều khiển bất đồng bộ cho UI xử lý.</summary>
    ValueTask YieldAsync(CancellationToken cancellationToken = default);
}
