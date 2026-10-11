using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Shell;

namespace PhotoReview.Shell.Win32.Input;

/// <summary>
/// WP-22: nguồn danh sách file của một <see cref="IDataObject"/> kéo-thả (CF_HDROP). Tách khỏi <see cref="ShellDropTarget"/> để
/// test dùng data object giả mà không cần HGLOBAL thật.
/// </summary>
internal static unsafe class DropData
{
    /// <summary><c>DROPFILES.pFiles</c> nằm ở byte 0, <c>fWide</c> ở byte 16; danh sách kết thúc bằng NUL kép.</summary>
    private const int FWideOffset = 16;

    /// <summary>true khi data object mang CF_HDROP trên HGLOBAL (<c>e.Data.GetDataPresent(DataFormats.FileDrop)</c>).</summary>
    public static bool HasFiles(IDataObject data)
    {
        var format = HDropFormat();
        return data.QueryGetData(&format) == 0;
    }

    /// <summary>Danh sách đường dẫn (theo thứ tự trong data object); rỗng khi không có CF_HDROP.</summary>
    public static IReadOnlyList<string> Files(IDataObject data)
    {
        var format = HDropFormat();
        StgMedium medium = default;
        if (data.GetData(&format, &medium) != 0)
        {
            return [];
        }

        try
        {
            if (medium.Medium != Tymed.HGlobal || medium.Handle == 0)
            {
                return [];
            }

            var memory = Kernel32.GlobalLock(medium.Handle);
            if (memory == 0)
            {
                return [];
            }

            try
            {
                return Parse((byte*)memory);
            }
            finally
            {
                Kernel32.GlobalUnlock(medium.Handle);
            }
        }
        finally
        {
            OleNative.ReleaseStgMedium(&medium);
        }
    }

    /// <summary>Đọc <c>DROPFILES</c> + danh sách chuỗi (UTF-16 hoặc ANSI theo <c>fWide</c>).</summary>
    internal static List<string> Parse(byte* block)
    {
        var offset = *(uint*)block;
        var wide = *(int*)(block + FWideOffset) != 0;
        var files = new List<string>();
        var cursor = block + offset;
        if (wide)
        {
            var text = (char*)cursor;
            while (*text != '\0')
            {
                var item = new string(text);
                files.Add(item);
                text += item.Length + 1;
            }
        }
        else
        {
            var text = (sbyte*)cursor;
            while (*text != 0)
            {
                var item = new string(text);
                files.Add(item);
                text += item.Length + 1;
            }
        }

        return files;
    }

    private static FormatEtc HDropFormat() => new()
    {
        ClipboardFormat = ClipboardFormats.HDrop,
        TargetDevice = 0,
        Aspect = DataViewAspect.Content,
        Index = -1,
        Medium = Tymed.HGlobal,
    };
}

/// <summary>
/// WP-22: kéo-thả file vào cửa sổ (<c>AllowDrop</c> + <c>Window_PreviewDragOver</c>/<c>Window_Drop</c> của WPF) bằng
/// <c>IDropTarget</c> thật: có file thì hiệu ứng Copy (con trỏ kéo hiện dấu +), không thì None; thả = mở file ĐẦU TIÊN.
/// OLE được khởi tạo trên UI thread (STA); <c>RegisterDragDrop</c> giữ một ref tới đối tượng cho tới <see cref="Dispose"/>.
/// </summary>
[GeneratedComClass]
internal sealed partial class ShellDropTarget : IDropTarget, IDisposable
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private readonly Func<string, Task> _openPath;
    private readonly Action<Exception> _onFailure;
    private readonly Func<IDataObject, IReadOnlyList<string>> _files;
    private readonly Func<IDataObject, bool> _hasFiles;
    private nint _hwnd;
    private bool _dragHasFiles;

    public ShellDropTarget(Func<string, Task> openPath, Action<Exception> onFailure,
        Func<IDataObject, IReadOnlyList<string>>? files = null, Func<IDataObject, bool>? hasFiles = null)
    {
        _openPath = openPath ?? throw new ArgumentNullException(nameof(openPath));
        _onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
        _files = files ?? DropData.Files;
        _hasFiles = hasFiles ?? DropData.HasFiles;
    }

    /// <summary>Đăng ký làm đích thả của <paramref name="hwnd"/>; trả false khi OLE từ chối (kéo-thả bị tắt, không phải lỗi chết).</summary>
    public bool Register(nint hwnd)
    {
        if (_hwnd != 0)
        {
            throw new InvalidOperationException("The drop target is already registered.");
        }

        // S_OK / S_FALSE (đã khởi tạo) / RPC_E_CHANGED_MODE: RegisterDragDrop bên dưới tự báo nếu OLE không dùng được.
        _ = Ole32.OleInitialize(0);
        var unknown = ComWrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
        try
        {
            var iid = typeof(IDropTarget).GUID;
            if (Marshal.QueryInterface(unknown, in iid, out var dropTarget) != 0)
            {
                return false;
            }

            try
            {
                if (Ole32.RegisterDragDrop(hwnd, dropTarget) != 0)
                {
                    return false;
                }
            }
            finally
            {
                Marshal.Release(dropTarget);
            }

            _hwnd = hwnd;
            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int DragEnter(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect)
    {
        _dragHasFiles = dataObject is not null && SafeHasFiles(dataObject);
        effect = _dragHasFiles ? DropEffect.Copy : DropEffect.None;
        return 0;
    }

    public int DragOver(uint keyState, PointL point, ref DropEffect effect)
    {
        effect = _dragHasFiles ? DropEffect.Copy : DropEffect.None;
        return 0;
    }

    public int DragLeave()
    {
        _dragHasFiles = false;
        return 0;
    }

    public int Drop(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect)
    {
        _dragHasFiles = false;
        effect = DropEffect.None;
        IReadOnlyList<string> files;
        try
        {
            files = dataObject is null ? [] : _files(dataObject);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _onFailure(ex);
            return 0;
        }

        if (files.Count == 0)
        {
            return 0;
        }

        effect = DropEffect.Copy;
        _ = OpenFirstAsync(files[0]);
        return 0;
    }

    private bool SafeHasFiles(IDataObject data)
    {
        try
        {
            return _hasFiles(data);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return false;
        }
    }

    // async void-free: một ngoại lệ thoát ra khỏi đây sẽ làm sập tiến trình (Window_Drop của WPF cũng nuốt và báo trên status).
    private async Task OpenFirstAsync(string path)
    {
        try
        {
            await _openPath(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _onFailure(ex);
        }
    }

    public void Dispose()
    {
        if (_hwnd != 0)
        {
            _ = Ole32.RevokeDragDrop(_hwnd); // thất bại khi cửa sổ đã huỷ: không còn gì để thu hồi
            _hwnd = 0;
        }

        // KHÔNG gọi OleUninitialize: OLE của thread UI sống tới hết tiến trình (cửa sổ khác trong cùng tiến trình có thể đang
        // dùng nó), và trên runner CI bản có OleUninitialize làm thread UI không thoát được lúc đóng (các cửa sổ tạo sau đó treo ở OleInitialize).
    }
}
