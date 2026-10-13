---
id: NOWPF-WP35-G3B-MOUSE
order: 1150
summary: |-
  WP-35 GĐ3b (CHƯA XÁC NHẬN, tiếp NOWPF-WP35-G3A): trang Mouse của SettingsDialog Win32 - 20 thuộc tính (zoom/click/bước phím/neo Fit width 1-2/giữ zoom/chuột giữa 11 mục, con lăn, kinetic, mũi tên ở mép, chuyển ảnh Fade + Ms, touchpad + khoảng vuốt) và 18 ô tick menu chuột phải dựng bằng mã theo `ContextMenuItems.Items`; mục Settings luôn bật + tooltip. Khung chỉ CỘNG THÊM 3 thứ nhỏ (hook OnActivated, SetTooltip, CurrentShortcutTexts). 19 đột biến chạy: 18 chết ở bước 1, 1 sống (M04) được lấp bằng test mới, 0 sống thật.
---

# NOWPF-WP35-G3B - trang Mouse (2026-10-13)

Theo hướng dẫn "cách thêm một trang" của [NOWPF-WP35-G3A-SETTINGS-SHELL](NOWPF-WP35-G3A-SETTINGS-SHELL.md); presenter/`SettingsFieldMap` KHÔNG đổi.

## 1. Đã làm

| Phần | File | Nội dung |
|---|---|---|
| Trang | `src/PhotoReview.Shell.Win32/Dialogs/SettingsUi/Pages/MouseSettingsPage.cs` | 3 nhóm như WPF: Zoom, "Mouse & zoom", Menu chuột phải. Thứ tự mục combobox giữ đúng chỉ số của `SettingsFormState` (middle-click = giá trị enum 0..10). Ô Ms của Fade chỉ bật khi chọn Fade. 18 ô tick: một cho mỗi `ContextMenuItems.Items`, con của Zoom thụt vào 12 dlu, chữ qua `ContextMenuItemLabel` nên đổi theo ngôn ngữ. Tóm tắt phím zoom (8 dòng) do mã đặt. |
| Danh mục | `SettingsPageCatalog.cs` | MỘT dòng `["Mouse"]`. |
| Khung (cộng thêm) | `ISettingsPageView`/`SettingsPageBase.OnActivated`, `SettingsPageContext.SetTooltip` + `CurrentShortcutTexts`, `SettingsDialog.SetControlTooltip` + gọi `OnActivated` trong `ActivatePage` | Tóm tắt phím zoom phải theo ô phím đang gõ ở trang Shortcuts (làm tươi khi chuyển tới trang Mouse); tooltip dùng lại `SetItemTooltips` (callback, đổi chữ theo ngôn ngữ). |
| Chuỗi | - | Không thêm khoá: mọi chuỗi đã có trong catalog (i18n-check xanh). |
| Test | `tests/PhotoReview.Shell.Tests/Dialogs/SettingsUi/MouseSettingsPageTests.cs` | file RIÊNG (34 ca kể cả các hàng Theory) để PR các trang song song không xung đột; `SettingsPagesTests.AnUnportedPage...` đổi sang "trang chờ đầu tiên còn lại" thay vì cố định Mouse. |

## 2. Quyết định (CHƯA XÁC NHẬN)

- Mục Settings bị khoá: WPF vô hiệu hoá ô + tooltip. Win32 không hiện tooltip của điều khiển bị disable (cửa sổ disable không nhận chuột) nên ô GIỮ bật, tự tick lại khi bấm và có tooltip. Phương án thay: disable + gắn tooltip lên hàng cha (xấu hơn).
- Lỗi `ClickZoomPercent` không có `SettingsFocusField` (presenter không đặt) nên Save từ trang khác không tự chuyển về trang Mouse (giống WPF: chỉ hiện cảnh báo). Muốn đổi phải thêm `ClickZoomPercent` vào enum + presenter (ngoài phạm vi).
- Ô Ms bị disable (transition = None) mà giá trị sai: cảnh báo hiện nhưng không focus được ô (giống WPF).

## 3. Kiểm chứng

- Ảnh chụp `settings-mouse-{en,vi}[-150][-mid|-bottom].png` (desktop ẩn `PRSetMouse`) đã xem bằng mắt: nhãn tiếng Việt dài (2-3 dòng), ghi chú 3-4 dòng, 8 dòng tóm tắt phím, 18 ô tick: không cắt chữ, không chồng ở 100 % và 150 %; thanh cuộn tối.
- Khung đã duyệt bố cục/Tab/rò handle en-vi 100 %/150 % cho mọi trang của `SettingsPresenter.Pages` (Mouse nằm trong đó): `SettingsUi` 109 ca xanh.
- Đột biến hai bước (19 đột biến thủ công chạy được, bước 1 = `MouseSettingsPageTests`; M19 "CurrentShortcutTexts trả rỗng" không dựng được vì analyzer CA1822 chặn, ý đó đã được M14/M18 phủ): lệch trường (M01-M04, M13, M20), chỉ số/thứ tự combo (M02, M08, M17), 18 ô menu bật/tắt (M05, M06), thụt (M07), mục khoá (M09, M16), phụ thuộc Fade (M10), FocusTarget (M11, M12), tóm tắt/hook khung (M14, M15, M18). Sống ở bước 1: M04 (ClickToZoom đọc từ ô KeepZoom - test sửa đồng thời mọi ô tick nên không phân biệt) -> test mới `Mouse_EachTickBox_WritesOnlyItsOwnProperty` (mỗi ô một lần), chết. 0 sống thật. Bước 2 (toàn Shell.Tests) không cần vì không còn kẻ sống sau bước 1.

## 4. Chưa kiểm

DPI hai màn hình thật, title bar tối, Narrator/IME bàn phím thật (như GĐ3a); bố cục của ba trang Performance/Files/Shortcuts do PR riêng.

Hoàn tác: revert PR (một dòng catalog + file trang; phần khung cộng thêm vô hại).
