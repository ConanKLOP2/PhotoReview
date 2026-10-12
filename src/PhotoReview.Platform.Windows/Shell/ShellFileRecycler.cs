using System.Runtime.InteropServices;

namespace PhotoReview.Platform.Windows.Shell;

/// <summary>
/// WP-33: gửi MỘT tệp vào Thùng rác qua <c>IFileOperation</c> (thay <c>Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(...,
/// OnlyErrorDialogs, SendToRecycleBin)</c>, vốn là <c>SHFileOperation</c> với <c>FOF_ALLOWUNDO | FOF_NOCONFIRMATION</c>). Cùng ngữ nghĩa:
/// hỏi xác nhận tắt, hộp thoại tiến trình tắt, hộp thoại LỖI vẫn hiện; huỷ giữa chừng KHÔNG ném (người gọi kiểm tệp đã rời nguồn).
/// Chỉ chạm một tệp của người gọi; không đụng nội dung Thùng rác có sẵn.
/// </summary>
internal static class ShellFileRecycler
{
    // IFileOperation::SetOperationFlags (FILEOP_FLAGS).
    internal const uint FofSilent = 0x0004;            // không hộp thoại tiến trình
    internal const uint FofNoConfirmation = 0x0010;    // không hỏi "bạn có chắc"
    internal const uint FofAllowUndo = 0x0040;         // đưa vào Thùng rác (giữ thông tin undo)
    internal const uint FofNoConfirmMkdir = 0x0200;    // như cờ VB cũ
    internal const uint FofxRecycleOnDelete = 0x00080000; // Win8+: xoá = chuyển vào Thùng rác khi có thể

    /// <summary>Cờ gửi vào Thùng rác. KHÔNG có <c>FOF_NOERRORUI</c>: lỗi vẫn hiện hộp thoại như bản VB (OnlyErrorDialogs).</summary>
    internal const uint RecycleFlags = FofAllowUndo | FofNoConfirmation | FofNoConfirmMkdir | FofSilent | FofxRecycleOnDelete;

    private const int HrFileNotFound = unchecked((int)0x80070002);
    private const int HrPathNotFound = unchecked((int)0x80070003);
    private const int HrAccessDenied = unchecked((int)0x80070005);
    private const int HrFilenameExcedRange = unchecked((int)0x800700CE);
    private const int HrCancelled = unchecked((int)0x800704C7);
    private const int HrAbort = unchecked((int)0x80004004);

    /// <summary>Đường thật: luồng STA riêng + <c>CLSID_FileOperation</c> + <c>SHCreateItemFromParsingName</c>.</summary>
    internal static void Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        ShellStaRunner.Run(() =>
        {
            Recycle(full, CreateOperation, CreateItem);
            return 0;
        });
    }

    /// <summary>Phần logic, kiểm được bằng fake <see cref="IFileOperation"/> / <see cref="IShellItem"/> (đối tượng managed).</summary>
    /// <param name="release">Test seam for the release of the two COM objects (default <see cref="ShellCom.Release"/>).</param>
    internal static void Recycle(string path, Func<IFileOperation> createOperation, Func<string, IShellItem> createItem,
        Action<object?>? release = null)
    {
        release ??= ShellCom.Release;
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            item = createItem(path);
            operation = createOperation();
            Throw(operation.SetOperationFlags(RecycleFlags), path);
            Throw(operation.DeleteItem(item, 0), path);
            Throw(operation.PerformOperations(), path);
        }
        finally
        {
            release(item);
            release(operation);
        }
    }

    /// <summary>
    /// HRESULT -> ngoại lệ cùng họ với bản VB cũ (<c>ThrowWinIOError</c>): FileNotFound / DirectoryNotFound / UnauthorizedAccess /
    /// PathTooLong, còn lại <see cref="IOException"/>. Huỷ (<c>ERROR_CANCELLED</c>, <c>E_ABORT</c>) KHÔNG ném.
    /// </summary>
    internal static void Throw(int hr, string path)
    {
        if (hr >= 0 || hr == HrCancelled || hr == HrAbort) return;
        var name = Path.GetFileName(path);
        throw hr switch
        {
            HrFileNotFound => new FileNotFoundException(Marshal.GetPInvokeErrorMessage(2), path),
            HrPathNotFound => new DirectoryNotFoundException(Marshal.GetPInvokeErrorMessage(3) + ": " + name),
            HrAccessDenied => new UnauthorizedAccessException(Marshal.GetPInvokeErrorMessage(5) + ": " + name),
            HrFilenameExcedRange => new PathTooLongException(Marshal.GetPInvokeErrorMessage(206) + ": " + name),
            _ => new IOException($"{Marshal.GetPInvokeErrorMessage(hr & 0xFFFF)} (0x{hr:X8}): {name}", hr),
        };
    }

    private static IFileOperation CreateOperation()
        => ShellCom.Wrap<IFileOperation>(ShellCom.CreateInstance(ShellIds.ClsidFileOperation, ShellIds.IidFileOperation));

    private static IShellItem CreateItem(string path)
    {
        var hr = ShellCom.SHCreateItemFromParsingName(path, 0, ShellIds.IidShellItem, out var pointer);
        Throw(hr, path);
        if (hr < 0 || pointer == 0) throw new IOException("The shell could not open the file: " + Path.GetFileName(path), hr);
        return ShellCom.Wrap<IShellItem>(pointer);
    }
}
