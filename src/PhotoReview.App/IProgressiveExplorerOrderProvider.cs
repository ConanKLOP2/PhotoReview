using System;
using System.Threading;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Platform.Windows;

namespace PhotoReview.App;

/// <summary>
/// Giao diện cung cấp ảnh theo thứ tự hiển thị của Explorer hỗ trợ tiến trình (Progressive).
/// </summary>
public interface IProgressiveExplorerOrderProvider : IExplorerOrderProvider
{
}

/// <summary>
/// Provider sản xuất: chuyển tiếp cuộc gọi tới ExplorerOrderService thực tế.
/// </summary>
public sealed class ExplorerOrderProviderAdapter : IProgressiveExplorerOrderProvider
{
    private readonly ExplorerOrderService _service = new();

    public Task<ExplorerViewSnapshot> TryGetSnapshotAsync(
        string folder,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => _service.TryGetSnapshotAsync(folder, timeout, cancellationToken);

    public Task<ExplorerViewSnapshot> TryGetSnapshotProgressiveAsync(
        string folder,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IProgress<ExplorerQueryProgress>? progress = null,
        int batchSize = 16)
        => _service.TryGetSnapshotProgressiveAsync(folder, timeout, cancellationToken, progress, batchSize);

    public void Dispose() => _service.Dispose();
}
