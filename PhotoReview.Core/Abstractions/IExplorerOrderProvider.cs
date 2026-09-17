using PhotoReview.Core.Catalog;

namespace PhotoReview.Core.Abstractions;

/// <summary>
/// Cung cấp thứ tự sắp xếp ảnh thực tế từ cửa sổ Windows Explorer đang mở.
/// </summary>
public interface IExplorerOrderProvider : IDisposable
{
    /// <summary>Lấy snapshot thứ tự Explorer với timeout.</summary>
    Task<ExplorerViewSnapshot> TryGetSnapshotAsync(
        string folder,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Lấy snapshot thứ tự Explorer với hỗ trợ progressive progress báo cáo tiến độ từng phần.</summary>
    Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
        string folder,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IProgress<ExplorerQueryProgress>? progress = null,
        int batchSize = 16);
}
