# WP-25 - độ trễ F11 trên cửa sổ shell (2026-10-12)

Đo bằng `ShellWindowingTests.Manual_F11Latency_IsReportedOnTheUiThread` (Category=Manual): cửa sổ Normal 640x480, 30 vòng
`ApplyFullscreen(true)` + `RenderNow()` rồi `ApplyFullscreen(false)` + `RenderNow()`, thời gian trên UI thread. Máy dev, WARP (`PreferWarp`), desktop ẩn
(`tools/run-tests-hidden.ps1`), nền bận (nhiều agent build song song), chưa R2R.

| | p50 | p95 | max |
|---|---|---|---|
| vào F11 (SetWindowPos + khung đầu) | 3,4 ms | 6,9 ms | 10,1 ms |
| thoát F11 | 4,6 ms | 17,0 ms | 19,9 ms |

Không thêm việc vào đường nóng: `Present` chỉ thêm một so sánh `double` (DpiScale) mỗi khung; `Refit` chỉ chạy khi đang F11 và khi WM_DISPLAYCHANGE/DPI.
Cờ `Present(1)` của F11 (WP-21) chưa đo trên GPU thật ở gói này.
