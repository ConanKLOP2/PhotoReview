---
id: NOWPF-WP24
order: 855
summary: |-
  WP-24 (đợt 3 NO-WPF, 2026-10-12): menu chuột phải của shell Win32. WM_CONTEXTMENU (nút phải, phím Menu/Shift+F10) -> ContextMenuController -> ContextMenuModelBuilder (WP-18) -> NativePopupMenuHost (menu native, tối qua uxtheme) -> lệnh theo id y như các handler *_Click của MainWindow WPF (Fit, Zoom to N %, submenu Zoom: preset/Custom qua IZoomPromptService/AlsoSet/SetCurrent, Refresh, thư mục, Editor, Copy, Settings). G-MENU (12 cấu hình) khớp 100 % qua controller; test thật trên shell (nút phải, Esc, chọn bằng phím, submenu Zoom). 25 đột biến: 23 bị diệt, 2 tương đương. Dựng mô hình ~93 us.
---

# NOWPF-WP24 - menu chuột phải, submenu Zoom, custom zoom (2026-10-12)

Thẻ: WP-24 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiêu thụ C-12 (WP-18), `IZoomPromptService` (WP-19b), `ShellInput`
(WP-22). App WPF (`PhotoReview.App`) và liên kết `.jpg` **không đổi**. Không có chuỗi i18n mới (khoá `main.menu.*` đã có).

## 1. Đã làm (`src/PhotoReview.Shell.Win32/Menus`)

| File | Nội dung |
|---|---|
| `ContextMenuController.cs` | `IContextMenuActions` (những gì menu cần từ cửa sổ) + controller: `BuildModel` (cài đặt/trạng thái ĐỌC LẠI mỗi lần mở như `ImageContextMenu_Opened`), `ShowAsync(điểm DIP)` (menu modal; không mở lồng; `IsOpen` luôn hạ kể cả khi host ném), `ExecuteAsync(id)`: Undo/Recycle/Refresh/OpenFolder/Next/PreviousFolder/Fit width/height chạy qua `ShellInput.ExecuteAsync` (cùng đường với phím tắt), Fit, "Zoom to N %", `Zoom.Preset.N` -> `ApplyZoomMenuSelectionAsync`, `Zoom.Custom` -> `OpenClickZoomCustomDialogAsync` (IZoomPromptService), `Zoom.AlsoSetClickLevel` (lật), `Zoom.SetCurrentAsClickLevel` (làm tròn + kẹp 10..800, không zoom lại), Editor/Copy/Settings. Lỗi vào log + status "action failed"; huỷ không phải lỗi. |
| `ContextMenuMessageHandler.cs` | `WM_CONTEXTMENU`: lParam = toạ độ MÀN HÌNH pixel có dấu (đa màn hình âm) -> DIP theo DPI cửa sổ; chỉ nhận điểm trong vùng client (thanh tiêu đề/viền: trả false để `DefWindowProc` hiện menu hệ thống); lParam = -1 (phím Menu, Shift+F10) neo ở GIỮA vùng client. |
| `ShellMainWindow.Menus.cs` | `RegisterContextMenu` (một dòng ở `RegisterFeatures`), `ContextMenu` (test thay `MenuHost`), `IContextMenuActions` trên `MainViewModel`/`ShellInput`/`SettingsStore`. |
| `NativePopupMenuHost.cs` (WP-18, sửa nhỏ) | `ToScreenPixels` tách thuần (test DPI); hook `MenuCreated` (test: mọi HMENU phải bị huỷ). |

Id mô hình theo quy ước của WP-18 ([NOWPF-WP18](NOWPF-WP18-MENU.md) mục 2). Phím tắt hiện sau TAB, mục tắt = `MF_GRAYED`, preset đang chọn
= `MF_CHECKED`, menu tối bật ở MỖI lần mở (`DarkModeMenus` nhớ lần đầu).

## 2. Khác bản WPF / điều cần biết

1. **Nút phải mở menu ở lúc NHẢ** (WM_CONTEXTMENU do `DefWindowProc` sinh từ WM_RBUTTONUP), như WPF. Nhấn nút phải vẫn chỉ dừng đà (WP-22).
2. **Mở bằng phím** (Menu/Shift+F10) neo giữa client: vị trí của WPF khi `CursorLeft == -1` chưa kiểm chứng (xem NOWPF-UNCONFIRMED, WP24-ANCHOR).
3. **"Explorer folder verb"** không có trong menu WPF (mục Open Folder là hộp chọn thư mục), nên không có ở đây.
4. Mọi lệnh của menu đều được bọc bắt lỗi (bản WPF để Undo/Recycle không bọc); lỗi hiện status thay vì ném lên dispatcher.
5. Menu chuột phải mở được cả khi con trỏ trên toolbar (WPF: `Grid.ContextMenu` phủ cả toolbar); WP-23 có thể tắt bằng `ShellInput.OverlayHitTest` nếu muốn.

## 3. Test

- `ContextMenuControllerTests` (Shell.Tests, HotPath, 40+ ca): mỗi id -> đúng một lệnh (port logic của `ContextMenuRedesignTests`,
  `ZoomContextMenuTests`, `ClickZoomCustomDialogTests`), huỷ, mô hình theo cài đặt/trạng thái tại lúc mở, AlsoSet lật hai chiều, SetCurrent làm tròn/kẹp/Fit,
  lỗi/huỷ, không mở lồng, lệnh chạy SAU khi menu đóng.
- `ContextMenuMessageHandlerTests`: DPI 1/1,25/1,5/2, ngoài client -> menu hệ thống, mép client, toạ độ âm, phím -1, thông điệp khác không mở;
  **G-MENU**: 12 cấu hình của `context-menu.v1.json` (WP-10) khớp từng id/dấu phân cách qua controller.
- `NativeContextMenuHostTests` (Native): `ToScreenPixels` theo DPI, dark bật mỗi lần mở + cho phép cửa sổ chủ, mọi HMENU (gốc + submenu) bị huỷ khi Esc / chọn mục / lặp 25 lần.
- `ShellContextMenuEndToEndTests` (Shell.Integration.Tests, UI): shell thật: WM_CONTEXTMENU, WM_RBUTTONDOWN+UP, phím -1, ngoài client;
  menu native thật với phím post trước (Esc đóng menu, KHÔNG đóng cửa sổ; mũi tên+Enter chọn Next folder; vào submenu Zoom chọn 200 %);
  chạy lệnh theo id qua cửa sổ thật (Zoom to N %, preset có/không AlsoSet, Custom có/huỷ, SetCurrent, AlsoSet, Fit, không mở thư mục, cài đặt đổi giữa hai lần mở).
  Không SendInput, toạ độ lấy từ `ClientToScreen`, không phụ thuộc kích thước màn hình runner, không đợi theo thời gian thật.

## 4. Số đo

Dựng mô hình menu đầy đủ (18 mục cấp trên + submenu Zoom): ~93 us / lần (máy dev, Release, 2000 vòng) - không đáng kể so với một khung.

## 5. Đột biến (25, thủ công: sửa -> build -> Shell.Tests `Menus` rồi Shell.Integration.Tests `ShellContextMenuEndToEnd` trên desktop ẩn)

23 bị diệt, 2 tương đương. Bị diệt bởi unit: Undo<->Recycle, FitWidth2->FitWidth, AlsoSet không lật, SetCurrent bỏ chốt Fit, preset lệch 1,
bỏ chống mở lồng, Copy luôn hiện, lỗi lọt ra ngoài, Open Folder->Next folder, bỏ kiểm non-client, phím neo gốc thay vì giữa, không chia DPI
(handler), bỏ khẳng định nuốt thông điệp, host bỏ scale DPI, host bỏ `DestroyMenu`, host bỏ bật dark. Chỉ bị diệt bởi e2e (keo dán cửa sổ):
không đăng ký menu, "Zoom to N %" mức cố định, `SetClickZoomPercent` không ghi, Fit no-op, `EffectiveZoom` luôn null, `Settings` luôn mặc định,
Custom không áp kết quả. Tương đương: `HasImage` luôn true / `CanCopyPath` luôn true ở cửa sổ (từng cái một: builder yêu cầu CẢ HAI nên không đổi mô hình).
Lượt đầu 3 đột biến (`if (false)`, `when (false)`) lỗi build do cảnh báo; viết lại dạng không cảnh báo rồi diệt.

## 6. Còn lại

- WP-23: nút Tools mở `BuildToolsMenu` qua cùng host; `OverlayHitTest` nếu muốn chuột phải trên toolbar không mở menu.
- Màu menu tối thật cần nhìn bằng mắt (kịch bản thủ công WP-27b) - không tự kiểm được.
