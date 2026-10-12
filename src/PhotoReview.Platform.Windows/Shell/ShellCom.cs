using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Platform.Windows.Shell;

/// <summary>
/// WP-33: ranh giới giữa con trỏ COM thô (từ hàm C như <c>SHCreateItemFromParsingName</c>, <c>CoCreateInstance</c>) và đối tượng
/// <c>[GeneratedComInterface]</c> trong đường shell của Platform.Windows (thùng rác, IFileOperation, cửa sổ Explorer). Cùng quy ước
/// với <c>Shell.Interop/ComInterop</c> và <c>WicCom</c> (WP-12/13b): tham số RA kiểu interface khai báo <c>out nint</c> và bọc bằng
/// <see cref="Wrap{T}"/> (wrapper <c>UniqueInstance</c> chiếm 1 ref); <see cref="Release"/> gọi <c>FinalRelease</c> NGAY (thay
/// <c>Marshal.FinalReleaseComObject</c>, vô tác dụng với wrapper của ComWrappers).
/// </summary>
internal static partial class ShellCom
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    internal const uint ClsctxAll = 0x17; // INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER

    /// <summary>HRESULT thất bại -> ngoại lệ tương ứng (<see cref="Marshal.ThrowExceptionForHR(int)"/>).</summary>
    internal static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>
    /// Bọc con trỏ COM thô thành <typeparamref name="T"/>. CHIẾM quyền sở hữu 1 ref của <paramref name="unknown"/>; ép kiểu sai
    /// (QueryInterface không hỗ trợ) thì trả wrapper ngay trước khi ném.
    /// </summary>
    internal static T Wrap<T>(nint unknown) where T : class
    {
        if (unknown == 0) throw new ArgumentNullException(nameof(unknown));
        object? wrapper = null;
        try
        {
            wrapper = Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            return (T)wrapper;
        }
        catch
        {
            if (wrapper is ComObject failed) failed.FinalRelease();
            throw;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Trả COM ref của wrapper ngay lập tức; gọi lại là no-op; đối tượng managed (fake test) không bị ảnh hưởng.</summary>
    internal static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper) wrapper.FinalRelease();
    }

    /// <summary>CoCreateInstance(<paramref name="clsid"/>, <paramref name="iid"/>) -> con trỏ đã AddRef (người gọi <see cref="Wrap{T}"/> hoặc <c>Marshal.Release</c>).</summary>
    internal static nint CreateInstance(in Guid clsid, in Guid iid, uint context = ClsctxAll)
    {
        Check(CoCreateInstance(in clsid, 0, context, in iid, out var instance));
        return instance;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    // ---- shell32 / user32 (đường shell; thay DllImport, L-AOT) ----

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderItem(in Guid folderId, uint flags, nint token, in Guid iid, out nint item);

    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    internal static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMenuItemInfo(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
}

/// <summary>MENUITEMINFOW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MenuItemInfo
{
    public uint Size;
    public uint Mask;
    public uint Type;
    public uint State;
    public uint Id;
    public nint SubMenu;
    public nint BitmapChecked;
    public nint BitmapUnchecked;
    public nuint ItemData;
    public nint TypeData;
    public uint Cch;
    public nint BitmapItem;
}

/// <summary>PROPERTYKEY.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint propertyId)
{
    public readonly Guid FormatId = formatId;
    public readonly uint PropertyId = propertyId;
}

/// <summary>CMINVOKECOMMANDINFO (bản ANSI: <see cref="Verb"/> là LPCSTR hoặc MAKEINTRESOURCE(offset)).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CmInvokeCommandInfo
{
    public uint Size;
    public uint Mask;
    public nint Window;
    public nint Verb;
    public nint Parameters;
    public nint Directory;
    public int Show;
    public uint HotKey;
    public nint Icon;
}

/// <summary>Chạy công việc COM của shell trên một luồng STA riêng, ngắn hạn: không phụ thuộc apartment của luồng gọi (thread pool là MTA, chưa chắc đã CoInitialize).</summary>
internal static class ShellStaRunner
{
    internal static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        T result = default!;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        })
        { IsBackground = true, Name = "PhotoReview shell COM" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
