---
id: NOWPF-WP35-G5
order: 1210
summary: |-
  WP-35 GĐ5 (CHƯA XÁC NHẬN): gỡ cầu WPF khỏi shell Win32. Mọi cửa sổ phụ của PhotoReview.exe (Settings, Recovery, Diagnostics, Benchmark, SkippedFiles, BatchReview, ZoomPrompt, ActionProfiles) đã là hộp thoại Win32 gốc nên Shell.Win32 không còn tham chiếu Shell.WpfBridge: xoá WpfBridgeLoader/SecondaryWindowPorts/SecondaryWindowPortLoader, SecondaryWindowRouter chỉ còn cổng Win32 -> cổng tạm, bỏ cờ PHOTOREVIEW_SETTINGS_WIN32. Project Shell.WpfBridge và App.WpfWindows GIỮ (bản WPF App.exe dùng WpfWindows; WpfBridge chỉ còn test của nó, chờ WP-37 do người dùng duyệt). Gói phát hành không còn PhotoReview.Shell.WpfBridge.dll; bản hybrid và Native AOT cùng một mã cửa sổ phụ.
---

# NOWPF-WP35-G5 - gỡ cầu WPF khỏi shell Win32 (2026-10-13)

Thẻ: WP-35 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiếp [NOWPF-WP35-G4-RECOVERY-WIN32](NOWPF-WP35-G4-RECOVERY-WIN32.md) và
[NOWPF-WP35-G3B-PAGE-MOUSE](NOWPF-WP35-G3B-PAGE-MOUSE.md) mục 5 (Settings đủ 7 trang, `SettingsPageCatalog.IsComplete` = true).
Lộ trình: [NOWPF-WP35-SPIKE-S3](NOWPF-WP35-SPIKE-S3.md). Cầu cũ: [NOWPF-SHELL-WIRE-WPF-BRIDGE](NOWPF-SHELL-WIRE-WPF-BRIDGE.md).

## 1. Kiểm kê: còn cửa sổ nào đi qua cầu từ `PhotoReview.exe`?

`SecondaryWindowKind` có 8 giá trị. `Win32SecondaryWindowPort.Supports` trả true cho CẢ 8 (Settings khi `SettingsPageCatalog.IsComplete`,
luôn true từ GĐ3b; 7 giá trị kia vô điều kiện). `SecondaryWindowRouter.Pick` chỉ tới cầu khi cổng Win32 trả false, nên trên đường chạy
thật KHÔNG còn cửa sổ nào tới `Shell.WpfBridge` / `App.WpfWindows`. Bằng chứng chạy được: probe tiến trình sạch `--probe-native`
(LazyLoadTests) mở Diagnostics, SkippedFiles, BatchReview, ZoomPrompt, Benchmark, ActionProfiles, Recovery, Settings qua DI thật mà không
nạp PresentationFramework/Core, WindowsBase, System.Xaml, App.WpfWindows, Shell.WpfBridge; và `ShellSecondaryWindowBridgeTests` (shell
thật, desktop ẩn) kiểm cùng điều đó cho Settings/Recovery/Diagnostics/Zoom/Benchmark rồi đóng shell với cửa sổ còn mở.

## 2. Thay đổi

| Phần | Nội dung |
|---|---|
| `Shell.Win32.csproj` | bỏ `ProjectReference` tới Shell.WpfBridge (và nhóm `ShellAot` điều kiện, `Compile Remove` của loader): Shell.Win32 không còn cạnh WPF nào; hybrid và AOT cùng mã. |
| `Dialogs/WpfBridgeLoader.cs`, `SecondaryWindowPorts.cs` | xoá (lớp DUY NHẤT chạm cầu). `SecondaryWindowPortHost` chuyển sang file riêng (`SecondaryWindowPortHost.cs`), vẫn là chủ sở hữu duy nhất của cổng. |
| `ShellCompositionRoot` | bỏ `SecondaryWindowPortLoader`; `SecondaryWindowRouter(new Win32SecondaryWindowPort(...), fallback)` (hai tham số). |
| `SecondaryWindowRouter` | bỏ tham số/Lazy cầu; cửa sổ cổng Win32 không làm được (chỉ còn Settings khi catalog thiếu trang) rơi về `SecondaryWindowFallback` như cũ. |
| Cờ `PHOTOREVIEW_SETTINGS_WIN32` | gỡ (vô nghĩa khi catalog đủ: chỉ có thể BẬT thứ đã bật). `SettingsEnabled` = seam test hoặc `IsComplete`. Chuỗi `settings.pagePending` (en/vi) không còn nhắc cờ. |
| Gói phát hành | `verify-release.ps1` không còn yêu cầu `PhotoReview.Shell.WpfBridge.dll` (vẫn cấm nó trong thư mục AOT); `App.WpfWindows.dll` vẫn cần (bản WPF). |
| Kiểm kiến trúc | `ShellRulesTests`: không kiểu nào của Shell.Win32 phụ thuộc WpfBridge, csproj không tham chiếu nó, không còn `WpfBridgeLoader` (L-SHELL không còn ngoại lệ). `docs/architecture.md` cập nhật. |
| Test của cầu | `Shell.WpfBridge.Tests` vẫn chạy cửa sổ WPF thật qua `WpfBridgePort` (bộ nối CHỈ trong test, chuyển nguyên từ `WpfBridgeLoader`) để hợp đồng cầu còn được bảo vệ tới WP-37. |

Giữ nguyên: `src/PhotoReview.App` (bản WPF, `PhotoReview.App.exe`) và `App.WpfWindows` (cửa sổ phụ WPF của nó); `Shell.WpfBridge` (mồ côi
trong `src/`: chỉ `Shell.WpfBridge.Tests` và `Architecture.Tests` tham chiếu). KHÔNG xoá project WPF nào (WP-37 cần người dùng đồng ý).

## 3. Quyết định (CHƯA XÁC NHẬN)

| ID | Quyết định | Phương án khác | Hoàn tác |
|---|---|---|---|
| WP35-G5-A | Gỡ hẳn tham chiếu Shell.Win32 -> Shell.WpfBridge ngay khi mọi cửa sổ phụ có bản Win32 (không giữ cầu làm "lưới an toàn" trong bản hybrid). | (1) Giữ cầu nạp lười làm lưới cho Settings: tốn ~20 MB WindowsDesktop trong gói hybrid và một đường mã không còn ai chạy; (2) gỡ chỉ ở AOT (đã có từ WP-36). | Revert PR này: khôi phục `WpfBridgeLoader`, `SecondaryWindowPorts` và tham chiếu csproj. |
| WP35-G5-B | Bỏ cờ môi trường `PHOTOREVIEW_SETTINGS_WIN32`. | Giữ cờ vô hại (chỉ bật được thứ đã bật) - thêm một biến môi trường vô nghĩa vào tài liệu. | Revert. |
| WP35-G5-C | Giữ project `Shell.WpfBridge` và test của nó (mồ côi) cho tới WP-37. | Xoá luôn (mất bộ test hợp đồng cầu; vượt phạm vi, WP-37 cần người dùng duyệt). | - |

## 4. Kiểm chứng

Xem phần báo cáo của PR (số test thật). Điểm cần để ý: nếu sau này thêm một mục vào `SettingsPresenter.Pages` mà chưa có trang Win32,
Settings sẽ rơi về cổng tạm (mở `config.json`) thay vì cầu WPF - hành vi có chủ đích (test `SecondaryWindowRoutingTests`).
