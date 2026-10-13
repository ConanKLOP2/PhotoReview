---
id: NOWPF-SHELL-WIRE-WPF-BRIDGE
order: 1070
summary: |-
  Nối cầu WPF vào exe Win32 bản không-AOT: `ShellCompositionRoot` đăng ký cổng cửa sổ phụ nạp lười (`SecondaryWindowPortHost` qua `SecondaryWindowPorts.TryLoad`) dùng chung cho `IDialogService` và `IZoomPromptService` (`Win32ZoomPromptService`), nên Settings/Diagnostics/Recovery/SkippedFiles/BatchReview/Benchmark/ClickZoomCustom MỞ được cửa sổ WPF thật (owner = HWND shell). Cửa sổ phụ đóng khi shell `Closing` (HWND còn sống) và trong `Cleanup` trước cửa sổ chủ; sửa lỗi treo khi shell thoát với hộp modal còn mở. Bản AOT vẫn không có cầu (cổng tạm). WPF vẫn không nạp trước cửa sổ phụ đầu tiên (CHƯA XÁC NHẬN).
---

# NOWPF-SHELL-WIRE-WPF-BRIDGE - nối cầu cửa sổ phụ WPF vào shell (2026-10-13)

Nền: [NOWPF-WP19B](NOWPF-WP19B-WPF-BRIDGE.md) (cầu + hướng dẫn nối, mục 4), [NOWPF-AOT-SETTINGS-FALLBACK](NOWPF-AOT-SETTINGS-FALLBACK.md) (#481: phát hiện
không ai đăng ký `ISecondaryWindowPort` trong `src/`), [NOWPF-WP20](NOWPF-WP20-STARTUP-SHELL.md), [NOWPF-WP23](NOWPF-WP23-OVERLAY-HUD.md) (nút Settings/ToolsMenu).

## 1. Vấn đề

`ShellCompositionRoot` chỉ `GetService<ISecondaryWindowPort>()` và không có `IZoomPromptService`: ở bản hybrid R2R (không-AOT) mọi lệnh Settings/
Diagnostics/Recovery/SkippedFiles/BatchReview/Benchmark/ClickZoomCustom rơi vào cổng tạm "không có trong bản này", dù cầu `Shell.WpfBridge` đã có và test.

## 2. Thay đổi (chỉ Shell.Win32 Dialogs/Startup + một chỗ ở WpfBridge)

| Phần | Nội dung |
|---|---|
| `ShellHostServices` | thêm `MessageLoop? Loop` (tham số cuối, mặc định null); `ShellStartup` truyền vòng lặp thật để cầu gắn `IRawMessageFilter` cho cửa sổ modeless (Tab). |
| `SecondaryWindowPortHost` (mới, Dialogs) | chủ sở hữu duy nhất của `Lazy<ISecondaryWindowPort?>`; `Dispose` đóng cửa sổ phụ + gỡ bộ lọc phím CHỈ khi cầu đã nạp (không nạp WPF để dọn). Singleton trong DI nên container cũng dọn. |
| `SecondaryWindowPortLoader` (delegate internal) | mặc định `SecondaryWindowPorts.TryLoad`; test thay bằng `(_, _) => null` để mô phỏng bản AOT. `ISecondaryWindowPort` đăng ký sẵn (test) vẫn thắng cầu thật. |
| Đăng ký | `IDialogService` dùng `host.Port`; `IZoomPromptService = Win32ZoomPromptService(owner, host.Port)` (trước đó không đăng ký nên `ZoomPrompt` của `ShellMainWindow` là null). `Win32ZoomPromptService` bắt lỗi nạp/đã dọn -> null (như huỷ). |
| Dọn (`ShellStartup`) | `CloseSecondaryWindows()`: (a) ở `window.Closing` đăng ký SAU `ShellMainWindow` và chỉ khi không `Cancel` (đang có file action thì giữ nguyên); (b) dự phòng ở `Closed`, `Cleanup` (trước cửa sổ chủ), smoke-exit, `FailStartup`. Idempotent. |
| `WpfSession` | theo dõi hộp modal đang trong `ShowDialog` (`Modal`), `CloseAll` đóng cả modal (ngược thứ tự) lẫn modeless. |
| Bản AOT | `TryLoad` = null (`SHELL_AOT`): `Lazy` = null -> cổng tạm như #481; không đổi. |

Lệnh từ toolbar/menu/phím tắt đã đi qua `MainViewModel.ShowXxx()` -> `IDialogService`; `SettingsStore.Changed` đã được `ShellMainWindow` (router phím,
overlay, bước pan) và `ShellStartup` (log) nghe từ trước, nên chỉ cần cổng thật là Lưu áp dụng như bản WPF.

## 3. Lỗi tìm thấy khi kiểm

Đóng shell (WM_CLOSE) khi một hộp modal WPF đang chạy vòng lồng KHÔNG thoát (treo): HWND owner bị huỷ trước nên WPF không nhận WM_CLOSE của hộp,
vòng lồng nuốt `WM_QUIT`. Sửa: đóng cửa sổ phụ ở `Closing` khi HWND shell còn sống (mục 2). Test `ShellSecondaryWindowBridgeTests` bắt đúng lỗi này.

## 4. Kiểm chứng

- `ShellWiringTests` (WpfBridge.Tests, DI THẬT `ShellCompositionRoot` trên UI thread + vòng lặp thật): đăng ký lười chung, Settings qua `IDialogService`
  (owner = shell, modal, Lưu -> `Changed` + `config.json`), đóng không lưu -> false/không `Changed`, Recovery không đổi dữ liệu, Diagnostics/SkippedFiles/
  BatchReview, ZoomPrompt Huỷ -> null / OK 175, Benchmark modeless + Tab tới `ComponentDispatcher` qua bộ lọc của vòng lặp shell, Dispose đóng modeless + modal và gỡ bộ lọc,
  thread khác bị từ chối, cổng đăng ký sẵn thắng và cầu vắng -> cổng tạm.
- `ShellSecondaryWindowBridgeTests` (Shell.Integration.Tests, shell thật, một lần chạy vì `Application` WPF một-cho-tiến-trình): Settings Lưu, Recovery, Diagnostics,
  Zoom Huỷ/OK, Benchmark + hộp modal rồi đóng shell: thoát mã 0, modal trả về, không treo.
- `ShellSecondaryWindowFallbackTests` đổi sang `SecondaryWindowPortLoader` null (mô phỏng AOT).

## 5. Số đo khởi động (R2R publish, `tools/diag/startup-probe.ps1`, xen kẽ)

Trước = `origin/master` `8e1aeaf6`, sau = nhánh này. Máy bận 88-100 % CPU (nhiều agent), nên số tuyệt đối đội lên; hai lô hoán đổi ô để loại sai lệch vị trí:

| Lô | Ảnh đầu median trước | sau | Cửa sổ thấy được trước | sau |
|---|---:|---:|---:|---:|
| 8 vòng (+1 bỏ), seed 1 | 883 | 903 (+2,3 %) | 725 | 830 |
| 10 vòng (+1 bỏ), seed 7, hoán đổi | 1145 | 821 (-28 %) | 1064 | 646 |

Hai lô đổi chiều theo nhiễu tải: KHÔNG có hồi quy phân biệt được > 5 %; thay đổi không nằm trên đường nóng (thêm 3 đăng ký lười, một closure). Số assembly WPF nạp
ở khởi động không đổi: `LazyLoadTests` (probe tiến trình sạch) vẫn xanh - danh sách WPF rỗng tới khi mở cửa sổ phụ đầu tiên.

## 6. Đột biến (hai bước, docs/MUTATION-TESTING.md)

16 đột biến thủ công (script ở scratchpad, build + test desktop ẩn). Bước 1 = `ShellWiringTests` (WpfBridge.Tests) rồi `ShellSecondaryWindow*` (Shell.Integration.Tests):

| # | Đột biến | Kết quả |
|---|---|---|
| M01 | bỏ đăng ký `IZoomPromptService` | chết bước 1 (ShellWiringTests) |
| M02 | cầu không bao giờ nạp (loader -> null) | chết bước 1 |
| M03 | `SecondaryWindowPortHost.Dispose` không làm gì (không dispose) | chết bước 1 |
| M04 | owner sai (`() => 0`) cho `IDialogService` | chết bước 1 |
| M05 | không truyền `MessageLoop` (không gắn bộ lọc phím) | chết bước 1 |
| M06 | Settings không modal (`Show` thay `ShowDialog`) | chết bước 1 |
| M07 | nạp không lười (ép `Lazy.Value` khi dựng host) | chết bước 1 |
| M08 | `SettingsStore.Save` không bắn `Changed` | chết bước 1 |
| M09 | bỏ đóng cửa sổ phụ ở `Closing` (lỗi treo của mục 3) | chết bước 1 (Shell.Integration: shell không thoát) |
| M10 | bỏ `CloseSecondaryWindows()` trong `Cleanup` | sống bước 1 và bước 2 (cả Shell.Integration.Tests, 142 test): TƯƠNG ĐƯƠNG vì `Closing` + `Closed` đã đóng; lớp dự phòng cho đường không qua WM_CLOSE |
| M11 | không theo dõi hộp modal (`Modal.Add`) | chết bước 1 |
| M12 | `Win32DialogService` bỏ qua cổng (luôn null) | chết bước 1 |
| M13 | cổng `ISecondaryWindowPort` đăng ký sẵn bị bỏ qua | chết bước 1 |
| M14 | zoom prompt owner sai (0) | chết bước 1 |
| M15 | `Closing` bỏ qua `Cancel` (đóng cửa sổ phụ dù shell ở lại) | SỐNG lần đầu; thêm `ShellClosingSecondaryWindowsTests` (tách `CloseSecondaryWindowsUnlessCancelled`) -> chết |
| M16 | `CloseAll` không đóng cửa sổ modeless | chết bước 1 |

Tổng: 16 chạy; 14 chết ở bước 1, 1 sống rồi bị test mới giết (M15), 1 tương đương (M10). Lượt đầu M02 mở notepad thật của máy (cổng vắng -> launcher thật): đã thêm
launcher giả vào test để đột biến cổng vắng không chạm tiến trình thật.

## 7. Quyết định

- CHƯA XÁC NHẬN: bật cầu WPF cho bản hybrid R2R (cửa sổ phụ là cửa sổ WPF thật, mở lần đầu ~0,5 s vì nạp WPF) thay vì chờ cửa sổ phụ không-WPF (WP-35).
  Phương án thay thế: (a) giữ cổng tạm cho tới WP-35 (nút im/báo "không có"); (b) bật có công tắc cấu hình. Đề xuất: làm như đã làm; hoàn tác = bỏ ba đăng ký
  và `CloseSecondaryWindows`, fallback #481 trả lời lại.
