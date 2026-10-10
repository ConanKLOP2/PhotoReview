using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using PhotoReview.Shell.Interop.Graphics;
using PhotoReview.Shell.Interop.Shell;

namespace PhotoReview.Shell.Tests.Graphics;

/// <summary>
/// WP-13b: smoke test COM của shell (IFileOpenDialog/IShellItem, IDataObject, IDropTarget) - không hiện hộp thoại,
/// không cần cửa sổ. Slot được kiểm bằng literal (không bằng chính khai báo) ở các test gọi qua con trỏ hàm thô.
/// </summary>
[Trait("Category", "Native")]
public sealed unsafe partial class ShellComInteropGraphicsTests
{
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

    private const uint CoinitApartmentThreaded = 0x2;
    private const uint GmemMoveable = 0x2;

    /// <summary>Chạy trên luồng STA riêng (COM của shell cần apartment), ném lại ngoại lệ gốc.</summary>
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            int hr = CoInitializeEx(0, CoinitApartmentThreaded);
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Giải phóng wrapper còn sót trước khi rời apartment.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                CoUninitialize();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "luồng STA không kết thúc trong 60 giây");
        if (failure is not null)
        {
            throw new InvalidOperationException("Test STA thất bại: " + failure.Message, failure);
        }
    }

    [Fact]
    public void ShellItem_FromPath_ReturnsFileSystemPathAndParent()
    {
        RunSta(() =>
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            IShellItem item = ShellNative.CreateItemFromPath(windows);
            try
            {
                ComInterop.Check(item.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint name));
                Assert.Equal(windows, ShellNative.TakeCoTaskString(name), ignoreCase: true);

                ComInterop.Check(item.GetParent(out nint parentRaw));
                IShellItem parent = ComInterop.Wrap<IShellItem>(parentRaw);
                ComInterop.Check(parent.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint parentName));
                Assert.Equal(Path.GetDirectoryName(windows), ShellNative.TakeCoTaskString(parentName), ignoreCase: true);
                ComInterop.Release(parent);
            }
            finally
            {
                ComInterop.Release(item);
            }
        });
    }

    [Fact]
    public void FileOpenDialog_ConfigureAndReadBack_WithoutShowing()
    {
        RunSta(() =>
        {
            IFileOpenDialog dialog = ShellNative.CreateFileOpenDialog();
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            IShellItem folder = ShellNative.CreateItemFromPath(windows);
            try
            {
                // Bộ lọc loại tệp phải đặt trước khi chuyển sang chế độ chọn thư mục (sau đó E_UNEXPECTED).
                fixed (char* name = "Ảnh JPEG")
                fixed (char* spec = "*.jpg;*.jpeg")
                {
                    ComdlgFilterSpec filter = new() { Name = name, Spec = spec };
                    ComInterop.Check(dialog.SetFileTypes(1, &filter));
                    ComInterop.Check(dialog.SetFileTypeIndex(1));
                    ComInterop.Check(dialog.GetFileTypeIndex(out uint index));
                    Assert.Equal(1u, index);
                }

                FileDialogOptions options = FileDialogOptions.PickFolders | FileDialogOptions.ForceFileSystem
                                            | FileDialogOptions.AllowMultiSelect | FileDialogOptions.PathMustExist;
                ComInterop.Check(dialog.SetOptions(options));
                ComInterop.Check(dialog.GetOptions(out FileDialogOptions actual));
                // Hộp thoại có thể tự thêm cờ mặc định (FileMustExist...) - chỉ cần các cờ đã đặt còn nguyên.
                Assert.Equal(options, actual & options);

                ComInterop.Check(dialog.SetTitle("Chọn thư mục ảnh"));
                ComInterop.Check(dialog.SetOkButtonLabel("Chọn"));
                ComInterop.Check(dialog.SetFileName("Ảnh"));
                ComInterop.Check(dialog.GetFileName(out nint fileName));
                Assert.Equal("Ảnh", ShellNative.TakeCoTaskString(fileName));

                ComInterop.Check(dialog.SetDefaultFolder(folder));
                ComInterop.Check(dialog.SetDefaultExtension("jpg"));
                ComInterop.Check(dialog.SetClientGuid(new Guid("5b2f0a1c-3d4e-4f60-8a71-92b3c4d5e6f7")));
                _ = dialog.ClearClientData(); // có thể trả lỗi khi chưa có dữ liệu client; chỉ cần không crash (slot [25])
                ComInterop.Check(dialog.SetFileNameLabel("Tên"));
                ComInterop.Check(dialog.SetFolder(folder));
                ComInterop.Check(dialog.GetFolder(out nint currentRaw));
                IShellItem current = ComInterop.Wrap<IShellItem>(currentRaw);
                ComInterop.Check(current.GetDisplayName(ShellItemDisplayName.FileSystemPath, out nint currentName));
                Assert.Equal(windows, ShellNative.TakeCoTaskString(currentName), ignoreCase: true);
                ComInterop.Release(current);
            }
            finally
            {
                ComInterop.Release(folder);
                ComInterop.Release(dialog);
            }
        });
    }

    [Fact]
    public void DataObject_SetThenGetUnicodeText_RoundTrips()
    {
        RunSta(() =>
        {
            const string Text = "Ảnh đẹp";
            IDataObject data = ShellNative.CreateEmptyDataObject();
            try
            {
                FormatEtc format = new()
                {
                    ClipboardFormat = ClipboardFormats.UnicodeText,
                    Aspect = DataViewAspect.Content,
                    Index = -1,
                    Medium = Tymed.HGlobal,
                };
                Assert.NotEqual(0, data.QueryGetData(&format)); // S_FALSE/DV_E_FORMATETC: chưa có

                int bytes = (Text.Length + 1) * 2;
                nint hGlobal = GlobalAlloc(GmemMoveable, (nuint)bytes);
                Assert.NotEqual(0, hGlobal);
                char* locked = (char*)GlobalLock(hGlobal);
                Text.AsSpan().CopyTo(new Span<char>(locked, Text.Length));
                locked[Text.Length] = '\0';
                GlobalUnlock(hGlobal);

                StgMedium medium = new() { Medium = Tymed.HGlobal, Handle = hGlobal };
                // fRelease = TRUE: data object nhận quyền sở hữu hGlobal.
                ComInterop.Check(data.SetData(&format, &medium, 1));
                Assert.Equal(0, data.QueryGetData(&format));

                StgMedium result = default;
                ComInterop.Check(data.GetData(&format, &result));
                try
                {
                    Assert.Equal(Tymed.HGlobal, result.Medium);
                    char* text = (char*)GlobalLock(result.Handle);
                    Assert.Equal(Text, new string(text));
                    GlobalUnlock(result.Handle);
                }
                finally
                {
                    OleNative.ReleaseStgMedium(&result);
                }

                FormatEtc hdrop = new()
                {
                    ClipboardFormat = ClipboardFormats.HDrop,
                    Aspect = DataViewAspect.Content,
                    Index = -1,
                    Medium = Tymed.HGlobal,
                };
                Assert.NotEqual(0, data.QueryGetData(&hdrop));
            }
            finally
            {
                ComInterop.Release(data);
            }
        });
    }

    [GeneratedComClass]
    private sealed partial class RecordingDropTarget : IDropTarget
    {
        public List<string> Calls { get; } = [];

        public int DragEnter(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect)
        {
            Calls.Add($"enter:{keyState}:{point.X},{point.Y}:{effect}:{dataObject is not null}");
            effect = DropEffect.Copy;
            return 0;
        }

        public int DragOver(uint keyState, PointL point, ref DropEffect effect)
        {
            Calls.Add($"over:{keyState}:{point.X},{point.Y}:{effect}");
            effect = DropEffect.Link;
            return 0;
        }

        public int DragLeave()
        {
            Calls.Add("leave");
            return 0;
        }

        public int Drop(IDataObject dataObject, uint keyState, PointL point, ref DropEffect effect)
        {
            Calls.Add($"drop:{keyState}:{point.X},{point.Y}:{effect}:{dataObject is not null}");
            effect = DropEffect.Move;
            return 0;
        }
    }

    [Fact]
    public void DropTarget_VtableSlots3To6_AreEnterOverLeaveDrop()
    {
        RunSta(() =>
        {
            RecordingDropTarget target = new();
            StrategyBasedComWrappers wrappers = new();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
            Guid iid = new(ShellGuids.DropTarget);
            nint dropTarget = ComInterop.QueryInterface(unknown, iid);
            Marshal.Release(unknown);

            IDataObject data = ShellNative.CreateEmptyDataObject();
            nint dataRaw = QueryDataObjectOf(data);
            try
            {
                // Con trỏ hàm thô theo SỐ SLOT literal của oleidl.h: 3 DragEnter, 4 DragOver, 5 DragLeave, 6 Drop.
                nint* vtable = *(nint**)dropTarget;
                PointL point = new() { X = 12, Y = 34 };
                DropEffect effect = DropEffect.Move;

                int hr = ((delegate* unmanaged[MemberFunction]<nint, nint, uint, PointL, DropEffect*, int>)vtable[3])(
                    dropTarget, dataRaw, 8u, point, &effect);
                Assert.Equal(0, hr);
                Assert.Equal(DropEffect.Copy, effect);

                hr = ((delegate* unmanaged[MemberFunction]<nint, uint, PointL, DropEffect*, int>)vtable[4])(
                    dropTarget, 8u, point, &effect);
                Assert.Equal(0, hr);
                Assert.Equal(DropEffect.Link, effect);

                hr = ((delegate* unmanaged[MemberFunction]<nint, int>)vtable[5])(dropTarget);
                Assert.Equal(0, hr);

                effect = DropEffect.Copy;
                hr = ((delegate* unmanaged[MemberFunction]<nint, nint, uint, PointL, DropEffect*, int>)vtable[6])(
                    dropTarget, dataRaw, 0u, point, &effect);
                Assert.Equal(0, hr);
                Assert.Equal(DropEffect.Move, effect);

                Assert.Equal(
                    ["enter:8:12,34:Move:True", "over:8:12,34:Copy", "leave", "drop:0:12,34:Copy:True"],
                    target.Calls);
            }
            finally
            {
                Marshal.Release(dataRaw);
                ComInterop.Release(data);
                Marshal.Release(dropTarget);
            }
        });
    }

    /// <summary>QueryInterface (IDataObject) trên wrapper; trả con trỏ mới đã AddRef.</summary>
    private static nint QueryDataObjectOf(object comObject)
    {
        Assert.True(ComWrappers.TryGetComInstance(comObject, out nint unknown));
        try
        {
            return ComInterop.QueryInterface(unknown, ShellGuids.IidDataObject);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
