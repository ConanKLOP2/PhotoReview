---
id: NOWPF-SHELL-MENUS-ICON
order: 1130
summary: |-
  Shell Win32: menu chuột phải và menu Tools (⋮) hiện nền trắng/chữ sáng trên Windows 11 build 26300 (uxtheme ordinal ForceDark nửa vời) -> menu tự vẽ owner-draw tối, không còn gọi ordinal cho menu; thêm icon app cho cửa sổ shell (ApplicationIcon + WM_SETICON theo DPI, hộp thoại phụ, kiểm trong verify-release); lỗi dựng/hiện menu ghi shell-errors.log + dòng trạng thái "Menu error". CHƯA XÁC NHẬN trên màn hình thật của người dùng.
---

# NOWPF-SHELL-MENUS-ICON - menu tối owner-draw, icon app, chẩn đoán menu (2026-10-13)

Nguồn: báo cáo người dùng bản shell `PhotoReview.exe` v2.0.475 (Windows 11 Insider build 26300, Intel UHD 630): "menu công cụ lỗi",
"menu chuột phải lỗi", "icon app cũng bị mất"; triệu chứng cụ thể: "Chỉ hiện màu trắng, rê chuột vào mới hiện dần nội dung".

## 1. Nguyên nhân

| Lỗi | Nguyên nhân gốc | Cách sửa |
|---|---|---|
| Menu Tools / chuột phải trắng, chữ chỉ hiện khi hover | `DarkModeMenus` gọi `SetPreferredAppMode(ForceDark)` + `FlushMenuThemes` qua uxtheme ordinal 135/136 (không công bố). Trên build 26300 chữ chuyển sáng nhưng nền menu không chuyển -> chữ sáng trên nền sáng, chỉ thấy ở mục được tô (hover). Cấu hình `config.json` của người dùng KHÔNG liên quan (đã chạy menu thật với BẢN SAO của nó qua `PHOTOREVIEW_TEST_CONFIG`: dựng đủ mục, không ngoại lệ, không `shell-errors.log`). | Menu native vẫn là `CreatePopupMenu` + `TrackPopupMenuEx` (bàn phím, submenu, đa màn hình do OS) nhưng MỌI mục là `MF_OWNERDRAW`: `OwnerDrawMenuSession` tạm subclass cửa sổ chủ (comctl32 `SetWindowSubclass`) nhận `WM_MEASUREITEM`/`WM_DRAWITEM`, vẽ nền `Dark.Panel`, chữ `Dark.Text`, hover `Dark.ButtonPressed`, mục tắt `Dark.TextFaint`, phím tắt căn phải, dấu chọn, mũi tên submenu, vạch ngăn; font = `lfMenuFont` của NONCLIENTMETRICS theo DPI; `SetMenuInfo(MIM_BACKGROUND)` tô nền/viền. KHÔNG còn gọi ordinal cho menu. |
| Mất icon | `PhotoReview.Shell.Win32.csproj` không có `ApplicationIcon`, lớp cửa sổ không có `hIcon`, không `WM_SETICON` (bản WPF có `PhotoReview.ico` ở csproj + `MainWindow.xaml`). | `ApplicationIcon` trỏ `PhotoReview.App\Assets\PhotoReview.ico` (dùng chung); `ShellIcons` (LoadImage từ tài nguyên exe id 32512, AOT-an toàn) đặt icon lớn/nhỏ cho lớp cửa sổ, `WM_SETICON` theo DPI khi tạo và khi `WM_DPICHANGED`, và cho `NativeDialog`. `verify-release.ps1` đòi `PhotoReview.exe` (và exe AOT) có `RT_GROUP_ICON`. WPF không đặt AUMID nên shell cũng không đặt. |
| Lỗi menu bị nuốt | `ContextMenuController.ShowAsync` chỉ có `try/finally`: ngoại lệ bay ra WndProc của `WM_CONTEXTMENU`; `ShellOverlay.ShowToolsMenu` tương tự. Log chung tắt (INV-10) nên không có dấu vết. | Bắt lỗi, ghi `shell-errors.log` (cạnh `config.json`, tối đa 256 KB, giữ nửa cuối) và hiện dòng trạng thái không chặn "Menu error: ..." (khoá `status.menuFailed`). |

## 2. Không tái hiện được trên hidden desktop

Trên máy này (cùng build 26300) menu cũ vẽ NỀN TỐI đúng trong `PrintWindow` của desktop ẩn: lỗi hiển thị chỉ xảy ra trên desktop thật
(DWM tổng hợp popup menu); desktop ẩn không có DWM. Vì vậy sửa theo hướng KHÔNG phụ thuộc theme hệ thống (owner-draw), chứng minh
bằng ảnh chụp: nền đúng `Dark.Panel` (#1E1E1E), chữ sáng, hover đúng màu, không có vùng trắng. Cần người dùng xác nhận trên màn hình thật.

## 3. Việc còn lại / ghi chú

- Mũi tên submenu: hệ thống vẫn vẽ thêm mũi tên đen của nó sau `WM_DRAWITEM` (không tắt được); đen trên nền tối nên gần như vô hình, mũi tên sáng của app nằm cạnh.
- `NativeDialogChrome` (hộp thoại phụ) vẫn dùng `DarkModeMenus` (ordinal 135) cho ComboBox/ListView/ô nhập: chưa thấy lỗi tương tự (menu không còn phụ thuộc), nhưng cần người dùng kiểm hộp thoại Settings/Diagnostics trên build 26300; nếu lỗi thì chuyển sang tự vẽ như menu.
- Người dùng gặp lỗi menu mới: gửi `%LOCALAPPDATA%\PhotoReview\shell-errors.log` (không có dữ liệu ảnh; chứa kiểu ngoại lệ, HRESULT, thông điệp cắt 300 ký tự, 12 khung stack).
