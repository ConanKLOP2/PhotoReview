---
id: NOWPF-WP25
order: 910
summary: |-
  WP-25 (đợt 3 NO-WPF, 2026-10-12): F11/đa màn hình/DPI/thanh tiêu đề tối trên cửa sổ shell Win32. `ViewerState.IsFullscreen` (WP-22 chỉ đặt cờ) áp lên HWND qua `FullscreenController` của App.Shared (một SetWindowPos lên cả monitor, trạng thái HĐH giữ nguyên, thoát trả đúng rect; monitor đã rút -> work area còn lại). Đóng khi đang F11 lưu trạng thái + rect Normal có trước F11 (R7-10). Thêm so với WPF: ép lại cửa sổ F11 lên monitor hiện tại khi WM_DISPLAYCHANGE/WM_DPICHANGED; bề mặt D2D nhận DPI mới kể cả khi số pixel không đổi. Thanh tiêu đề tối (DWMWA 20/19 + caption #171717). Test: 20 test cửa sổ thật trên desktop ẩn + 12 unit; mutation 22/23 bị bắt (1 tương đương); F11 vào/ra p50 3,4/4,6 ms (WARP). Chưa kiểm trên máy hai màn hình thật.
---

# NOWPF-WP25 - F11, đa màn hình, DPI, thanh tiêu đề tối (2026-10-12)

Thẻ: WP-25 trong [NO-WPF-EXEC-PLAN-WP](NO-WPF-EXEC-PLAN-WP.md); tiền đề: WP-08 ([placement/fullscreen logic](NOWPF-WP08-PLACEMENT.md)),
WP-14 ([cửa sổ, WM_DPICHANGED](NOWPF-WP14-WINDOW-LOOP.md)), WP-20 ([startup, ShellPlacement, cloak](NOWPF-WP20-STARTUP-SHELL.md)),
WP-21 ([F11 bật `Present(1)` qua `FramePacer`](NOWPF-WP21-PRESENTATION.md)), WP-22 ([F11 chỉ đặt `ViewerState.IsFullscreen`](NOWPF-WP22-INPUT.md)).
App WPF (`PhotoReview.App`) **không đổi**; liên kết `.jpg` không đổi. Số đo: [perf/2026-10-12-wp25-windowing.md](../perf/2026-10-12-wp25-windowing.md).

## 1. Đã làm

| File (`src/PhotoReview.Shell.Win32/Windowing`) | Nội dung |
|---|---|
| `ShellFullscreen.cs` | `IFullscreenNative` (seam: trạng thái cửa sổ, rcMonitor, rect cửa sổ, `SetWindowPos`, `ShowWindow`) + `Win32FullscreenNative`; `ShellFullscreen` bọc `FullscreenController` (WP-08): idempotent (bật hai lần không ghi đè trạng thái trước F11), đường dự phòng phóng to khi không có thông tin monitor, `Refit` ép cửa sổ F11 lên rcMonitor gần nhất, `RestoreState`/`RestoreBounds` cho lúc đóng. |
| `TitleBar.cs` | `DWMWA_USE_IMMERSIVE_DARK_MODE` (20, dự phòng 19) + `DWMWA_CAPTION_COLOR` #171717 như `WindowsDarkTitleBar`/`DarkTitleBarChrome`; seam `IDwmWindowAttributes`; không bao giờ ném. Áp lúc dựng `ShellMainWindow`, tức khi cửa sổ còn bị cloak (không chớp thanh sáng). |
| `ShellMainWindow.Windowing.cs` | Điểm nối: `ViewerState.IsFullscreen` -> `ApplyFullscreen`; handler `WM_DISPLAYCHANGE` (xếp `Refit` SAU khi HĐH dời xong); `OnWindowingDpiChanged` (Refit trước `OnDpiChanged` lõi); `FullscreenRestoreState/Bounds` cho `SavePlacement`; `ShellWindowingSeams` cho test. |

Sửa nhỏ ngoài `Windowing/`: `ShellMainWindow.cs` (một dòng `RegisterWindowing()` ở `RegisterFeatures`, một dòng `DisposeWindowing()`, `SavePlacement` truyền trạng thái/rect F11, tuỳ chọn `Windowing`),
`Startup/ShellPlacement.cs` (`Save(..., fullscreenNormalBounds)`: ghi rect Normal trước F11 thay cho rect monitor, như `WindowPlacementService.Save`),
`Startup/ShellStartup.cs` (`ShellStartupOptions.Windowing` chuyển xuống cho test), `ShellMainWindow.Frame.cs` (bề mặt `Resize` cũng khi DPI đổi, không chỉ khi số pixel đổi).
Phần thẻ đã có sẵn từ WP-20/WP-14 (không làm lại): placement khôi phục trước khi hiện, `WM_DPICHANGED` áp rect gợi ý + `ViewerState.DpiScale`/hộp decode cùng lượt,
tiêu đề = `MainViewModel.FolderTitle` đã gộp `[nhãn instance]`, restore + activate khi nhận forward (`IForwardedOpenTarget`), đóng hoãn khi file action giữ cổng (R7-7).
Thu nhỏ: client 0x0 không vẽ (`Present` bỏ qua), ViewerState không đổi (`UpdateViewport` bỏ qua <= 1).

## 2. Quyết định

| ID | Quyết định | Phương án khác |
|---|---|---|
| WP25-NOTOPMOST | F11 KHÔNG đặt `WS_EX_TOPMOST`: như WPF, cửa sổ phủ đúng rcMonitor ở foreground nên Windows tự ẩn thanh tác vụ (RV-A02 đã đo). | (b) topmost: che cả cửa sổ hệ thống/hộp thoại của app khác. |
| WP25-REFIT | Thêm so với WPF: đang F11, `WM_DISPLAYCHANGE`/`WM_DPICHANGED` ép lại cửa sổ lên rcMonitor của monitor gần nhất (WPF để Windows dời, cửa sổ có thể lệch/không phủ hết). Không F11: không làm gì. | (b) giữ y WPF. |
| WP25-SURFACE-DPI | `D2DRenderSurface.Resize` chạy khi DPI cửa sổ khác DPI bề mặt, không chỉ khi số pixel đổi (rect gợi ý cùng số pixel nhưng DPI khác). | (b) chỉ khi cỡ đổi: nét vẽ sai tỉ lệ tới lần resize kế. |

Các dòng CHƯA XÁC NHẬN tương ứng ở [NOWPF-UNCONFIRMED-DECISIONS-20261010](NOWPF-UNCONFIRMED-DECISIONS-20261010.md).

## 3. Kiểm chứng

- `tests/PhotoReview.Shell.Integration.Tests/Windowing/ShellWindowingTests.cs` (shell THẬT, HWND thật, WARP, desktop ẩn, không phụ thuộc cỡ/số màn hình: tham chiếu lấy từ monitor thật
  của cửa sổ lúc chạy; "màn hình thứ hai", "monitor bị rút" là seam `IFullscreenNative`/`IMonitorLayout`): F11 Normal phủ cả monitor, không đổi trạng thái, không viền,
  thoát về đúng rect + style; F11 trên Maximized giữ Maximized; hai lần bật/thoát khi không F11 là no-op; phím F11/Esc qua thông điệp thật; một bước resize (không cỡ trung gian);
  đóng khi F11 lưu trạng thái + rect Normal trước F11 (Normal và Maximized); đóng hoãn giữa lúc F11; round-trip lược đồ `window-placement.json` với store dùng chung của bản WPF;
  monitor rút lúc F11 -> ép lại; thoát F11 sau khi monitor gốc bị rút -> work area còn lại giữ cỡ; DPI đổi lúc F11; DPI đổi cùng số pixel -> bề mặt/hộp decode cập nhật;
  thanh tiêu đề tối; tiêu đề + nhãn instance; thu nhỏ/khôi phục không đổi rect và ViewerState; F11 lúc đang thu nhỏ.
- `tests/PhotoReview.Shell.Tests`: `TitleBarTests` (6), `ShellFullscreenTests` (3), `ShellPlacementTests` (+3: rect Normal trước F11).
- Mutation thủ công 23 đột biến (placement clamp, layout-change fallback, băng caption hiển thị, work area gần nhất, restore F11, idempotence, DPI refit/surface/decode box,
  monitor refit, đóng hoãn, thanh tiêu đề: màu + thuộc tính dự phòng): 22 bị test bắt; M08 (bỏ guard `IsActive` ở `OnDisplayChanged`) tương đương vì `Refit` đã tự trả false khi không F11.

## 4. Chưa kiểm (cần máy hai màn hình thật)

- Kéo cửa sổ giữa hai monitor DPI khác nhau (WM_DPICHANGED thật với rect gợi ý của HĐH) và F11 trên monitor thứ hai.
- Rút cáp/đổi chế độ hiển thị lúc đang F11 (WM_DISPLAYCHANGE thật, thứ tự HĐH dời cửa sổ so với `Refit` xếp sau).
- Có khung trung gian sai hay không (`tools/diag/fullscreen-capture.ps1` trên shell): test chỉ khẳng định MỘT lần đổi cỡ client, không chụp khung.
