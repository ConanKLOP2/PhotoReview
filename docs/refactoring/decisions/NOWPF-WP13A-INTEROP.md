---
id: NOWPF-WP13A
order: 272
summary: |-
  WP-13a (đợt 1 của NO-WPF-EXEC-PLAN, 2026-10-10): khai báo interop Win32 (user32/kernel32/gdi32/dwmapi/shcore/comctl32/ole32/shell32) trong
  `src/PhotoReview.Shell.Interop/Win32/*` bằng LibraryImport, struct blittable và hằng; `InteropSignatureTests` (14 ca) kiểm kích thước/offset
  struct và gọi thử 4 hàm an toàn; mutation M1 (đổi kiểu một trường struct) và M2 (sai EntryPoint) đều bị bắt. Hợp đồng v1 không đổi.
---

# NOWPF-WP13A - interop Win32 (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (mục 2, 3, 8), thẻ WP-13a trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Phụ thuộc WP-01 (khung project). WP-14, WP-18, WP-19a, WP-25 dùng các khai báo này.

## 1. Phạm vi và kết quả

- Chỉ khai báo: mọi `[LibraryImport]` (không `DllImport`, không `[ComImport]`), struct blittable `internal`, hằng số. Không logic.
- Vùng ghi: `src/PhotoReview.Shell.Interop/Win32/*` và `tests/PhotoReview.Shell.Tests/InteropSignatureTests.cs`. Không đụng `Graphics/*`, `Shell/*` (WP-13b),
  không đụng `Shell.Win32` (WP-01 `BootstrapWindow` vẫn biên dịch nguyên vẹn nhờ các alias giữ tên cũ).
- Hợp đồng v1 (`contracts.v1.txt`) không đổi; `ContractSurfaceTests` (ca chặn không-manual) xanh.

## 2. Danh sách khai báo

| File | Nội dung |
|---|---|
| `Win32/User32.cs` | RegisterClassExW, UnregisterClassW, CreateWindowExW, DefWindowProcW, ShowWindow, IsWindowVisible, DestroyWindow, PostMessageW, PostQuitMessage, GetMessageW, PeekMessageW, TranslateMessage, DispatchMessageW, MsgWaitForMultipleObjectsEx, LoadCursorW, SetCursor, SetCapture, ReleaseCapture, GetDpiForWindow, GetDpiForSystem, GetSystemMetrics, GetSystemMetricsForDpi, AdjustWindowRectExForDpi, GetThreadDpiAwarenessContext, GetAwarenessFromDpiAwarenessContext, MonitorFromWindow/Rect/Point, GetMonitorInfoW (2 overload: MonitorInfo, MonitorInfoEx), EnumDisplayMonitors, Get/SetWindowPlacement, SetWindowPos, Get WindowRect/ClientRect, Get/SetWindowLongPtrW, GetCurrentInputMessageSource, clipboard (Open/Empty/Set/Get/Close), CreatePopupMenu, DestroyMenu, AppendMenuW, TrackPopupMenuEx |
| `Win32/Kernel32.cs` | GetModuleHandleW, GlobalAlloc, GlobalLock, GlobalUnlock, GlobalFree |
| `Win32/Gdi32.cs` | GetStockObject, `BlackBrush` (giữ từ WP-01) |
| `Win32/Dwmapi.cs` | DwmSetWindowAttribute (`void*`), hằng DWMWA_* |
| `Win32/Shcore.cs` | GetDpiForMonitor, MDT_EFFECTIVE_DPI |
| `Win32/Comctl32.cs` | TaskDialogIndirect, TDCBF_*, TDF_*, TD_*_ICON |
| `Win32/Ole32.cs` | OleInitialize, OleUninitialize, RegisterDragDrop, RevokeDragDrop |
| `Win32/Shell32.cs` | DragAcceptFiles (dự phòng, theo thẻ) |
| `Win32/Structs.cs` | Rect, Point, WndClassEx, Msg, MonitorInfo, MonitorInfoEx, WindowPlacement, InputMessageSource, TaskDialogButton, TaskDialogConfig |
| `Win32/WindowMessages.cs` | WM_*, WS_*, WS_EX_*, SWP_*, HWND_*, SW_*, GWL/GWLP, SM_*, MONITOR_*, MF_*, TPM_*, CF_*, GMEM_*, PM_*, QS_*/MWMO_*, WAIT_*, ID* |

Kích thước x64 kiểm trong test: Rect 16, Point 8, WndClassEx 80, Msg 48, MonitorInfo 40, MonitorInfoEx 104, WindowPlacement 44,
InputMessageSource 8, TaskDialogButton 16, TaskDialogConfig 176 (offset Callback 152, Width 168).

## 3. Quyết định trong lúc làm (cần lead xem)

1. **Di chuyển `User32.cs` gốc (WP-01) vào `Win32/`** bằng `git mv`, không giữ bản cũ ở thư mục gốc (tránh hai `partial class User32`).
   Tên WP-01 (`WmCreate`, `WmDestroy`, `WmClose`, `WsOverlappedWindow`, `CwUseDefault`, `SwShowNoActivate`, `SwShowDefault`, `IdcArrow`,
   `BlackBrush`, `GetStockObject`, `GetModuleHandle`, `Msg`, `WndClassEx`) giữ nguyên để `BootstrapWindow` không đổi.
2. **`.gitignore` có một dòng ngoại lệ** `!src/PhotoReview.Shell.Interop/Win32/` sau quy tắc `[Ww][Ii][Nn]32/`. Quy tắc gốc (dành cho
   thư mục build `bin/Win32`) làm git bỏ qua mọi file mới trong `Win32/`. Ngoại lệ hẹp chỉ cho thư mục này; không đổi quy tắc chung.
3. **Thêm ngoài danh sách thẻ, vì cần để dùng được hàm đã liệt kê:** GlobalUnlock, GlobalFree (đi kèm GlobalLock/GlobalAlloc), GetClipboardData
   (WP-19a test đọc lại clipboard), GetSystemMetrics (cho test gọi thử), PeekMessageW (vòng lặp WP-14 cần), SetCursor/SetCapture/ReleaseCapture,
   OleInitialize/OleUninitialize (RegisterDragDrop cần OLE trên STA), GetWindowLongPtrW/SetWindowLongPtrW (App đang dùng).
4. **Hằng DWMWA_* nằm trong `Dwmapi.cs`** (không trong `WindowMessages.cs`) để gắn với hàm dùng chúng.
5. **Không đưa vào** các P/Invoke ngoài App của `Platform.Windows` (Explorer COM, RecycleBin, memory probe, D3DKMT, CreateFileW, ...): thẻ chỉ
   yêu cầu P/Invoke của App và danh sách hàm. Các hàm này sẽ được xử lý khi project chứa chúng được chuyển sang Shell (không thuộc WP-13a).
6. **Không có Windows SDK headers trên máy** (kiểm `C:\Program Files (x86)\Windows Kits\10\Include`: không có). Giá trị sizeof/offset trong test
   được tính tay theo định nghĩa SDK x64 và ghi chú cách tính trong test; nếu SDK có sẵn, đối chiếu lại `TASKDIALOGCONFIG` (176) và `WNDCLASSEXW` (80).
7. **`SetWindowLongPtrW`/`GetWindowLongPtrW`** chỉ có ý nghĩa trên x64 (app chỉ x64 trong thực tế); bản x86 không được hỗ trợ bởi khai báo này.
8. **`TaskDialogIndirect`** cần manifest Common-Controls v6: đã khai báo trong manifest của WP-01 (`ShellAssemblyTests` kiểm).

## 4. Kiểm chứng

- `dotnet build src/PhotoReview.Shell.Interop -c Release`: thành công, 0 warning. `Shell.Tests` Release build: 0 warning.
- `InteropSignatureTests` (14 ca, `HotPath` cho kích thước/offset, `Native` cho gọi OS): **14/14 xanh**.
- Mutation M1: đổi `Msg.Private` từ `uint` sang `nint` -> `Msg_Size_Is48` đỏ (đã khôi phục).
- Mutation M2: đổi EntryPoint `GetMonitorInfoW` sang tên không tồn tại -> `MonitorFromPointAndGetMonitorInfoEx_PrimaryMonitor_FillsDeviceName` đỏ (đã khôi phục).
- `PhotoReview.Architecture.Tests` (lọc `ContractSurface|ShellRules`): 17 xanh; 1 đỏ là `L-CONTRACT (manual)` (test Manual cố ý
  chặn khi lọc rộng để không ghi đè `contracts.v1.txt`; không phải lỗi hợp đồng).

## 5. Việc còn mở

- Chưa có test thật cho `Win32` ngoài các ca trên (dwmapi, shcore, comctl32, ole32, clipboard, menu): sẽ được kiểm khi WP-14/18/19a dùng tới,
  vì mỗi gói đó có test riêng theo thẻ.
- Nếu Windows SDK được cài trên máy phát triển, cần đối chiếu sizeof trong test với header thật một lần.
