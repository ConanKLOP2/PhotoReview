---
id: NOWPF-WP33
order: 970
summary: |-
  WP-33: COM shell/Recycle Bin typed (IFileOperation, IShellItem2); AOT 80->0 IL.
---

# NOWPF-WP33 - COM shell / Recycle Bin không late-bound (2026-10-12)

Thẻ: WP-33 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); mẫu COM = [NOWPF-WP12](NOWPF-WP12-WIC-GENERATED-COM.md) (`[GeneratedComInterface]`, ranh giới
con trỏ thô `out nint` + `Wrap`/`FinalRelease`); gỡ rào AOT do [NOWPF-WP32](NOWPF-WP32-AOT-SPIKE.md) (G0.4: 80 cảnh báo ở 2 file) báo.
Số đo: [perf/2026-10-12-wp33-com-shell.md](../perf/2026-10-12-wp33-com-shell.md).

## 1. Đã làm (chỉ `src/PhotoReview.Platform.Windows` + test Integration)

| Chỗ late-bound cũ | Thay bằng |
|---|---|
| `WindowsRecycleBin.SendToRecycleBin` mặc định: `FileSystem.DeleteFile(..., OnlyErrorDialogs, SendToRecycleBin)` (VB, `SHFileOperation`) | `Shell/ShellFileRecycler`: `CLSID_FileOperation` + `SHCreateItemFromParsingName` + `IFileOperation.SetOperationFlags/DeleteItem/PerformOperations` trên luồng STA riêng (`ShellStaRunner`). HRESULT -> FileNotFound / DirectoryNotFound / UnauthorizedAccess / PathTooLong / `IOException(hr)` (cùng họ `ThrowWinIOError` của VB); `ERROR_CANCELLED`/`E_ABORT` không ném (người gọi vẫn kiểm tệp đã rời nguồn, `CoreRecycleNotDeleted`) |
| `WindowsRecycleBin.RestoreViaShell`: `Type.GetTypeFromProgID("Shell.Application")` + `Activator.CreateInstance` + `dynamic` (Namespace(10), Items, ExtendedProperty, Name, Size, Verbs, DoIt, InvokeVerb) + `ComEnumeration` | `Shell/RecycleBinShell`: chính sách tách khỏi COM (`RecycleBinRestorer` trên `IRecycleBinSession`/`IRecycleBinEntry`/`IRecycleVerbs`, kiểm bằng fake); COM thật `ComRecycleBinSession` = `SHGetKnownFolderItem(FOLDERID_RecycleBinFolder)` -> `BindToHandler(BHID_EnumItems)` `IEnumShellItems` -> `IShellItem2` (`System.Recycle.DeletedFrom`, `System.Size`, `System.DateModified`) và verb = `BHID_SFUIObject` `IContextMenu` + HMENU tạm (đúng cách `FolderItem.Verbs`/`InvokeVerb` làm bên trong); tên verb -> `RestoreVerb` y như cũ, dự phòng verb chuẩn `undelete` |
| `ExplorerOrderService.ShellAccess.Real`: ProgID + `Activator` + `InvokeMember("Windows")`; `ComEnumeration` (IDispatch/IEnumVARIANT `ComImport` + `GetObjectForIUnknown`); `((dynamic)window).LocationURL` | `Explorer/ExplorerComInterop`: `ShellWindowsSource` (`CLSID_ShellWindows`, `IShellWindows` slot 7 `get_Count` / 8 `Item(VT_I4)`) + `ShellWindow` (`IWebBrowserApp` slot 30 `get_LocationURL`); `ShellAccess(IsAvailable, Open)` trả `IExplorerWindowSource`; `REGDB_E_CLASSNOTREG` -> `ShellUnavailable` |
| `ExplorerNativeVtable`: `Marshal.GetDelegateForFunctionPointer` + delegate `UnmanagedFunctionPointer` | `delegate* unmanaged[Stdcall]` theo số slot ghi sẵn (cùng slot, cùng thứ tự gọi, cùng cache theo vtable của `IShellItem`) |
| `DllImport` ở đường Recycle: `GetDriveTypeW`, `GetVolumePathNameW`, `GetVolumeNameForVolumeMountPointW`, `GetDiskFreeSpaceExW`; shell32 `SHParseDisplayName/ILCombine/SHGetNameFromIDList` | `LibraryImport` (L-AOT) |
| `RecycleItemFailure` (có `RuntimeBinderException`) / selector bắt `RuntimeBinderException` | bỏ nhánh `RuntimeBinder` (không còn `dynamic`); thêm `E_NOINTERFACE` -> `InvalidCastException` bị bỏ qua êm như cửa sổ đang đóng |

`ComEnumeration.cs` bị xoá (không còn ai dùng). `Microsoft.VisualBasic.*` không còn trong `src/`. Số chỗ late-bound đã thay: **5 đường** (gửi Thùng rác, khôi phục,
mở danh sách cửa sổ Explorer, đọc `LocationURL`, liệt kê IEnumVARIANT) + 1 `GetDelegateForFunctionPointer` x 7 + 7 `DllImport`.

## 2. Hành vi giữ nguyên / khác có chủ ý

- Không đổi `IRecycleBin`, `CanRecycle`/`FitsInRecycleBin`/`DeletePermanently` (R2-F-05/R11/R26/F-WIN-2), thứ tự: kiểm ổ -> đọc size lại -> `RecycleBinCapacityGuard` -> shell ->
  `File.Exists` sau đó. Không đổi hành vi xoá/undo/journal, không đụng `IRecycleBin` contract ADR 0003/0005.
- **Khác có chủ ý (đề xuất, CHƯA XÁC NHẬN):** (a) thêm `FOFX_RECYCLEONDELETE` vào cờ (bản VB chỉ có `FOF_ALLOWUNDO`; cờ này chỉ nhấn mạnh "xoá = vào thùng rác khi có thể",
  không làm xoá vĩnh viễn); (b) mỗi lần gửi/khôi phục chạy trên luồng STA ngắn hạn (thread pool là MTA, chưa chắc đã `CoInitialize`; chi phí ~0,2 ms/lần, lần gọi vốn đồng bộ);
  (c) `System.DateModified` đọc bằng `GetFileTime` (FILETIME UTC chính xác 100 ns, đã đo khớp tệp gốc) thay vì DATE "UTC digits" + đọc thay thế theo giờ địa phương;
  `RecycleCandidate.AlternateLastWriteUtc` còn trong selector nhưng đường thật không dùng nữa; (d) tên mục = `SIGDN_PARENTRELATIVE` (đã đo: `SIGDN_NORMALDISPLAY` của mục thùng
  rác là cả đường dẫn gốc, không phải tên tệp); (e) danh sách cửa sổ đi theo chỉ số `Item(i)` (đủ `get_Count`) thay vì `_NewEnum`; một `Item` lỗi giữa chừng vẫn dừng đi tiếp
  và giữ phần đã tìm (RV-P03), `Item` trả null (cửa sổ đã đóng) thì bỏ qua.
- Chưa làm (ngoài phạm vi shell/Recycle, giao WP-34): `DllImport` còn lại ở `WindowsDisplayClock`, `WindowsDarkTitleBar`, `WindowsDwmCloak`, `InstanceForwardClient`,
  `WindowsNaturalComparer`, `WindowsMemoryProbe` (Platform.Windows) và `App`/`Core` (`PhysicalJournalCompactionFiles`); `IsAotCompatible` cho Platform.Windows (NETSDK1210 trên
  `net10.0-windows`; cổng thật là publish AOT, đã 0 cảnh báo IL cho đường này). `tests/.../TestRecycleBinCleanup` (test-only) vẫn dùng `Shell.Application`.

## 3. Kiểm chứng

- Build Release `PhotoReview.slnx` 0 warning / 0 error. `dotnet publish src/PhotoReview.Shell.Win32 -r win-x64 -p:ShellAot=true`: **0 cảnh báo IL2xxx/IL3xxx** (spike: 80).
- Test mới `ShellComWp33Tests` (53): `ShellCom.Wrap/Release` đếm ref bằng đối tượng native giả, `ShellStaRunner`, `ShellFileRecycler` với fake `IFileOperation`/`IShellItem`
  (thứ tự gọi, cờ, ánh xạ HRESULT, giải phóng cả khi lỗi), `RecycleBinRestorer` với fake phiên (đúng-một-khớp, size/giờ, verb theo tên/chuẩn, lỗi theo mục vs lỗi chặn,
  giải phóng mọi entry/verb/phiên), `ShellWindowsSource`/`ShellWindow` trên vtable native giả (count/Item VT_I4/null/lỗi giữa chừng/QI lỗi/BSTR), dịch vụ Explorer giải phóng
  cửa sổ + nguồn; một test ĐỌC-CHỈ trên thùng rác thật (mở, liệt kê <= 5 mục, đọc thuộc tính, verb) - không gửi/khôi phục/xoá gì. Test cũ sửa theo seam mới
  (`ExplorerNativeViewTests`, `WindowsRecycleBinCandidateTests`); `PlatformP3AuditTests` P-COM-01 (ComEnumeration) retire.
- Native TC06 (`NativeRecycleBinTests`, chỉ mục của chính test, tự dọn): 3/3 xanh trên đường mới (gửi `IFileOperation`, khôi phục `IContextMenu`, dọn `$R/$I` của chính nó).
  Explorer thật (cửa sổ do chính tôi mở trên thư mục tạm, đã đóng): `Available`, 6 đường dẫn, 3 cột sắp xếp; warm 27-42 ms.
- **Mutation thủ công, 36 đột biến** (xem script/kết quả dưới; chỉ chạy bộ test dùng fake, KHÔNG chạm Thùng rác thật):
  cờ (bỏ ALLOWUNDO, đổi giá trị ALLOWUNDO, bỏ SILENT, thêm NOERRORUI), bỏ kiểm HRESULT của `DeleteItem`/`PerformOperations`, bỏ release operation, huỷ/E_ABORT ném, đảo ánh xạ
  FileNotFound/AccessDenied/cancel, `S_OK` ném, `Release` không `FinalRelease`, `Wrap` không trả ref/không trả wrapper khi ép kiểu sai, chấp nhận nhiều ứng viên, không
  release entry/verb/phiên, không bỏ qua size/giờ không đọc được, luôn verb chuẩn, lỗi lạ coi là theo-mục, bỏ `InvalidCast` khỏi lỗi theo-mục, VT_I4 -> VT_I2, không bỏ qua item null,
  không release nguồn/QI/AddRef/BSTR null, bộ lọc `REGDB_E_CLASSNOTREG` đảo, không release cửa sổ/nguồn, selector không bỏ qua `InvalidCast`, luồng không STA, đảo slot `GetDisplayName`.
  **33/36 diệt ở lần chạy đầu** (3 trong đó là sập host khi wrapper rò, vẫn tính diệt); **3 sống sót thật** đã lấp: M30 (cửa sổ không release sau truy vấn) và M31 (nguồn không dispose)
  bằng `Service_ReleasesEveryWindowAndTheSource_WhateverTheOutcome`; M33 (selector bắt `InvalidCast` im lặng) bằng `WindowWithoutTheBrowserInterfaceIsSkippedWithoutALogEntry`.
  Chạy lại: 36/36 diệt. (Đột biến `Entries()`/`ComRecycleVerbs` COM thật không chạy được trên Thùng rác thật; được Native TC06 + test đọc-chỉ phủ.)

## 4. Còn lại cho WP-34/36

`IsAotCompatible` + cổng publish AOT 0 cảnh báo trên toàn bộ assembly (Core/Imaging/Platform.Windows/App.Shared); bỏ `DllImport` còn lại (mục 2); WP-35 cửa sổ phụ; WP-36 CI.
