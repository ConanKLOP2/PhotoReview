using System;
using System.Threading.Tasks;
using PhotoReview.Core.Abstractions;

namespace PhotoReview.App;

/// <summary>
/// T14a test seam: các hook phục vụ kiểm thử chạy trên host STA của MainWindow.
/// </summary>
internal sealed class MainWindowTestHooks
{
    /// <summary>Nguồn cung cấp thứ tự Explorer thay thế (INV-7, INV-9).</summary>
    public IProgressiveExplorerOrderProvider? Explorer { get; init; }

    /// <summary>Xử lý thùng rác thay thế.</summary>
    public IRecycleBin? RecycleBin { get; init; }

    /// <summary>Được kích hoạt khi ảnh vừa hoàn tất hiển thị ra màn hình.</summary>
    public Action<string>? OnPresented { get; init; }

    /// <summary>Thay thế bước di chuyển tệp MoveFileAsync; (source, destination).</summary>
    public Func<string, string, Task>? MoveOverride { get; init; }
}
