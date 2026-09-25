using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện trong quá trình tải thư mục ảnh.
/// </summary>
public interface IFolderLoadSink
{
    /// <summary>Xóa bộ nhớ đệm hình ảnh và scheduler của thư mục trước.</summary>
    void ResetCaches();

    /// <summary>Thông báo danh mục ảnh sơ bộ đã sẵn sàng. <paramref name="session"/> là phiên đã nạp đúng một lần bởi coordinator.</summary>
    void OnCatalogReady(string folder, int count, PhotoReview.Core.Session.SessionState session);

    /// <summary>Yêu cầu hiển thị ảnh tại vị trí chỉ định.</summary>
    Task PresentAsync(int index, long presentationGeneration);

    /// <summary>Thông báo thư mục rỗng không có ảnh hỗ trợ.</summary>
    void OnEmpty(string folder, PhotoReview.Core.Session.SessionState session);

    /// <summary>
    /// Thông báo thứ tự Explorer tự nhiên đã được áp dụng vào danh mục. <paramref name="currentKept"/>:
    /// the current image stays on screen at its new <paramref name="currentIndex"/> (no re-present
    /// follows), so anything positioned around the old index (preload) must re-center.
    /// </summary>
    void OnOrderApplied(int count, int currentIndex, bool currentKept);

    /// <summary>
    /// IO05: một số tệp/mục không đọc được đã bị bỏ qua khi quét (gọi ngay sau <see cref="OnCatalogReady"/>,
    /// chỉ khi có mục bị bỏ qua). Mặc định không làm gì để các sink thử nghiệm không phải cài đặt.
    /// </summary>
    void OnFilesSkipped(string folder, IReadOnlyList<PhotoReview.Core.Abstractions.SkippedEntry> skipped) { }

    /// <summary>Thông báo nạp thư mục thất bại.</summary>
    void OnFailed(string folder, Exception exception);
}
