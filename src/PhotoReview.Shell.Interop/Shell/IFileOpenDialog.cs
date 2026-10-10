using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Interop.Shell;

// WP-13b: hộp thoại mở file/thư mục kiểu Vista (IFileOpenDialog) + IShellItem(Array). "[n]" = SLOT vtable theo
// shobjidl_core.h (IUnknown 0..2). ReservedNN = chỗ giữ slot, không được gọi/đổi thứ tự. [PreserveSig] toàn bộ.
// Nhớ: gọi trên luồng STA (CoInitializeEx) - cửa sổ UI của shell chạy STA.

internal static class ShellGuids
{
    public const string ModalWindow = "b4db1657-70d7-485e-8e3e-6fcb5a5c1802";
    public const string FileDialog = "42f85136-db7e-439c-85f1-e4075d135fc8";
    public const string FileOpenDialog = "d57c7288-d4ad-4768-be02-9d969532d960";
    public const string ShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
    public const string ShellItemArray = "b63ea76d-1f85-456f-a19c-48159efa858b";
    public const string DropTarget = "00000122-0000-0000-c000-000000000046";
    public const string DataObject = "0000010e-0000-0000-c000-000000000046";
    public const string ClsidFileOpenDialog = "dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7";

    public static readonly Guid IidFileOpenDialog = new(FileOpenDialog);
    public static readonly Guid IidShellItem = new(ShellItem);
    public static readonly Guid IidDataObject = new(DataObject);
    public static readonly Guid ClsidFileOpenDialogGuid = new(ClsidFileOpenDialog);
}

/// <summary>FILEOPENDIALOGOPTIONS.</summary>
[Flags]
internal enum FileDialogOptions : uint
{
    None = 0,
    OverwritePrompt = 0x2,
    StrictFileTypes = 0x4,
    NoChangeDir = 0x8,
    PickFolders = 0x20,
    ForceFileSystem = 0x40,
    AllNonStorageItems = 0x80,
    NoValidate = 0x100,
    AllowMultiSelect = 0x200,
    PathMustExist = 0x800,
    FileMustExist = 0x1000,
    CreatePrompt = 0x2000,
    ShareAware = 0x4000,
    NoReadOnlyReturn = 0x8000,
    NoTestFileCreate = 0x10000,
    HideMruPlaces = 0x20000,
    HidePinnedPlaces = 0x40000,
    NoDereferenceLinks = 0x100000,
    DontAddToRecent = 0x2000000,
    ForceShowHidden = 0x10000000,
    DefaultNoMiniMode = 0x20000000,
}

/// <summary>SIGDN (IShellItem::GetDisplayName).</summary>
internal enum ShellItemDisplayName : uint
{
    NormalDisplay = 0,
    ParentRelativeParsing = 0x80018001,
    DesktopAbsoluteParsing = 0x80028000,
    FileSystemPath = 0x80058000,
    Url = 0x80068000,
}

/// <summary>COMDLG_FILTERSPEC: hai chuỗi UTF-16 kết thúc NUL do người gọi giữ sống tới khi hộp thoại đóng.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ComdlgFilterSpec
{
    public char* Name;
    public char* Spec;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(ShellGuids.ShellItem)]
internal partial interface IShellItem
{
    [PreserveSig] int Reserved03BindToHandler(nint a, in Guid b, in Guid c, out nint d);                      // [3]
    [PreserveSig] int GetParent(out nint parent);                                                       // [4]

    /// <summary>[5] <paramref name="name"/> là PWSTR cấp bằng CoTaskMemAlloc: người gọi <c>Marshal.FreeCoTaskMem</c>.</summary>
    [PreserveSig] int GetDisplayName(ShellItemDisplayName sigdnName, out nint name);

    [PreserveSig] int Reserved06GetAttributes(uint a, out uint b);                                            // [6]
    [PreserveSig] int Reserved07Compare(nint a, uint b, out int c);                                           // [7]
}

[GeneratedComInterface]
[Guid(ShellGuids.ShellItemArray)]
internal partial interface IShellItemArray
{
    [PreserveSig] int Reserved03BindToHandler(nint a, in Guid b, in Guid c, out nint d);                      // [3]
    [PreserveSig] int Reserved04GetPropertyStore(uint a, in Guid b, out nint c);                              // [4]
    [PreserveSig] int Reserved05GetPropertyDescriptionList(nint a, in Guid b, out nint c);                    // [5]
    [PreserveSig] int Reserved06GetAttributes(uint a, uint b, out uint c);                                    // [6]
    [PreserveSig] int GetCount(out uint count);                                                               // [7]
    [PreserveSig] int GetItemAt(uint index, out nint item);                                             // [8]
    [PreserveSig] int Reserved09EnumItems(out nint a);                                                        // [9]
}

[GeneratedComInterface]
[Guid(ShellGuids.ModalWindow)]
internal partial interface IModalWindow
{
    /// <summary>[3] Chặn tới khi đóng. HRESULT_FROM_WIN32(ERROR_CANCELLED) = 0x800704C7 khi người dùng huỷ.</summary>
    [PreserveSig] int Show(nint owner);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(ShellGuids.FileDialog)]
internal unsafe partial interface IFileDialog : IModalWindow
{
    [PreserveSig] int SetFileTypes(uint fileTypeCount, ComdlgFilterSpec* fileTypes);                          // [4]
    [PreserveSig] int SetFileTypeIndex(uint fileTypeIndex);                                                   // [5]
    [PreserveSig] int GetFileTypeIndex(out uint fileTypeIndex);                                               // [6]
    [PreserveSig] int Reserved07Advise(nint events, out uint cookie);                                         // [7]
    [PreserveSig] int Reserved08Unadvise(uint cookie);                                                        // [8]
    [PreserveSig] int SetOptions(FileDialogOptions options);                                                  // [9]
    [PreserveSig] int GetOptions(out FileDialogOptions options);                                              // [10]
    [PreserveSig] int SetDefaultFolder(IShellItem folder);                                                    // [11]
    [PreserveSig] int SetFolder(IShellItem folder);                                                           // [12]
    [PreserveSig] int GetFolder(out nint folder);                                                       // [13]
    [PreserveSig] int GetCurrentSelection(out nint item);                                               // [14]
    [PreserveSig] int SetFileName(string name);                                                               // [15]
    [PreserveSig] int GetFileName(out nint name);                                                             // [16] CoTaskMemFree
    [PreserveSig] int SetTitle(string title);                                                                 // [17]
    [PreserveSig] int SetOkButtonLabel(string text);                                                          // [18]
    [PreserveSig] int SetFileNameLabel(string label);                                                         // [19]
    [PreserveSig] int GetResult(out nint item);                                                         // [20]
    [PreserveSig] int Reserved21AddPlace(nint item, uint alignment);                                          // [21]
    [PreserveSig] int SetDefaultExtension(string defaultExtension);                                           // [22]
    [PreserveSig] int Close(int hr);                                                                          // [23]
    [PreserveSig] int SetClientGuid(in Guid guid);                                                            // [24]
    [PreserveSig] int ClearClientData();                                                                      // [25]
    [PreserveSig] int Reserved26SetFilter(nint filter);                                                       // [26]
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid(ShellGuids.FileOpenDialog)]
internal unsafe partial interface IFileOpenDialog : IFileDialog
{
    [PreserveSig] int GetResults(out nint results);                                                // [27]
    [PreserveSig] int GetSelectedItems(out nint items);                                            // [28]
}

internal static unsafe partial class ShellNative
{
    private const uint ClsctxInprocServer = 0x1;

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingNameNative(string path, nint bindContext, in Guid riid, out nint item);

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateInstanceNative(in Guid clsid, nint outer, uint clsContext, in Guid riid, out nint instance);

    [LibraryImport("shell32.dll", EntryPoint = "SHCreateDataObject")]
    private static partial int SHCreateDataObjectNative(nint pidlFolder, uint count, nint childPidls, nint inner,
        in Guid riid, out nint dataObject);

    /// <summary>IShellItem cho đường dẫn tệp/thư mục (không cần tồn tại nếu <c>FileMustExist</c> không bật là việc của hộp thoại).</summary>
    public static IShellItem CreateItemFromPath(string path)
    {
        ComInterop.Check(SHCreateItemFromParsingNameNative(path, 0, ShellGuids.IidShellItem, out nint raw));
        return ComInterop.Wrap<IShellItem>(raw);
    }

    /// <summary>Tạo IFileOpenDialog (COM đã khởi tạo trên luồng gọi).</summary>
    public static IFileOpenDialog CreateFileOpenDialog()
    {
        ComInterop.Check(CoCreateInstanceNative(ShellGuids.ClsidFileOpenDialogGuid, 0, ClsctxInprocServer,
            ShellGuids.IidFileOpenDialog, out nint raw));
        return ComInterop.Wrap<IFileOpenDialog>(raw);
    }

    /// <summary>IDataObject rỗng của shell (SetData/GetData tuỳ ý) - dùng cho kéo-thả ra ngoài và test.</summary>
    public static IDataObject CreateEmptyDataObject()
    {
        ComInterop.Check(SHCreateDataObjectNative(0, 0, 0, 0, ShellGuids.IidDataObject, out nint raw));
        return ComInterop.Wrap<IDataObject>(raw);
    }

    /// <summary>Đọc PWSTR do shell cấp (CoTaskMemAlloc) rồi giải phóng.</summary>
    public static string? TakeCoTaskString(nint pwstr)
    {
        if (pwstr == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pwstr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pwstr);
        }
    }
}
