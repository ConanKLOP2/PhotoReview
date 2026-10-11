using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Interop.Shell;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>
/// WP-23: hộp thoại chọn thư mục native (<c>IFileOpenDialog</c> với <c>FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM |
/// FOS_PATHMUSTEXIST</c>, giống <c>OpenFolderDialog</c> của WPF) thay <see cref="UnwiredShellFolderDialog"/>. Modal, chạy trên
/// luồng STA của cửa sổ chủ (bơm thông điệp của chính hộp thoại). Huỷ -&gt; null; lỗi thật -&gt; <see cref="COMException"/>
/// (người gọi bọc "guarded"). COM wrapper được trả ngay khi xong, không chờ finalizer.
/// </summary>
internal sealed class NativeShellFolderDialog : IShellFolderDialog
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): người dùng đóng hộp thoại.</summary>
    internal const int HResultCancelled = unchecked((int)0x800704C7);

    /// <summary>Cờ của hộp thoại chọn thư mục.</summary>
    internal const FileDialogOptions FolderOptions =
        FileDialogOptions.PickFolders | FileDialogOptions.ForceFileSystem | FileDialogOptions.PathMustExist;

    private readonly Action<IFileOpenDialog>? _beforeShow;

    public NativeShellFolderDialog()
        : this(null)
    {
    }

    /// <param name="beforeShow">Test: gọi sau khi cấu hình, ngay trước <c>Show</c> (đặt hẹn giờ đóng hộp thoại).</param>
    internal NativeShellFolderDialog(Action<IFileOpenDialog>? beforeShow)
    {
        _beforeShow = beforeShow;
    }

    public string? Show(nint owner, string title, string? initialFolder)
    {
        IFileOpenDialog dialog = ShellNative.CreateFileOpenDialog();
        try
        {
            Configure(dialog, title, initialFolder);
            _beforeShow?.Invoke(dialog);
            int hr = dialog.Show(owner);
            if (hr == HResultCancelled)
            {
                return null;
            }

            ComInterop.Check(hr);
            return ReadResult(dialog);
        }
        finally
        {
            ComInterop.Release(dialog);
        }
    }

    /// <summary>Đặt tuỳ chọn, tiêu đề và thư mục khởi đầu (chỉ khi truyền vào).</summary>
    internal static void Configure(IFileOpenDialog dialog, string title, string? initialFolder)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ComInterop.Check(dialog.GetOptions(out FileDialogOptions current));
        ComInterop.Check(dialog.SetOptions(current | FolderOptions));
        if (!string.IsNullOrEmpty(title))
        {
            ComInterop.Check(dialog.SetTitle(title));
        }

        if (!string.IsNullOrEmpty(initialFolder))
        {
            IShellItem folder = ShellNative.CreateItemFromPath(initialFolder);
            try
            {
                ComInterop.Check(dialog.SetFolder(folder));
            }
            finally
            {
                ComInterop.Release(folder);
            }
        }
    }

    private static string? ReadResult(IFileOpenDialog dialog)
    {
        ComInterop.Check(dialog.GetResult(out nint raw));
        IShellItem item = ComInterop.Wrap<IShellItem>(raw);
        try
        {
            ComInterop.Check(item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint name));
            return ShellNative.TakeCoTaskString(name);
        }
        finally
        {
            ComInterop.Release(item);
        }
    }
}
