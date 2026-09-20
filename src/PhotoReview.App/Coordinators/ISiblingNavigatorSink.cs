using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện khi điều hướng thư mục anh em (sibling).
/// </summary>
public interface ISiblingNavigatorSink
{
    /// <summary>Cập nhật thông báo trạng thái cho người dùng.</summary>
    void SetStatusText(string status);

    /// <summary>Yêu cầu mở thư mục ảnh.</summary>
    Task OpenFolderAsync(string folder, string? initialPath = null);

    /// <summary>Thông báo cần cập nhật trạng thái điều hướng (button states, v.v).</summary>
    void NotifyNavigationStateChanged();
}
