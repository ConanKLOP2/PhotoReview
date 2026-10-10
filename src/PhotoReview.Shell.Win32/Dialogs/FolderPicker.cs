using System.IO;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// Seam cho hộp thoại chọn thư mục của shell (<c>IFileOpenDialog</c> + <c>FOS_PICKFOLDERS</c>). COM của
/// <c>IFileOpenDialog</c> do WP-13b khai báo trong Shell.Interop; lớp thực thi native nối vào đây ở bước lắp ráp (WP-20).
/// </summary>
internal interface IShellFolderDialog
{
    /// <summary>Hiện hộp thoại modal; trả đường dẫn đã chọn hoặc null khi huỷ.</summary>
    string? Show(nint owner, string title, string? initialFolder);
}

/// <summary>
/// Chọn thư mục: quyết định (thư mục khởi đầu hợp lệ, bắt lỗi COM) nằm ở đây và test được; việc mở UI thật nằm sau
/// <see cref="IShellFolderDialog"/>. Giữ hành vi <c>WpfFolderPicker</c>: thư mục khởi đầu chỉ dùng khi tồn tại.
/// </summary>
internal sealed class FolderPicker
{
    private readonly IShellFolderDialog _dialog;
    private readonly Func<nint> _ownerProvider;

    public FolderPicker(IShellFolderDialog dialog, Func<nint> ownerProvider)
    {
        _dialog = dialog;
        _ownerProvider = ownerProvider;
    }

    /// <summary>Thư mục khởi đầu chỉ khi tồn tại trên đĩa; trống/thiếu thì để hộp thoại tự chọn.</summary>
    internal static string? UsableInitialFolder(string? initialFolder)
        => !string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder) ? initialFolder : null;

    public string? Pick(string title, string? initialFolder)
        => _dialog.Show(_ownerProvider(), title, UsableInitialFolder(initialFolder));
}

/// <summary>Chưa nối COM thật (chờ WP-13b): không hiện gì, trả null như người dùng huỷ.</summary>
internal sealed class UnwiredShellFolderDialog : IShellFolderDialog
{
    public static readonly UnwiredShellFolderDialog Instance = new();

    public string? Show(nint owner, string title, string? initialFolder) => null;
}
