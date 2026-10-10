---
id: NOWPF-WP19A
order: 382
summary: |-
  WP-19a (đợt 2 của NO-WPF-EXEC-PLAN, 2026-10-10): `Shell.Win32/Dialogs/*` - `Win32DialogService : IDialogService` (xác nhận/thông báo/lỗi bằng
  TaskDialogIndirect, chọn thư mục qua seam `IShellFolderDialog`, cửa sổ phụ uỷ qua `ISecondaryWindowPort` nạp lười), `Win32ClipboardService`
  (OpenClipboard/SetClipboardData, thử lại 5 x 20 ms) và sửa lỗi bố cục WP-13a: TASKDIALOGCONFIG/TASKDIALOG_BUTTON phải Pack = 1 (160/12 byte).
  16 test mới + 2 test chữ ký đã sửa; mutation 10/10 bị bắt. Hợp đồng v1 không đổi; còn chờ nối COM IFileOpenDialog (WP-13b) và
  `IClipboardService` dời sang App.Shared (WP-09).
---

# NOWPF-WP19A - hộp thoại native + clipboard (2026-10-10)

Kế hoạch: [NO-WPF-EXEC-PLAN](NO-WPF-EXEC-PLAN.md) (C-13), thẻ WP-19a trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md).
Phụ thuộc WP-13a ([NOWPF-WP13A](NOWPF-WP13A-INTEROP.md)). WP-19b (cầu nối WPF) và WP-20 (composition root) dùng các lớp này.

## 1. Đã làm

| File (`src/PhotoReview.Shell.Win32/Dialogs/`) | Nội dung |
|---|---|
| `TaskDialogs.cs` | `ITaskDialogPort` (seam), `NativeTaskDialogPort` gọi `TaskDialogIndirect`: Có/Không cho xác nhận, OK + biểu tượng thông tin/cảnh báo cho ShowMessage/ShowError (khớp `WpfDialogService`), `TDF_ALLOW_DIALOG_CANCELLATION` để X/Esc trả `IDCANCEL` |
| `Win32DialogService.cs` | `IDialogService` (internal). `ShowConfirmation` true chỉ khi `IDYES`; `PickFolder` lấy tiêu đề từ `Tr.DialogPickFolderTitle`; `ShowSettings/Recovery/Diagnostics/Benchmark/SkippedFiles/BatchReview` uỷ `ISecondaryWindowPort` qua `Lazy<>` (không nạp khi chỉ hiện hộp thoại native; cầu nối vắng = false/no-op) |
| `FolderPicker.cs` | `IShellFolderDialog` (seam) + `FolderPicker` (thư mục khởi đầu chỉ khi tồn tại, như `WpfFolderPicker`) + `UnwiredShellFolderDialog` (trả null) |
| `Win32ClipboardService.cs` | `TrySetText`: `OpenClipboard(owner)` thử lại 5 x 20 ms, `EmptyClipboard`, `GlobalAlloc` + `SetClipboardData(CF_UNICODETEXT)`, luôn `CloseClipboard`, free bộ nhớ nếu chưa chuyển giao, không ném (false khi vẫn bận / không có cửa sổ chủ) |
| `src/PhotoReview.Shell.Interop/Win32/ActivationContext.cs` | CreateActCtx/ActivateActCtx: chỉ để test native nạp comctl32 v6 trong testhost (không manifest) |

Test: `tests/PhotoReview.Shell.Tests/Win32DialogServiceTests.cs` (14 HotPath qua seam + 2 `Category=Native`: TaskDialog thật đóng bằng
`PostMessage(WM_CLOSE)` từ callback TDN_CREATED -> `ShowConfirmation` false; clipboard set -> đọc lại bằng `GetClipboardData`, khôi phục văn bản cũ).

## 2. Lỗi WP-13a đã sửa

`TaskDialogIndirect` trả `E_INVALIDARG` (0x80070057) vì `TASKDIALOGCONFIG`/`TASKDIALOG_BUTTON` nằm giữa `pshpack1.h` trong `commctrl.h`
(đóng gói 1 byte). `Structs.cs` đã khai báo bố cục mặc định (176 / 16 byte). Sửa: `Pack = 1` -> 160 / 12 byte, offset ParentWindow 4,
WindowTitle 28, ButtonCount 60, Buttons 64, Callback 140, Width 156; `InteropSignatureTests` cập nhật. Chỉ lộ ra khi gọi thật (test cũ chỉ so khớp
số tự viết). Mọi nơi khác dùng struct này (hiện chưa có) cần đọc theo bố cục mới.

## 3. Lệch khỏi thẻ / điểm cần nối

1. **Lớp internal, không implement interface của App**: `IClipboardService` còn ở `PhotoReview.App.Services` (WP-09 mới dời sang App.Shared) và
   Shell.Win32 không tham chiếu App. `Win32ClipboardService` có đúng chữ ký `bool TrySetText(string)`; WP-09 chỉ cần thêm `: IClipboardService`.
   Không cần v1.1 nếu WP-09 dời theo C-13; nếu muốn khoá trong hợp đồng thì đề xuất v1.1 thêm `IClipboardService` vào App.Shared.
2. **Chọn thư mục**: `IFileOpenDialog` + `FOS_PICKFOLDERS` chưa có COM trong Interop (WP-13b). `FolderPicker` đã đủ logic; WP-13b/WP-20 cần
   implement `IShellFolderDialog` (qua `[GeneratedComInterface]` trong Shell.Interop) và đưa vào thay `UnwiredShellFolderDialog`. Test chỉ test seam
   (đúng thẻ: không mở UI trong CI).
3. **`ISecondaryWindowPort`** nằm trong Shell.Win32 và phản chiếu `ISecondaryWindowHost` (C-13b) vì Shell.Win32 không tham chiếu WpfBridge. WP-19b:
   `WpfBridgeLoader` bọc `ISecondaryWindowHost` thành `ISecondaryWindowPort` (6 phương thức, `PromptCustomZoom` thuộc `IZoomPromptService`).
4. **`IZoomPromptService`** đã có sẵn trong App.Shared (WP-01); thẻ WP-19a không giao phần native, nên chưa có thực thi - WP-19b làm qua bridge.
5. Chuỗi nút: dùng nút chung của hệ thống (Có/Không/OK, hệ thống tự bản địa hoá), tiêu đề/nội dung do người gọi truyền (đã qua `Tr`); không khoá i18n mới.
6. Test TaskDialog thật chạy trên desktop hiện tại (nháy cửa sổ rất ngắn), không phải desktop ẩn: mặc định bị loại bởi `Category=Native`;
   dùng `tools/run-tests-hidden.ps1` nếu muốn không nhìn thấy.
