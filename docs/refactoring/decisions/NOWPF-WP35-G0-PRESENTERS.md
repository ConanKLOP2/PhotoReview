---
id: NOWPF-WP35-G0
order: 1020
summary: |-
  WP-35 GĐ0 (PR 1): tách logic của 6 cửa sổ phụ (Settings, ActionProfiles, ClickZoomCustom, Diagnostics, BatchReview, SkippedFiles) khỏi WPF sang presenter không-WPF trong App.Shared; cửa sổ WPF chỉ còn là view mỏng, hành vi không đổi. Recovery và Benchmark ở PR riêng.
---

# NOWPF-WP35-G0 - tách presenter khỏi WPF (PR 1, 2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); căn cứ: [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md) mục 4, giai đoạn 0
(khuyến nghị B "hộp thoại Win32 gốc", CHƯA XÁC NHẬN). GĐ0 có lợi cho mọi phương án (A/B/D/E/F) nên làm trước, KHÔNG đổi giao diện/hành vi.

## 1. Đã làm

| Cửa sổ | Trước (code-behind) | Sau: view WPF | Presenter / model (App.Shared, `PhotoReview.App.Presenters`, `internal`) |
|---|---|---|---|
| `SettingsWindow` | 1 257 dòng | 765 dòng (chép điều khiển <-> `SettingsFormState`, bật/tắt, MessageBox, hộp thoại tệp) | `SettingsPresenter` (636) + `SettingsFieldMap` (411) + `SettingsFormState` (128) + `ActionListJson` (53) |
| `ActionProfilesWindow` | 198 | 101 | `ActionProfilesPresenter` (221) |
| `ClickZoomCustomDialog` | 40 | 39 | `ClickZoomCustomPresenter` |
| `DiagnosticsWindow` | 85 | 31 | `DiagnosticsPresenter` |
| `BatchReviewWindow` | 32 | 25 | `BatchReviewPresenter` |
| `SkippedFilesWindow` | 67 | 60 | `SkippedFilesPresenter` |
| 3 cửa sổ nhỏ cuối | | | cùng file `SecondaryDialogPresenters.cs` (160) |

- **`SettingsFieldMap` là bảng ánh xạ hai chiều duy nhất** (thay ba danh sách viết tay LoadFields / Save_Click / BuildProbeSettings): mỗi hàng khai báo các thuộc tính
  `AppSettings` nó sở hữu, hàm nạp và hàm áp dụng. **Thứ tự hàng = thứ tự Save cũ** (hàng nào bị từ chối thì dừng; các hàng trước đã ghi vào bản sao, hộp thoại vẫn mở).
  Combo là chỉ số mục theo thứ tự cố định của hộp thoại (thứ tự là hợp đồng); ô văn bản là chuỗi như người dùng gõ (Save kiểm tra chuỗi, không phải số).
- `SettingsPresenter`: nạp, `TrySave` (kiểm tra phím tắt bắt buộc/tùy chọn, trùng, JSON action, đích action, store, đổi ngôn ngữ), mặc định, nhập/xuất, cảnh báo trùng phím
  trực tiếp (có cache parse), bắt phím (`CaptureShortcut`), thanh RAM (`RamCacheRange`), kiểm tra cập nhật, tích hợp Explorer, bộ chọn ngôn ngữ, trang nhớ trong phiên.
- JSON action dùng source-gen (`ActionListJsonContext`, App.Shared là AOT-compatible): cùng tùy chọn cũ (thụt lề + escape nới lỏng cho ô văn bản; xuất tệp dùng escape mặc định).
- Hợp đồng: không đổi khoá `config.json`, không đổi kiểu public/đóng băng (mọi kiểu mới `internal`, đã có InternalsVisibleTo cho WpfWindows + test) nên không phải tái sinh `Approved/contracts`.
- Test cũ: 156 ca UI (Settings*, ActionProfiles*, ClickZoomCustom, ZoomPromptSeam, WindowLocalization, AccessibilityNames, ReviewRound7Window, ZoomContextMenu) chạy qua view, xanh;
  chỉ đổi chỗ gọi hàm tĩnh đã dời (`ActionProfilesPresenter.HasInvalidActions`, `DiagnosticsPresenter.ReasonText`, ...) và hai chỗ phản chiếu (`s_lastPageKey`, `_updateUrl`).

## 2. Test mới (không cần cửa sổ, `tests/PhotoReview.App.Tests/Presenters`)

80 ca: `SettingsFieldMapTests` (9: mọi thuộc tính `AppSettings` có hàng hoặc nằm trong `NotEditedInDialog` có lý do; hai danh sách chỉ nêu thuộc tính thật; accessor phím tắt phủ đủ
`ShortcutMappings` trừ alias cũ; vòng Load->Save cho từng thuộc tính vô hướng, từng phím tắt, menu chuột phải, cờ EXIF/tiêu đề), `SettingsPresenterTests` (52: thứ tự kiểm tra và
ô cần focus, tất-cả-hoặc-không ở preload/auto-hide, phím tắt, JSON action, mặc định, nhập/xuất, RAM, kiểm tra cập nhật, Explorer, trang) và `SecondaryDialogPresenterTests` (19).

## 3. Đột biến (hai bước, `docs/MUTATION-TESTING.md`)

Xem mục "Kết quả đột biến" cuối tệp (điền sau khi chạy).

## 4. Còn lại

- PR 2: Recovery (+RecoveryPathPanel/RecoveryPresenter) - an toàn dữ liệu, bộ rộng cho mọi đột biến sống sót, không chạm Thùng rác thật.
- PR 3 (nếu còn sức): Benchmark (modeless, đóng giữa chừng phải huỷ).
- GĐ1+ (khung Win32) chưa làm; vẫn CHƯA XÁC NHẬN phương án B.
- Hạn chế đã biết: `SettingsFormState` mang chỉ số combo, nên view mới (Win32) phải giữ thứ tự mục; có thể đổi sang enum ở GĐ1 nếu muốn.

## 5. Hoàn tác

Thuần tái cấu trúc: `git revert` PR này; không đổi định dạng dữ liệu hay khoá cấu hình.
