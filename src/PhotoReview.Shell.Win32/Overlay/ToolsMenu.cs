using PhotoReview.App.Menus;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>
/// WP-23: lệnh của toolbar mà overlay gọi (cầu sang <c>MainViewModel</c>; test thay bằng bản giả). Mọi lệnh bất đồng bộ do
/// bên gọi bọc "guarded" (lỗi vào log + dòng trạng thái, như <c>RunGuardedAsync</c> của MainWindow).
/// </summary>
internal interface IOverlayCommands
{
    Task OpenFolderAsync();

    void ShowSettings();

    Task FitAsync();

    void ShowSkippedFiles();

    void ShowRecovery();

    void ShowDiagnostics();

    void ShowBenchmark();

    Task ClearCacheAsync();

    Task RemoveDuplicatesAsync(bool numbered);
}

/// <summary>
/// WP-23: nút ⋮ -&gt; menu Tools (C-12 <see cref="ContextMenuModelBuilder.BuildToolsMenu"/> + <c>IPopupMenuHost</c>) thay cho
/// <c>Popup</c> sáu nút của XAML. Id mục theo quy ước <c>Tools.*</c> của builder.
/// </summary>
internal static class ToolsMenu
{
    internal const string Recovery = "Tools.Recovery";
    internal const string Diagnostics = "Tools.Diagnostics";
    internal const string Benchmark = "Tools.Benchmark";
    internal const string ClearCache = "Tools.ClearCache";
    internal const string RemoveNumberedDuplicates = "Tools.RemoveNumberedDuplicates";
    internal const string RemoveOriginalDuplicates = "Tools.RemoveOriginalDuplicates";

    /// <summary>Mô hình menu (hai mục xoá trùng tắt khi chưa có ảnh).</summary>
    public static IReadOnlyList<MenuItemModel> Build(bool hasImages) => ContextMenuModelBuilder.BuildToolsMenu(hasImages);

    /// <summary>
    /// Chạy lệnh của mục <paramref name="id"/>. Trả Task của lệnh bất đồng bộ (hoặc đã hoàn thành cho lệnh đồng bộ), null nếu
    /// id lạ. Mục xoá trùng là no-op khi chưa có ảnh (khớp WPF: catalog rỗng).
    /// </summary>
    public static Task? Execute(string? id, IOverlayCommands commands, bool hasImages)
    {
        ArgumentNullException.ThrowIfNull(commands);
        switch (id)
        {
            case Recovery:
                commands.ShowRecovery();
                return Task.CompletedTask;
            case Diagnostics:
                commands.ShowDiagnostics();
                return Task.CompletedTask;
            case Benchmark:
                commands.ShowBenchmark();
                return Task.CompletedTask;
            case ClearCache:
                return commands.ClearCacheAsync();
            case RemoveNumberedDuplicates:
                return hasImages ? commands.RemoveDuplicatesAsync(true) : Task.CompletedTask;
            case RemoveOriginalDuplicates:
                return hasImages ? commands.RemoveDuplicatesAsync(false) : Task.CompletedTask;
            default:
                return null;
        }
    }
}
