using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Interop.Shell;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>Một dòng bộ lọc của hộp thoại tệp: tên hiển thị + mẫu (ví dụ <c>*.json</c>, nhiều mẫu cách nhau <c>;</c>).</summary>
internal sealed record FileDialogFilter(string Name, string Pattern);

/// <summary>
/// WP-35 GĐ1: seam chọn tệp/lưu tệp (C-13 chỉ có chọn thư mục). Dành cho Import/Export Settings và JSON action ở các giai đoạn sau;
/// hiện chưa có chỗ gọi trong shell nên chỉ đăng ký trong DI và được test.
/// </summary>
internal interface IShellFileDialog
{
    /// <summary>Chọn một tệp có sẵn; null khi huỷ.</summary>
    string? PickOpenFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder);

    /// <summary>Chọn đường dẫn để lưu (hỏi ghi đè); null khi huỷ.</summary>
    string? PickSaveFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? defaultExtension,
        string? suggestedFileName, string? initialFolder);
}

/// <summary>
/// <see cref="IShellFileDialog"/> thật: <c>IFileOpenDialog</c> (FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM) /
/// <c>IFileSaveDialog</c> (FOS_OVERWRITEPROMPT | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM) qua <c>[GeneratedComInterface]</c>
/// (AOT-sạch). Modal trên luồng STA của cửa sổ chủ; huỷ -&gt; null; lỗi thật -&gt; <see cref="COMException"/>. COM wrapper trả ngay
/// khi xong (không chờ finalizer); chuỗi bộ lọc là bộ nhớ COM được giải phóng sau khi hộp thoại đóng.
/// </summary>
internal sealed unsafe class NativeShellFileDialog : IShellFileDialog
{
    internal const FileDialogOptions OpenOptions =
        FileDialogOptions.FileMustExist | FileDialogOptions.PathMustExist | FileDialogOptions.ForceFileSystem;

    internal const FileDialogOptions SaveOptions =
        FileDialogOptions.OverwritePrompt | FileDialogOptions.PathMustExist | FileDialogOptions.ForceFileSystem
        | FileDialogOptions.NoReadOnlyReturn;

    private readonly Action<IFileDialog>? _beforeShow;

    public NativeShellFileDialog()
        : this(null)
    {
    }

    /// <param name="beforeShow">Test: gọi sau khi cấu hình, ngay trước <c>Show</c> (hẹn giờ đóng hộp thoại).</param>
    internal NativeShellFileDialog(Action<IFileDialog>? beforeShow)
    {
        _beforeShow = beforeShow;
    }

    public string? PickOpenFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder)
    {
        IFileOpenDialog dialog = ShellNative.CreateFileOpenDialog();
        try
        {
            return Run(dialog, owner, title, filters, initialFolder, OpenOptions, defaultExtension: null, suggestedFileName: null);
        }
        finally
        {
            ComInterop.Release(dialog);
        }
    }

    public string? PickSaveFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? defaultExtension,
        string? suggestedFileName, string? initialFolder)
    {
        IFileSaveDialog dialog = ShellNative.CreateFileSaveDialog();
        try
        {
            return Run(dialog, owner, title, filters, initialFolder, SaveOptions, defaultExtension, suggestedFileName);
        }
        finally
        {
            ComInterop.Release(dialog);
        }
    }

    private string? Run(IFileDialog dialog, nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder,
        FileDialogOptions options, string? defaultExtension, string? suggestedFileName)
    {
        var specs = Configure(dialog, title, filters, initialFolder, options, defaultExtension, suggestedFileName);
        try
        {
            _beforeShow?.Invoke(dialog);
            var hr = dialog.Show(owner);
            if (hr == NativeShellFolderDialog.HResultCancelled)
            {
                return null;
            }

            ComInterop.Check(hr);
            return ReadResult(dialog);
        }
        finally
        {
            FreeSpecs(specs);
        }
    }

    /// <summary>Đặt tuỳ chọn, tiêu đề, bộ lọc, đuôi mặc định, tên gợi ý, thư mục khởi đầu. Trả bộ nhớ bộ lọc cần giải phóng.</summary>
    internal static ComdlgFilterSpec[] Configure(IFileDialog dialog, string title, IReadOnlyList<FileDialogFilter> filters,
        string? initialFolder, FileDialogOptions options, string? defaultExtension, string? suggestedFileName)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(filters);
        ComInterop.Check(dialog.GetOptions(out FileDialogOptions current));
        ComInterop.Check(dialog.SetOptions(current | options));
        if (!string.IsNullOrEmpty(title))
        {
            ComInterop.Check(dialog.SetTitle(title));
        }

        ComdlgFilterSpec[] specs = [];
        if (filters.Count > 0)
        {
            specs = new ComdlgFilterSpec[filters.Count];
            for (var i = 0; i < filters.Count; i++)
            {
                specs[i].Name = (char*)Marshal.StringToCoTaskMemUni(filters[i].Name);
                specs[i].Spec = (char*)Marshal.StringToCoTaskMemUni(filters[i].Pattern);
            }

            fixed (ComdlgFilterSpec* first = specs)
            {
                ComInterop.Check(dialog.SetFileTypes((uint)specs.Length, first));
            }

            ComInterop.Check(dialog.SetFileTypeIndex(1));
        }

        if (!string.IsNullOrEmpty(defaultExtension))
        {
            ComInterop.Check(dialog.SetDefaultExtension(defaultExtension.TrimStart('.')));
        }

        if (!string.IsNullOrEmpty(suggestedFileName))
        {
            ComInterop.Check(dialog.SetFileName(suggestedFileName));
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

        return specs;
    }

    private static void FreeSpecs(ComdlgFilterSpec[] specs)
    {
        foreach (var spec in specs)
        {
            Marshal.FreeCoTaskMem((nint)spec.Name);
            Marshal.FreeCoTaskMem((nint)spec.Spec);
        }
    }

    private static string? ReadResult(IFileDialog dialog)
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
