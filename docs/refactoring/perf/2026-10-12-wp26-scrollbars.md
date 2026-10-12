# WP-26 - chi phí thanh cuộn tự vẽ (2026-10-12)

Hai nguồn đo, cả hai trên máy dev (Release, WARP cho phần vẽ thật):

1. `ScrollBarPerfReportTests` (Shell.Tests, `Category=Manual`, 20.000 lần mỗi mục, đã làm nóng), cửa sổ giả + viewport thật:

   | Việc | Mỗi lần |
   |---|---|
   | `ShellScrollBars.HitTest` (tính hình học từ layout + offset rồi hit-test) | 2,2 us |
   | `ShellScrollBars.Draw` vào bộ ghi (3 `FillRectangle` + 2 `FillRoundedRectangle`) | 2,7 us |
   | `WM_MOUSEMOVE` khi kéo con trượt (hình học + `ViewportController.ScrollTo` + `UpdateLayout` + Invalidate) | 3,6 us |
   | Vẽ D2D WARP offscreen 1200x800 (gồm đọc lại pixel của test) | 6,5 ms |

2. `ShellScrollBarEndToEndTests.ThumbDragLatency_IsMeasuredAtTheHandler_...` (`ShellMainWindow` THẬT, `SendMessage(WM_MOUSEMOVE)` khi đang
   kéo, 200 mẫu, ảnh 4000x3000 ở 100 %): handler xong và offset đã áp **p50 0,044 ms, p95 0,063 ms, max 2,4 ms** (lần đầu: JIT).
   Test chặn p95 < 50 ms; số liệu ghi ở `%TEMP%\pr-wp26-drag-latency.txt`.

Kết luận: thanh cuộn tốn dưới 5 us mỗi sự kiện chuột và mỗi khung; độ trễ kéo-thả do khung trình bày (WP-21/Present), không do thanh
cuộn. Hàm vẽ thoát ngay khi không có thanh nào (Fit) và không cấp phát (hai thanh duyệt tuần tự, không mảng tạm).
