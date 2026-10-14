---
id: FEAT-EXTERNAL-EDITORS
order: 1390
summary: |-
  Q-R48 mở rộng: nhiều trình chỉnh sửa ngoài (Lightroom, Topaz, DxO...) thành submenu động trong menu chuột phải; phím tắt chỉ cho editor mặc định (mặc định trống), tham số mỗi editor, gửi RAW gốc khi có (mặc định bật). CHƯA XÁC NHẬN.
---

# FEAT-EXTERNAL-EDITORS - submenu "Open in External Editor" nhiều editor (2026-10-14)

Nền: [Q-R41..Q-R52](Q-R41-Q-R52-user-feedback.md) (Q-R48: một đường dẫn, mục menu ẩn tới khi khai báo), [ADR 0010](../../adr/0010-win32-direct2d-shell.md).
Yêu cầu của người dùng: mục "Open in External Editor" thành submenu như Zoom, liệt kê các editor đã khai báo (số lượng không cố định) kèm
"Add more editors..." và "Set default editor...", khai báo được bằng menu hoặc trong Settings; chọn editor mặc định và có phím tắt tuỳ
biến (thiết lập ở Settings) để gửi ảnh tới editor đó. Người dùng đã bảo "xác nhận, làm thử xem" cho các đề xuất dưới đây.

## 1. Quyết định (CHƯA XÁC NHẬN, làm theo đề xuất của lead)

| # | Quyết định | Lý do | Hoàn tác |
|---|---|---|---|
| a | Phím tắt chỉ cho **editor mặc định**: phím tuỳ chọn mới `Shortcuts.OpenInDefaultEditor`, mặc định **trống**, hiện ở nhóm Actions của trang Shortcuts, qua kiểm tra xung đột phím như mọi phím tuỳ chọn | Một phím cho mỗi editor làm trang Shortcuts phình theo số editor (danh sách động) và dễ xung đột | Bỏ hàng ở `ShortcutsSettingsPage.ActionRows` + thuộc tính; cấu hình cũ vẫn đọc được (khoá lạ bị bỏ qua) |
| b | Mỗi editor có **tham số** `Arguments`, mặc định `"{file}"`; `{file}` thay bằng đường dẫn đầy đủ. Một lượt quét trái-phải (đường dẫn chèn vào không bị quét lại), mọi `{file}` đều được thay; token trong cặp nháy nhận đường dẫn trần, token trần nhận đường dẫn đã quote; thiếu `{file}` thì nối đường dẫn đã quote vào cuối | Mỗi chương trình một cú pháp riêng (`--open`, `/edit`...) | Đặt lại ô tham số về `"{file}"` (hành vi cũ) |
| c | Công tắc **"Gửi tệp RAW gốc khi ảnh có RAW"**, mặc định **bật** (`ExternalEditorSendsRawOriginal`). Cặp RAW+JPEG lấy từ `CaptureGroup` của catalog (cơ chế ghép sẵn có, ADR 0009); khi chế độ ghép RAW là Separate thì tìm tệp cùng thư mục, cùng tên, phần mở rộng thuộc `ImageFileTypes.RawExtensions` | Chỉnh sửa chuyên nghiệp cần RAW, JPEG chỉ là bản xem | Tắt công tắc ở Settings > Files |

## 2. Thiết kế

- **Cấu hình** (`ConfigVersion` 3 -> 4): `ExternalEditors` (`{Id, Name, Path, Arguments}`, Id GUID "N" ổn định), `DefaultExternalEditorId`,
  `ExternalEditorSendsRawOriginal`. `ExternalEditorPath` được GIỮ và luôn phản chiếu đường dẫn của editor mặc định để bản WPF đóng băng
  (NE-7) vẫn đọc/ghi được; cửa sổ WPF chỉ sửa trường này, `ExternalEditors.ApplyLegacyPath` đưa giá trị đó vào editor mặc định.
- **Migrate** (`SettingsStore.Migrate`, `ConfigVersion < 4`): `ExternalEditorPath` khác rỗng và danh sách rỗng -> một editor tên lấy từ tên tệp
  (ví dụ `Photoshop`), đặt làm mặc định. Không tính là "repair" nên hộp thoại khởi động không hiện. Cấu hình mới hơn bản đang chạy vẫn đi theo
  cơ chế C-04 (sao lưu `config.json.newer-<ts>`).
- **Chuẩn hoá** (`ExternalEditors.Reconcile`, gọi ở Normalize, Migrate và Persist): bỏ mục thiếu Path, Id rỗng/trùng cấp Id mới, tham số rỗng
  thành mặc định, tối đa 32 editor, `DefaultExternalEditorId` lạ -> editor đầu (rỗng nếu không có editor). Export/Import settings tự mang
  các trường mới (cùng `AppSettings`).
- **Menu** (`ContextMenuModelBuilder`): chưa khai báo editor -> giữ nguyên một Command mở Settings; có >= 1 editor -> `Submenu` (id
  `ExternalEditor`, vẫn ẩn/hiện theo layout) gồm một mục Toggle mỗi editor (id `ExternalEditor.Open.<id>`, dấu chọn = mặc định, hiện
  phím `OpenInDefaultEditor`), dấu phân cách, `ExternalEditor.Add` ("Add more editors..."), `ExternalEditor.SetDefault`
  ("Set default editor..."). Mục editor tắt khi không có ảnh.
- **Hộp thoại Win32** (`Shell.Win32/Dialogs/Native`): `ExternalEditorsDialog` (danh sách + Add/Remove/Set default + tên/chương trình/tham số,
  mở từ nút "Manage editors..." ở trang Files), `TextPromptDialog` (tên hiển thị, mặc định = ProductName của tệp), `ListDialog.ForChoice`
  (danh sách chọn một cho "Set default editor..."). Trang Files giữ ô đường dẫn + Browse (nay = đường dẫn của editor mặc định) và thêm nút
  quản lý, dòng tóm tắt, công tắc RAW.
- **Chạy editor** (`MainViewModel.LaunchExternalEditor`): `Process.Start` với `UseShellExecute=false` (không qua shell), `Arguments` đã dựng,
  `WorkingDirectory` = thư mục chương trình; chặn đường dẫn chương trình không tồn tại (`Tr.DialogExternalEditorExeMissing`) và tệp ảnh mất
  (`Tr.DialogExternalEditorFileMissing`). Mở bằng phím tắt khi chưa khai báo editor nào thì mở Settings (như menu).
- **WPF**: KHÔNG sửa XAML. Mã dùng chung biên dịch và test WPF/App.Tests hiện có xanh; phím tắt mới và submenu chỉ có ở shell Win32
  (WPF không có ô nhập phím mới nên không đổi được phím; `ShortcutRouter` có thể trả lệnh mới mà `MainWindow` WPF bỏ qua).

## 3. Điều chưa kiểm / giới hạn đã biết

- **Lightroom Classic** thường KHÔNG nhận đường dẫn tệp trên dòng lệnh (chỉ mở được ảnh có trong catalog của nó); Lightroom (cloud) và
  nhiều chương trình khác thì nhận. Ghi ở gợi ý của ô tham số; nếu cần, dùng tham số riêng của chương trình hoặc "Open with" bên ngoài.
- Chưa chạy thử với các chương trình thật (Lightroom, Topaz, DxO, Capture One, DPP): chỉ kiểm cấu trúc dòng lệnh bằng unit test.
- Không có "Edit" cho một editor ngay trong menu (sửa tên/tham số làm ở Settings > Files > Manage editors...).
- Sắp xếp lại thứ tự editor chưa có (theo thứ tự thêm).

## 4. Kiểm thử

Core: `ExternalEditorsTests` (thay `{file}` với khoảng trắng/Unicode/ngoặc/dấu nháy, token lặp, migrate, repair, mặc định, RAW,
round trip, xung đột phím). App: `MainViewModelAdvancedTests.ExternalEditors`, `ExternalEditorsSettingsPresenterTests` (Load -> Save),
`OpenInDefaultEditorShortcutRouterTests`. Shell: `ExternalEditorsMenuModelTests` (0/1/N editor, đánh dấu mặc định, ẩn theo layout),
`ExternalEditorsUiTests` (nút Manage, hộp thoại, prompt, chọn mặc định, và ảnh BitBlt thật của submenu / trang Files / hộp thoại ở 96 và
144 DPI, en và vi, khẳng định không phẳng một màu).
