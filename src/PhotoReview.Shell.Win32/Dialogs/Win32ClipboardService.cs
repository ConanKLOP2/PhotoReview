using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;

namespace PhotoReview.Shell.Win32.Dialogs;

/// <summary>Seam cho API clipboard Win32: test thay bằng fake để kiểm tra thử lại / dọn dẹp mà không chạm clipboard thật.</summary>
internal interface IClipboardApi
{
    bool Open(nint owner);

    void Close();

    /// <summary>Xoá clipboard rồi đặt CF_UNICODETEXT; false nếu thất bại (bộ nhớ không bị rò).</summary>
    bool SetUnicodeText(string text);
}

/// <summary>
/// Ghi văn bản vào clipboard bằng <c>OpenClipboard/SetClipboardData(CF_UNICODETEXT)</c> (WP-19a). Khi tiến trình khác đang
/// giữ clipboard thì thử lại 5 x 20 ms như bản WPF (<c>SetDataObject(copy: true)</c>); vẫn bận thì trả false, không ném.
/// </summary>
/// <remarks>
/// Cùng hình dạng với <c>PhotoReview.App.Services.IClipboardService</c> (<c>bool TrySetText(string)</c>); interface đó còn ở
/// project App tới khi WP-09 dời sang App.Shared - lúc đó lớp này chỉ cần khai báo thêm <c>: IClipboardService</c>.
/// </remarks>
internal sealed class Win32ClipboardService
{
    internal const int Attempts = 5;
    internal const int RetryDelayMilliseconds = 20;

    private readonly IClipboardApi _api;
    private readonly Func<nint> _ownerProvider;
    private readonly Action<int> _sleep;

    public Win32ClipboardService(Func<nint> ownerProvider)
        : this(new NativeClipboardApi(), ownerProvider, Thread.Sleep)
    {
    }

    internal Win32ClipboardService(IClipboardApi api, Func<nint> ownerProvider, Action<int> sleep)
    {
        _api = api;
        _ownerProvider = ownerProvider;
        _sleep = sleep;
    }

    /// <summary>True khi văn bản đã nằm trên clipboard; false khi clipboard vẫn bị chiếm hoặc không có cửa sổ chủ.</summary>
    public bool TrySetText(string text)
    {
        // Không có cửa sổ chủ thì EmptyClipboard đặt chủ = NULL và SetClipboardData sẽ thất bại.
        var owner = _ownerProvider();
        if (owner == 0) return false;

        try
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0) _sleep(RetryDelayMilliseconds);
                if (!_api.Open(owner)) continue;
                try
                {
                    return _api.SetUnicodeText(text);
                }
                finally
                {
                    _api.Close();
                }
            }
        }
        catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or InvalidOperationException)
        {
            return false;
        }

        return false;
    }
}

internal sealed unsafe class NativeClipboardApi : IClipboardApi
{
    public bool Open(nint owner) => User32.OpenClipboard(owner);

    public void Close() => User32.CloseClipboard();

    public bool SetUnicodeText(string text)
    {
        var bytes = (nuint)((text.Length + 1) * sizeof(char));
        var memory = Kernel32.GlobalAlloc(WindowMessages.GmemMoveable, bytes);
        if (memory == 0) return false;

        var ownedByClipboard = false;
        try
        {
            var target = Kernel32.GlobalLock(memory);
            if (target == 0) return false;
            try
            {
                text.AsSpan().CopyTo(new Span<char>((void*)target, text.Length));
                ((char*)target)[text.Length] = '\0';
            }
            finally
            {
                Kernel32.GlobalUnlock(memory);
            }

            if (!User32.EmptyClipboard()) return false;
            ownedByClipboard = User32.SetClipboardData(WindowMessages.CfUnicodeText, memory) != 0;
            return ownedByClipboard;
        }
        finally
        {
            // Sau SetClipboardData thành công, hệ thống sở hữu bộ nhớ; chỉ giải phóng khi chưa chuyển giao.
            if (!ownedByClipboard) Kernel32.GlobalFree(memory);
        }
    }
}
