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
        IProgress<ExplorerQueryProgress>? progress = null,
        int batchSize = 16,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// perf(startup): starts the snapshot query for <paramref name="folder"/> now, before anyone asks
    /// for it (App_Startup knows the folder long before the folder load starts). The next
    /// <see cref="TryGetSnapshotProgressiveAsync"/> for the same folder joins this query instead of
    /// queueing a second one; any other request cancels it. Default: no-op (test fakes).
    /// </summary>
    void Prefetch(string folder, TimeSpan timeout) { }
}
