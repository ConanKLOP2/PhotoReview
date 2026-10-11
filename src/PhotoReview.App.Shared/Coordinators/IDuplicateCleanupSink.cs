
namespace PhotoReview.App.Coordinators;

/// <summary>
/// Giao diện tiếp nhận sự kiện và cập nhật giao diện khi xử lý trùng lặp và xóa cache.
/// </summary>
public interface IDuplicateCleanupSink
{
    /// <summary>Cập nhật thông báo trạng thái cho người dùng.</summary>
    void SetStatusText(string status);

    /// <summary>
    /// RV-A10: a batch that finished after the user had left its folder. Must not overwrite the new folder's own status
    /// (the view model drops it unless the status line is idle). The default writes it as a plain status.
    /// </summary>
    void ShowLateActionStatus(string status) => SetStatusText(status);

    /// <summary>Yêu cầu mở thư mục ảnh.</summary>
    Task OpenFolderAsync(string folder, string? initialPath = null);

    /// <summary>
    /// Reloads <paramref name="folder"/> after a batch. Returns false when the user (or a forwarded open) started another
    /// folder load while this one ran: the batch result then belongs to a folder that is no longer shown (D-03). The default
    /// has no way to tell and reports true.
    /// </summary>
    async Task<bool> ReloadFolderAsync(string folder)
    {
        await OpenFolderAsync(folder);
        return true;
    }
}
