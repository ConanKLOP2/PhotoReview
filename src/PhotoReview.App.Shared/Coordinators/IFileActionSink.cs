using PhotoReview.Core.Model;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện khi thực hiện thao tác file (Move, Copy, Recycle, Undo).
/// </summary>
public interface IFileActionSink
{
    /// <summary>Cập nhật thông báo trạng thái cho người dùng.</summary>
    void SetStatusText(string status);

    /// <summary>
    /// APP-03: hint for a file action that finished after the folder changed. Unlike <see cref="SetStatusText"/> it must
    /// not replace text the current folder already shows (a load in progress, an error), so the sink may drop it.
    /// </summary>
    void ShowLateActionStatus(string status);

    /// <summary>
    /// Thông báo danh mục đã thay đổi (item bị xóa/khôi phục). <paramref name="removedPath"/> là ảnh vừa bị gỡ
    /// (chỉ ảnh này bị loại khỏi cache; ảnh kế tiếp phải giữ nguyên trong RAM), hoặc null nếu không có ảnh bị gỡ.
    /// </summary>
    void OnCatalogChanged(string? removedPath);

    /// <summary>
    /// The other members (JPEG/RAW partner) of a capture that just left the catalog together with the path passed to
    /// <see cref="OnCatalogChanged"/>: their cached previews / preloaded keys must go too, like the removed file's.
    /// </summary>
    void EvictCachedPaths(IReadOnlyList<string> paths) { }

    /// <summary>
    /// FEAT-RENAME: files of the folder were renamed in place and the catalog already lists the new names
    /// (<paramref name="renames"/>: old path -> new path, every member of a capture; empty after a rename attempt that
    /// changed nothing, where it only re-arms what the action's StopForAction stopped). The sink re-keys the session
    /// (current path, skipped list), drops the old names' cached previews and lets the presenter keep the shown image under
    /// the new name WITHOUT presenting again (zoom/pan stay).
    /// </summary>
    /// <returns>True when the shown image was kept in place; false when the caller must present the current entry again.</returns>
    bool OnFilesRenamed(IReadOnlyDictionary<string, string> renames) => false;

    /// <summary>Yêu cầu hiển thị ảnh tại vị trí chỉ định.</summary>
    Task PresentAsync(int index);

    /// <summary>Cập nhật session sau khi thao tác thành công (lưu đường dẫn hiện tại).</summary>
    void UpdateSessionPath(string currentPath);

    /// <summary>Thông báo cần cập nhật trạng thái điều hướng (undo history, button states, v.v).</summary>
    void NotifyNavigationStateChanged();
}
