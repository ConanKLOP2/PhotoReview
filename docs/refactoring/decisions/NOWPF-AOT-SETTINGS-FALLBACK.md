---
id: NOWPF-AOT-SETTINGS-FALLBACK
order: 1020
summary: |-
  Bản Native AOT không có cầu WPF: lệnh Settings mở config.json bằng trình soạn thảo mặc định, các cửa sổ phụ khác báo "không có trong bản này" (CHƯA XÁC NHẬN).
---

# NOWPF-AOT-SETTINGS-FALLBACK - "cổng chưa có" tối thiểu cho cửa sổ phụ (2026-10-13)

Nền: [NOWPF-WP36](NOWPF-WP36-PUBLISH-AOT.md) (bản AOT không nạp được cầu WPF, `SecondaryWindowPorts.TryLoad` trả `null`),
[NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md) (cửa sổ phụ không WPF, việc lớn hơn).

## 1. Vấn đề

Khi cổng `ISecondaryWindowPort` vắng (bản `-p:ShellAot=true`, hoặc nạp cầu thất bại) `Win32DialogService` là no-op: người dùng bấm
Settings/Diagnostics/Recovery... mà không có phản hồi nào.

## 2. Hành vi mới

`SecondaryWindowFallback` (Shell.Win32/Dialogs), gắn vào `Win32DialogService` ở `ShellCompositionRoot`; chỉ chạy khi cổng là `null` (hoặc đọc
`Lazy.Value` ném):

| Lệnh | Khi không có cổng |
|---|---|
| Settings | `IShellLauncher.TryOpenFile(IAppPaths.ConfigFile, "open")` (ShellExecute); không có chương trình gắn `.json`/lỗi thì `notepad.exe "<đường dẫn>"`; trả `false` (không có gì được lưu). Dòng trạng thái: `status.secondaryUnavailable.settings` ("Settings window is not available in this build; opening config.json"), hoặc `...settingsFailed` (kèm đường dẫn) nếu cả hai cách đều lỗi |
| Diagnostics, SkippedFiles, Benchmark | chỉ dòng trạng thái `status.secondaryUnavailable.<tên>` |
| Recovery | dòng trạng thái nói rõ phục hồi chạy ở bản đầy đủ (không đổi dữ liệu) |
| BatchReview | dòng trạng thái, trả `false` (lô không chạy, không đổi gì) |

- Không ghi/sửa/tạo `config.json`, không đổi định dạng: chỉ mở tệp. Đường dẫn qua `IAppPaths`.
- Thông báo không chặn: ghi vào `MainViewModel.StatusText` qua `ShellNoticeSink` (singleton; `ShellStartup` gắn sau khi dựng view model).
- Lỗi launcher/thông báo bị nuốt (không ném, không treo). Có cổng thật thì hành vi cũ giữ nguyên.
- Chuỗi qua `Localizer`: 7 khóa `status.secondaryUnavailable.*` ở CUỐI `en.json`/`vi.json`/`en.notes.json`.
- Ghi chú: `ShellCompositionRoot` hiện chỉ tìm `ISecondaryWindowPort` trong DI (`GetService`), không ai đăng ký nó trong `src/`
  (chỉ test WpfBridge gọi `SecondaryWindowPorts.CreateLazy`), nên đường fallback này cũng chạy ở bản không-AOT cho tới khi việc nối cầu vào exe được làm.

## 3. Kiểm chứng

- `SecondaryWindowFallbackTests` (Shell.Tests, launcher giả): đúng đường dẫn + verb `open`, dự phòng notepad có ngoặc kép, launcher ném,
  cả hai lỗi, thông báo của từng cửa sổ, nạp cổng ném, có cổng thì không fallback, không ghi tệp.
- `ShellSecondaryWindowFallbackTests` (Shell.Integration.Tests, shell thật trên desktop ẩn, không phụ thuộc kích thước màn hình).
- Đột biến hai bước: xem mô tả PR.

## 4. Quyết định (đề xuất, CHƯA XÁC NHẬN)

- CHƯA XÁC NHẬN: làm fallback tối thiểu này (mở config.json + thông báo trạng thái) cho bản AOT trong lúc chờ WP-35.
- Phương án thay thế: (a) không làm gì (nút im lặng, khó hiểu); (b) hộp thoại `TaskDialog` thông báo (chặn, thêm một cú nhấp cho mỗi lần).
- Hoàn tác: xóa nhánh xử lý `_fallback` trong `Win32DialogService`, `SecondaryWindowFallback.cs`, `ShellNoticeSink` và 7 khóa; không ảnh hưởng dữ liệu.
