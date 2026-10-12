using Microsoft.Extensions.DependencyInjection;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Win32;

/// <summary>WP-23: đăng ký của gói overlay trong <c>ShellCompositionRoot.RegisterFeatures</c> (một dòng).</summary>
internal static class ShellOverlayServiceCollectionExtensions
{
    /// <summary>
    /// Nối hộp thoại chọn thư mục native (<see cref="NativeShellFolderDialog"/>) cho <c>FolderPicker</c> (thay bản chưa nối của
    /// WP-19a) - dùng bởi nút 📂, menu chuột phải "Mở thư mục" và "Move/Copy to folder".
    /// </summary>
    public static IServiceCollection AddShellOverlay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IShellFolderDialog, NativeShellFolderDialog>();
        return services;
    }
}
