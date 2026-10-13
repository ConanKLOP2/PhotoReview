---
id: NOWPF-WP35-G3B-SHORTCUTS
order: 1190
summary: |-
  WP-35 GĐ3b (CHƯA XÁC NHẬN, phương án B của spike S3): trang SHORTCUTS của Settings Win32 - 27 ô bắt phím (EDIT chỉ đọc, subclass WM_KEYDOWN/WM_SYSKEYDOWN, cùng bộ ghi tên phím của WPF qua KeyIdMapping + SettingsPresenter.CaptureShortcut, không chữ/IME lọt vào ô), 14 nút xoá cho phím tuỳ chọn, "Khôi phục phím mặc định", cảnh báo trùng phím trực tiếp + dấu cảnh báo theo ô, nút mở Action profiles (modal lồng, JSON action dùng chung với trang khác). Khung mở rộng CỘNG THÊM (KeyGrid/Warning, ô bắt phím, Context.ActionsJson/EditActionProfiles). 28 đột biến: 25 chết, 3 sống đều tương đương/không quan sát được. 0 cảnh báo ILC; không nạp WPF.
---

# NOWPF-WP35-G3B - trang Shortcuts của Settings Win32 (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G3A-SETTINGS-SHELL](NOWPF-WP35-G3A-SETTINGS-SHELL.md) (hợp đồng trang). Presenter/FieldMap
KHÔNG đổi ([NOWPF-WP35-G0-PRESENTERS](NOWPF-WP35-G0-PRESENTERS.md)). Bản WPF và cầu WPF giữ nguyên.

## 1. Đã làm

| Phần | File (`src/`) | Nội dung |
|---|---|---|
| Trang | `Shell.Win32/Dialogs/SettingsUi/Pages/ShortcutsSettingsPage.cs` | 3 nhóm Navigation (8) / View (13) / Actions (6) = 27 hàng theo thứ tự của `SettingsFieldMap.Shortcuts`; nhãn = tên lệnh; nút xoá "×" cho 14 phím `ShortcutMappings.OptionalNames` (tooltip = chuỗi `*.clearAutomationName` cũ); nút "Khôi phục phím mặc định"; dòng cảnh báo (`DuplicateShortcutWarning`, cùng phép kiểm của Save, màu cảnh báo); dấu "⚠" trước nhãn của ô rỗng-bắt-buộc / sai tên / trùng (`FindProblems`); nút "Edit action profiles" |
| Builder | `.../SettingsPageBuilder.cs` | THÊM `KeyGrid` (lưới nhãn + ô bắt phím + nút xoá, 1-2 cột theo bề rộng, ô thẳng cột, hàng cao theo nhãn xuống dòng) và `Warning` |
| Khung | `.../SettingsDialog.cs` | subclass ô bắt phím (WM_KEYDOWN/WM_SYSKEYDOWN -> `KeyIdMapping.FromVirtualKey` -> `SettingsPresenter.CaptureShortcut`; nuốt WM_CHAR/IME/dán/cắt); `OnDialogKey` đưa phím vào ô TRƯỚC `IsDialogMessage` (Enter không bấm Save, mũi tên/Space không thành điều hướng); gỡ IME khỏi ô (`ImmAssociateContext`) + đọc phím thật khi VK_PROCESSKEY; màu ô (EDIT chỉ đọc gửi WM_CTLCOLORSTATIC) như ô nhập; `ActionsJson` làm việc dùng chung (`SetActionsJson` nạp lại các trang đã tạo); `SettingsDialogOptions.ShowActionProfiles` |
| Ngữ cảnh | `.../SettingsPageContext.cs` | THÊM `SetTooltip`, `ActionsJson`, `CollectActionsJson`, `CommitActionsJson`, `EditActionProfiles` |
| Cổng | `Dialogs/Native/Win32SecondaryWindowPort.cs` | một dòng: `ShowActionProfiles = baseOptions.ShowActionProfiles ?? ShowActionProfiles` (modal lồng, owner = cửa sổ Settings) |
| Interop | `Shell.Interop/Win32/KeyCaptureNative.cs` | `ImmAssociateContext`, `ImmGetVirtualKey` (LibraryImport) |
| Danh mục | `SettingsPageCatalog.cs` | một dòng `["Shortcuts"]` |
| Chuỗi | `Languages/{en,vi,en.notes}.json` | THÊM Ở CUỐI: `settings.shortcuts.resetKeys` |

Thuộc tính sở hữu: `Shortcuts` (27 phím) và `Actions` (JSON; trang Files KHÔNG có ô JSON). `MoveCopyReuseLastFolder` do trang Files khai báo (XAML đặt nó cạnh
Shortcuts nhưng FieldMap chỉ cho một trang sở hữu).

## 2. Hành vi giữ đúng WPF (và chỗ khác có chủ ý)

1. Ô ghi TÊN PHÍM chuẩn hoá, KHÔNG kèm Ctrl/Shift/Alt (chuỗi phím tắt là một tên phím). Ctrl/Shift/Alt/Win, Tab, Esc không bị bắt (Tab đổi focus, Esc huỷ).
   Backspace/Delete là phím bình thường (Delete là phím mặc định của "Move to Recycle Bin"); xoá phím tuỳ chọn bằng nút "×" như WPF, KHÔNG bằng Backspace.
2. Alt+phím (WM_SYSKEYDOWN) cũng được bắt và không đóng/mở gì (như `Key.System` của WPF). Enter số = Enter.
3. Chữ/IME không bao giờ lọt vào ô: EDIT chỉ đọc + subclass nuốt WM_CHAR/WM_IME_*/WM_PASTE...; IME bị gỡ khỏi ô.
4. Ngoài WPF: dấu "⚠" theo ô và nút "Khôi phục phím mặc định" (chỉ 27 phím); nút xoá dùng "×" (U+00D7) thay "✕" (rơi font, viền ClearType ở 100 %).
5. Cảnh báo trực tiếp gọi presenter nên tính cả phím của action; tên trong thông báo là tên thuộc tính (thông báo của Core, như WPF).

## 3. Kiểm chứng

Test mới `Shell.Tests/Dialogs/SettingsUi/SettingsShortcutsPageTests.cs`: 45 phương thức (+ 1 ca chụp ảnh thủ công). Phủ: 27 hàng/14 nút xoá/thứ tự hàng, ô chỉ-đọc/TabStop/thứ tự Tab/
nhãn đứng ngay trước ô (tên truy cập), Load->Collect->Save từng phím, hơn 60 phím (F1-24, Space, mũi tên, Home/End, PageUp/Down, numpad, Oem...) qua cả subclass và bộ lọc vòng lặp,
vòng lặp mọi VK 1..255 qua `ShortcutKeyName`, Alt+phím, modifier không bắt / tổ hợp chỉ ghi phím cuối, Tab/Esc, Enter không Save, chữ + IME không lọt, xoá, khôi phục,
Defaults/Reset trang, xung đột (bắt buộc/tuỳ chọn/bí danh Return-Enter/hoa-thường/action), Action profiles lồng (Apply/Cancel/lỗi/qua cổng thật), import đổi JSON action,
đổi ngôn ngữ, bố cục en/vi ở 100 %/150 %, cuộn tới ô cuối, màu, rò GDI/USER sau 5 lượt.

Chạy 2026-10-13 (desktop ẩn `PRSetKeys`, filter CI): `Shell.Tests` 1 349, `Shell.Integration.Tests` 150 (1 ca đo độ trễ `InputLatency_ArrowPan` đỏ lúc máy tải, đạt khi chạy riêng),
`WpfBridge.Tests` 39, `Architecture.Tests` 158, `App.Tests` 2 583. `PhotoReview.slnx -c Release`: 0 cảnh báo. Lặp 50 lượt bộ Shortcuts (45 ca/lượt): 50 đạt, 0 lỗi, không lượt nào treo.
ILC: `tools/check-aot-warnings.ps1 -RequireToolchain` = 0 cảnh báo (PhotoReview.exe 15,2 MB). Không nạp WPF (`LazyLoadTests` giữ nguyên, đạt).

Ảnh `PrintWindow` (hộp thoại ẩn, comctl32 v6) `settings-shortcuts-{top,top-150,conflict,bottom,bottom-150}-{en,vi}.png`: đã xem; hai cột, ô thẳng cột, nhãn Việt xuống dòng không
cắt, ⚠ + dòng cảnh báo vàng, nút "×" rõ, 150 % không chồng.

### Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

28 chạy (1 không dựng được: M11 đã thay bằng M10). Bước 1 = `SettingsShortcutsPageTests`; kẻ sống chạy bước 2 = toàn `Shell.Tests`.

| Nhóm | Đột biến | Kết quả |
|---|---|---|
| Collect/Load | M01 Next đọc ô Previous; M02 Load bỏ qua form | chết bước 1 |
| Xoá/Reset | M03 Reset bỏ phím tuỳ chọn; M04 nút xoá không làm gì | chết |
| Xung đột | M05 rỗng-bắt-buộc không bị gắn cờ; M06 so trùng phân biệt hoa thường; M07 cặp không là xung đột; M08 mất dấu ⚠; M09 cảnh báo không cập nhật; M12 bấm phím không làm mới | chết |
| Action profiles | M10 kết quả trình sửa bị bỏ; M20 SetActionsJson không giữ; M21 form làm việc bỏ JSON; M22 Import giữ JSON cũ; M23 cổng không nối trình sửa; M28 mở không có owner | chết |
| Bắt phím | M14 bỏ luật reserved/chuẩn hoá; M16 bộ lọc không bắt (Enter = Save); M17 subclass bỏ WM_SYSKEYDOWN; M18 ô không chỉ-đọc; M29 mọi điều khiển là ô bắt phím | chết |
| Giao diện | M19 hàng lưới bỏ chiều cao nhãn (lúc đầu SỐNG -> thêm `KeyGridRow_IsAsTall...` + khoảng cách nhãn, chết); M24 ô sơn như nhãn; M25 nền ô sai; M26 dòng cảnh báo không màu cảnh báo | chết |
| Sống | M13 đổi ngôn ngữ không xoá cảnh báo cũ (TƯƠNG ĐƯƠNG: `Refresh` so với thông báo mới nên vẫn cập nhật); M15 bộ lọc bỏ WM_SYSKEYDOWN (TƯƠNG ĐƯƠNG: `IsDialogMessage` chuyển phím hệ thống tới subclass); M27 không gỡ IME (không quan sát được: `ImmGetContext` luôn 0 trên máy này, giữ khẳng định) | 3 |

Bước 2 cho M15/M19/M27 báo đỏ ở `DiagnosticsAndListDialogTests.List_WithFiftyThousandLines...` (quá 60 s khi máy tải, không liên quan; không tính là chết).

## 4. CHƯA kiểm được bằng máy / giới hạn

- IME tiếng Việt thật (Telex/TSF), bàn phím thật qua RDP: chỉ kiểm được bằng thông điệp (WM_CHAR/WM_IME_*, VK_PROCESSKEY không phím thật); IME bị gỡ khỏi ô nhưng chưa quan sát được.
- Narrator/UIA: tên truy cập của ô = nhãn đứng ngay trước (chuẩn hộp thoại); nút "×" chỉ có tooltip + chữ "×" (không có tên riêng - cần IAccessible), tooltip KHÔNG tự đổi
  khi đổi ngôn ngữ giữa phiên (đặt lúc tạo trang).
- Dòng cảnh báo trực tiếp không thấy sửa tay chưa lưu ở ô JSON của trang Files (Save vẫn kiểm đủ).
- Title bar tối/DPI hai màn hình thật: như GĐ3a.

## 5. Quyết định (CHƯA XÁC NHẬN) và hoàn tác

- CHƯA XÁC NHẬN: Backspace/Delete là phím (không phải "xoá ô") để giữ đúng WPF và vì Delete là phím mặc định; xoá bằng nút "×".
- CHƯA XÁC NHẬN: dấu ⚠ + nút "Khôi phục phím mặc định" là bổ sung ngoài WPF.
- Hoàn tác: revert PR (một dòng `SettingsPageCatalog` quay trang về ghi chú "chưa có"; khung CỘNG THÊM không ảnh hưởng trang khác).
