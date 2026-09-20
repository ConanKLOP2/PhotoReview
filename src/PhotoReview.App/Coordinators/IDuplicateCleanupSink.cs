using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện khi xử lý trùng lặp và xóa cache.
/// </summary>
public interface IDuplicateCleanupSink
{
    /// <summary>Cập nhật thông báo trạng thái cho người dùng.</summary>
    void SetStatusText(string status);

    /// <summary>Yêu cầu mở thư mục ảnh.</summary>
    Task OpenFolderAsync(string folder, string? initialPath = null);

    /// <summary>Thông báo cần cập nhật trạng thái điều hướng (button states, v.v).</summary>
    void NotifyNavigationStateChanged();
}
