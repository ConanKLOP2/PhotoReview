using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Interop.Shell;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Tests.Menus;

/// <summary>
/// WP-23: hộp thoại chọn thư mục native (IFileOpenDialog). Cấu hình đọc lại được từ chính hộp thoại; hiện thật được đóng bằng
/// timer luồng (WM_TIMER của vòng modal) gọi <c>IFileDialog::Close</c> - trả null như người dùng huỷ.
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe class NativeShellFolderDialogTests
{
    private static T RunSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA thread không kết thúc");
        if (error is not null)
        {
            throw new InvalidOperationException("STA body failed", error);
        }

        return result;
    }

    [Fact]
    public void Configure_SetsFolderPickingFlags_Title_AndInitialFolder()
    {
        string folder = Path.GetTempPath();
        (FileDialogOptions options, string? currentFolder) = RunSta(() =>
        {
            IFileOpenDialog dialog = ShellNative.CreateFileOpenDialog();
            try
            {
                NativeShellFolderDialog.Configure(dialog, "Chọn thư mục ảnh", folder);
                ComInterop.Check(dialog.GetOptions(out FileDialogOptions flags));
                ComInterop.Check(dialog.GetFolder(out nint raw));
                IShellItem item = ComInterop.Wrap<IShellItem>(raw);
                try
                {
                    ComInterop.Check(item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint name));
                    return (flags, ShellNative.TakeCoTaskString(name));
                }
                finally
                {
                    ComInterop.Release(item);
                }
            }
            finally
            {
                ComInterop.Release(dialog);
            }
        });

        Assert.True(options.HasFlag(FileDialogOptions.PickFolders));
        Assert.True(options.HasFlag(FileDialogOptions.ForceFileSystem));
        Assert.True(options.HasFlag(FileDialogOptions.PathMustExist));
        Assert.Equal(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(currentFolder!), ignoreCase: true);
    }

    [Fact]
    public void Show_ReturnsNullWhenTheDialogWindowIsClosed_ReturnsOnlyAfterItWasShown()
    {
        string? picked = "sentinel";
        bool closedWindow = false;
        RunSta(() =>
        {
            uint threadId = GetCurrentThreadId();
            var dialog = new NativeShellFolderDialog(unused =>
            {
                // Luồng khác đóng cửa sổ hộp thoại của luồng STA này (WM_CLOSE): Show trả ERROR_CANCELLED.
                _ = Task.Run(() =>
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline && !Volatile.Read(ref closedWindow))
                    {
                        Thread.Sleep(100);
                        EnumThreadWindows(threadId, &CloseVisible, 0);
                    }
                });
            });
            picked = dialog.Show(0, "Chọn thư mục", null);
            Volatile.Write(ref closedWindow, true);
            return 0;
        });

        Assert.Null(picked);
    }

    [UnmanagedCallersOnly]
    private static int CloseVisible(nint hwnd, nint param)
    {
        if (IsWindowVisible(hwnd))
        {
            PostMessage(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);
        }

        return 1;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, delegate* unmanaged<nint, nint, int> callback, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
