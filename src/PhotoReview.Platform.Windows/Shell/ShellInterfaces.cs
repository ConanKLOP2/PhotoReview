using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PhotoReview.Platform.Windows.Shell;

// WP-33: interface COM của đường shell (thùng rác, IFileOperation) khai báo bằng [GeneratedComInterface] (source-generated, không
// ComImport/RCW/late-binding; thân thiện trim/AOT). Số trong ngoặc vuông là SLOT vtable theo shobjidl_core.h / shobjidl.h
// (IUnknown 0..2). Mọi method [PreserveSig]: HRESULT do người gọi kiểm bằng ShellCom.Check. Tham số RA kiểu interface là
// `out nint` (xem ShellCom). Method không dùng giữ chỗ slot, KHÔNG được đổi thứ tự.

internal static class ShellIds
{
    internal const string ShellItem = "43826d1e-e718-42ee-bc55-a1e261c37bfe";
    internal const string ShellItem2 = "7e9fb0d3-919f-4307-ab2e-9b1860310c93";
    internal const string EnumShellItems = "70629033-e363-4a28-a567-0db78006e6d7";
    internal const string ContextMenu = "000214e4-0000-0000-c000-000000000046";
    internal const string FileOperation = "947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8";

    internal static readonly Guid IidShellItem = new(ShellItem);
    internal static readonly Guid IidEnumShellItems = new(EnumShellItems);
    internal static readonly Guid IidContextMenu = new(ContextMenu);
    internal static readonly Guid IidFileOperation = new(FileOperation);
    internal static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    internal static readonly Guid FolderIdRecycleBin = new("b7534046-3ecb-4c18-be4e-64cd4cb7d6ac");
    /// <summary>BHID_EnumItems: IShellItem::BindToHandler -> IEnumShellItems.</summary>
    internal static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    /// <summary>BHID_SFUIObject: IShellItem::BindToHandler -> IContextMenu (cùng đối tượng mà FolderItem.Verbs dùng).</summary>
    internal static readonly Guid BhidSfUiObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

    /// <summary>System.Recycle.DeletedFrom: thư mục gốc của mục trong thùng rác.</summary>
    internal static readonly PropertyKey DeletedFrom = new(new Guid("9b174b33-40ff-11d2-a27e-00c04fc30871"), 2);
    /// <summary>System.DateModified.</summary>
    internal static readonly PropertyKey DateModified = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 14);
    /// <summary>System.Size.</summary>
    internal static readonly PropertyKey Size = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 12);

    /// <summary>
    /// SIGDN_PARENTRELATIVE (SHGDN_INFOLDER): tên hiển thị TRONG thư mục, như <c>FolderItem.Name</c> (bỏ đuôi khi Explorer ẩn đuôi đã
    /// biết). Đã đo: SIGDN_NORMALDISPLAY của mục thùng rác là cả đường dẫn gốc, không phải tên tệp.
    /// </summary>
    internal const uint SigdnParentRelative = 0x80080001;
}

[GeneratedComInterface]
[Guid(ShellIds.ShellItem)]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);   // [3]
    [PreserveSig] int GetParent(out nint parent);                                                       // [4]

    /// <summary>[5] <paramref name="name"/> là PWSTR cấp bằng CoTaskMemAlloc: người gọi <c>Marshal.FreeCoTaskMem</c>.</summary>
    [PreserveSig] int GetDisplayName(uint sigdn, out nint name);

    [PreserveSig] int GetAttributes(uint mask, out uint attributes);                                    // [6]
    [PreserveSig] int Compare(IShellItem other, uint hint, out int order);                              // [7]
}

[GeneratedComInterface]
[Guid(ShellIds.ShellItem2)]
internal partial interface IShellItem2 : IShellItem
{
    [PreserveSig] int GetPropertyStore(uint flags, in Guid iid, out nint store);                                          // [8]
    [PreserveSig] int GetPropertyStoreWithCreateObject(uint flags, nint createObject, in Guid iid, out nint store);       // [9]
    [PreserveSig] int GetPropertyStoreForKeys(nint keys, uint count, uint flags, in Guid iid, out nint store);            // [10]
    [PreserveSig] int GetPropertyDescriptionList(in PropertyKey key, in Guid iid, out nint list);                         // [11]
    [PreserveSig] int Update(nint bindContext);                                                                           // [12]
    [PreserveSig] int GetProperty(in PropertyKey key, nint propVariant);                                                  // [13]
    [PreserveSig] int GetCLSID(in PropertyKey key, out Guid clsid);                                                       // [14]

    /// <summary>[15] FILETIME (100 ns kể từ 1601, UTC) dạng <see cref="long"/>.</summary>
    [PreserveSig] int GetFileTime(in PropertyKey key, out long fileTime);

    [PreserveSig] int GetInt32(in PropertyKey key, out int value);                                                        // [16]

    /// <summary>[17] <paramref name="value"/> là PWSTR cấp bằng CoTaskMemAlloc: người gọi <c>Marshal.FreeCoTaskMem</c>.</summary>
    [PreserveSig] int GetString(in PropertyKey key, out nint value);

    [PreserveSig] int GetUInt32(in PropertyKey key, out uint value);                                                      // [18]
    [PreserveSig] int GetUInt64(in PropertyKey key, out ulong value);                                                     // [19]
    [PreserveSig] int GetBool(in PropertyKey key, out int value);                                                         // [20]
}

[GeneratedComInterface]
[Guid(ShellIds.EnumShellItems)]
internal partial interface IEnumShellItems
{
    /// <summary>[3] Lấy tối đa 1 mục (celt = 1): <paramref name="item"/> là IShellItem đã AddRef, S_FALSE khi hết.</summary>
    [PreserveSig] int Next(uint count, out nint item, out uint fetched);

    [PreserveSig] int Skip(uint count);                                                                                   // [4]
    [PreserveSig] int Reset();                                                                                            // [5]
    [PreserveSig] int Clone(out nint enumerator);                                                                         // [6]
}

[GeneratedComInterface]
[Guid(ShellIds.ContextMenu)]
internal partial interface IContextMenu
{
    /// <summary>[3] Thành công: mã thành công có byte thấp = số lệnh đã thêm.</summary>
    [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);

    [PreserveSig] int InvokeCommand(in CmInvokeCommandInfo info);                                                         // [4]
    [PreserveSig] int GetCommandString(nuint idCmd, uint type, nint reserved, nint name, uint cch);                       // [5]
}

/// <summary>IFileOperation (shobjidl_core.h, Vista+). Chỉ <c>SetOperationFlags</c>, <c>DeleteItem</c>, <c>PerformOperations</c>, <c>GetAnyOperationsAborted</c> được gọi.</summary>
[GeneratedComInterface]
[Guid(ShellIds.FileOperation)]
internal partial interface IFileOperation
{
    [PreserveSig] int Advise(nint sink, out uint cookie);                                                                 // [3]
    [PreserveSig] int Unadvise(uint cookie);                                                                              // [4]
    [PreserveSig] int SetOperationFlags(uint flags);                                                                      // [5]
    [PreserveSig] int SetProgressMessage(nint message);                                                                   // [6]
    [PreserveSig] int SetProgressDialog(nint dialog);                                                                     // [7]
    [PreserveSig] int SetProperties(nint properties);                                                                     // [8]
    [PreserveSig] int SetOwnerWindow(nint owner);                                                                         // [9]
    [PreserveSig] int ApplyPropertiesToItem(IShellItem item);                                                             // [10]
    [PreserveSig] int ApplyPropertiesToItems(nint items);                                                                 // [11]
    [PreserveSig] int RenameItem(IShellItem item, nint newName, nint sink);                                               // [12]
    [PreserveSig] int RenameItems(nint items, nint newName);                                                              // [13]
    [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, nint newName, nint sink);                         // [14]
    [PreserveSig] int MoveItems(nint items, IShellItem destination);                                                      // [15]
    [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, nint copyName, nint sink);                        // [16]
    [PreserveSig] int CopyItems(nint items, IShellItem destination);                                                      // [17]

    /// <summary>[18] <paramref name="sink"/> = IFileOperationProgressSink (không dùng: 0).</summary>
    [PreserveSig] int DeleteItem(IShellItem item, nint sink);

    [PreserveSig] int DeleteItems(nint items);                                                                            // [19]
    [PreserveSig] int NewItem(IShellItem destination, uint attributes, nint name, nint templateName, nint sink);          // [20]

    /// <summary>[21] Chạy các thao tác đã xếp hàng, đồng bộ.</summary>
    [PreserveSig] int PerformOperations();

    [PreserveSig] int GetAnyOperationsAborted(out int aborted);                                                           // [22]
}
