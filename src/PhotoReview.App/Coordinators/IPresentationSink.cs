namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận kết quả trình diễn ảnh và cập nhật UI.
/// Tách rời ImagePresenter khỏi tầng UI / WPF theo quy tắc K-2.
/// </summary>
public interface IPresentationSink
{
    /// <summary>Cập nhật ảnh hiển thị chính (hoặc null khi ở chế độ Compare hoặc rỗng).</summary>
    void SetCurrentImage(object? image);

    /// <summary>Cập nhật nội dung thanh trạng thái.</summary>
    void SetStatusText(string status);

    /// <summary>Áp dụng chế độ xem ban đầu (Fit / Uniform / 100%).</summary>
    void ApplyInitialViewMode();

    /// <summary>Kích hoạt hook OnPresented seam T14a khi ảnh đã sẵn sàng hiển thị.</summary>
    void OnPresented(string path);

    /// <summary>Ghi nhận mốc đo Rendered và Presented khi khung hình hiển thị (hỗ trợ ETW/Perf).</summary>
    void TracePresented(long token, string kind, long assignedTimestamp);
}