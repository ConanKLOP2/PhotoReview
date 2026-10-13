using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Interop.Shell;
using PhotoReview.Shell.Win32.Dialogs;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GÄ1: <c>IFileOpenDialog</c> / <c>IFileSaveDialog</c> qua <c>[GeneratedComInterface]</c>. Cáº¥u hÃ¬nh Ä‘á»c láº¡i Ä‘Æ°á»£c tá»« chÃ­nh há»™p
/// thoáº¡i (COM tháº­t, khÃ´ng hiá»‡n cá»­a sá»•); hiá»‡n tháº­t Ä‘Æ°á»£c Ä‘Ã³ng báº±ng luá»“ng Ä‘Ã³ng cá»­a sá»• (nhÆ° test thÆ° má»¥c) nÃªn tráº£ null = ngÆ°á»i dÃ¹ng huá»·.
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe class NativeShellFileDialogTests
{
    private static readonly FileDialogFilter[] Filters =
    [
        new("JSON (*.json)", "*.json"),
        new("Táº¥t cáº£ tá»‡p", "*.*"),
    ];

    [Fact]
    public void OpenDialog_Configure_SetsExistingFileFlags_FileTypeIndex_AndInitialFolder()
    {
        var folder = Path.GetTempPath();
        var (options, typeIndex, current) = OnSta(() =>
        {
            IFileOpenDialog dialog = ShellNative.CreateFileOpenDialog();
            ComdlgFilterSpec[] specs = [];
            try
            {
                specs = NativeShellFileDialog.Configure(dialog, "Chá»n tá»‡p", Filters, folder, NativeShellFileDialog.OpenOptions, null, null);
                ComInterop.Check(dialog.GetOptions(out FileDialogOptions flags));
                ComInterop.Check(dialog.GetFileTypeIndex(out uint index));
                ComInterop.Check(dialog.GetFolder(out nint raw));
                IShellItem item = ComInterop.Wrap<IShellItem>(raw);
                try
                {
                    ComInterop.Check(item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint name));
                    return (flags, index, ShellNative.TakeCoTaskString(name));
                }
                finally
                {
                    ComInterop.Release(item);
                }
            }
            finally
            {
                foreach (var spec in specs)
                {
                    Marshal.FreeCoTaskMem((nint)spec.Name);
                    Marshal.FreeCoTaskMem((nint)spec.Spec);
                }

                ComInterop.Release(dialog);
            }
        });

        Assert.True(options.HasFlag(FileDialogOptions.FileMustExist));
        Assert.True(options.HasFlag(FileDialogOptions.PathMustExist));
        Assert.True(options.HasFlag(FileDialogOptions.ForceFileSystem));
        Assert.False(options.HasFlag(FileDialogOptions.PickFolders));
        Assert.Equal(1u, typeIndex);
        Assert.Equal(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(current!), ignoreCase: true);
    }

    [Fact]
    public void SaveDialog_Configure_SetsOverwritePrompt_FileName_AndKeepsTheFirstFilter()
    {
        var (options, name) = OnSta(() =>
        {
            IFileSaveDialog dialog = ShellNative.CreateFileSaveDialog();
            ComdlgFilterSpec[] specs = [];
            try
            {
                specs = NativeShellFileDialog.Configure(dialog, "LÆ°u", Filters, null, NativeShellFileDialog.SaveOptions, ".json", "settings.json");
                ComInterop.Check(dialog.GetOptions(out FileDialogOptions flags));
                ComInterop.Check(dialog.GetFileName(out nint raw));
                return (flags, ShellNative.TakeCoTaskString(raw));
            }
            finally
            {
                foreach (var spec in specs)
                {
                    Marshal.FreeCoTaskMem((nint)spec.Name);
                    Marshal.FreeCoTaskMem((nint)spec.Spec);
                }

                ComInterop.Release(dialog);
            }
        });

        Assert.True(options.HasFlag(FileDialogOptions.OverwritePrompt));
        Assert.True(options.HasFlag(FileDialogOptions.ForceFileSystem));
        Assert.False(options.HasFlag(FileDialogOptions.FileMustExist));
        Assert.Equal("settings.json", name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Show_ReturnsNullWhenTheUserClosesTheWindow(bool save)
    {
        string? picked = "sentinel";
        using var finished = new ManualResetEventSlim();
        OnSta(() =>
        {
            var threadId = GetCurrentThreadId();
            var dialog = new NativeShellFileDialog(unused =>
            {
                _ = Task.Run(() =>
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline && !finished.Wait(100))
                    {
                        EnumThreadWindows(threadId, &CloseVisible, 0);
                    }
                });
            });
            picked = save
                ? dialog.PickSaveFile(0, "LÆ°u", Filters, ".json", "a.json", null)
                : dialog.PickOpenFile(0, "Má»Ÿ", Filters, null);
            finished.Set();
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
