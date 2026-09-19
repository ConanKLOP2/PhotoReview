using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện trong quá trình tải thư mục ảnh.
/// </summary>
public interface IFolderLoadSink
{
    /// <summary>Xóa bộ nhớ đệm hình ảnh và scheduler của thư mục trước.</summary>
    void ResetCaches();

    /// <summary>Thông báo danh mục ảnh sơ bộ đã sẵn sàng.</summary>
    void OnCatalogReady(string folder, int count);

    /// <summary>Yêu cầu hiển thị ảnh tại vị trí chỉ định.</summary>
    Task PresentAsync(int index, long presentationGeneration);

    /// <summary>Thông báo thư mục rỗng không có ảnh hỗ trợ.</summary>
    void OnEmpty(string folder);

    /// <summary>Thông báo thứ tự Explorer tự nhiên đã được áp dụng vào danh mục.</summary>
    void OnOrderApplied(int count, int currentIndex);

    /// <summary>Thông báo nạp thư mục thất bại.</summary>
    void OnFailed(string folder, Exception exception);
}
