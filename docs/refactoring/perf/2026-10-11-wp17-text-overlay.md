# WP-17 chữ DirectWrite + overlay: dựng layout, cache, khung overlay (2026-10-11)

Quyết định: [`NOWPF-WP17`](../decisions/NOWPF-WP17-TEXT-OVERLAY.md). Nhánh `feat/nowpf-wp17-text-overlay`.

## Cách đo

- `tests/PhotoReview.Shell.Tests/Overlay/TextOverlayPerfTests.cs` (`Category=Manual`, không assert thời gian), Release:
  `dotnet test tests/PhotoReview.Shell.Tests -c Release --filter "FullyQualifiedName~TextOverlayPerfTests" --logger "console;verbosity=detailed"`.
- Cảnh = 5 panel của G-OVL (Status 2 dòng, FolderInfo, Zoom, CapturePair, Compare) ở 1920x1080 px, DPI 1,5, Segoe UI 12,
  DirectWrite thật. "submit" = `Arrange` + `BeginDraw..EndDrawAndPresent` (CPU); "done" = thêm chờ GPU xong (đọc 1 pixel
  qua texture staging, `WaitForGpu`). Bỏ 20 khung đầu.
- Máy: i7-9750H, GPU thật qua D3D11 hardware và WARP. Tải CPU lúc đo ~23 % (một agent khác build nền); số "done" vẫn có nhiễu.

## Kết quả (ms)

| Phép đo | n | P50 | P95 | max |
|---|---:|---:|---:|---:|
| `CreateLayout` cache MISS (dựng + ellipsis, chuỗi tiếng Việt ~45 ký tự) | 400 | 0,103 | 0,288 | 43,5 (lần JIT) |
| `CreateLayout` cache HIT | 2000 | < 0,001 | 0,001 | 0,053 |
| `Arrange` cả cảnh, layout đã cache | 140 | 0,005 | 0,018 | 0,054 |
| **GPU:** Arrange + Render submit (CPU) | 140 | **0,47** | **1,37** | 9,7 |
| GPU: Arrange + Render done (GPU xong) | 140 | 4,43 | 18,8 | 28,6 |
| GPU: như trên, chữ zoom đổi mỗi khung (1 layout miss/khung), done | 140 | 3,86 | 23,0 | 34,2 |
| WARP: Arrange + Render submit | 140 | 0,52 | 3,44 | 13,1 |
| WARP: done | 140 | 4,28 | 37,2 | 58,3 |

## Nhận xét

- **CPU của overlay không đáng kể:** cảnh 5 panel tốn 0,5 ms P50 / 1,4 ms P95 để gửi lệnh (ngân sách khung 16,7 ms ở 60 Hz); `Arrange` với
  layout cache gần như 0. Đổi chữ mỗi khung (đồng hồ, zoom %) chỉ thêm ~0,1 ms/layout.
- Cột "done" (4-5 ms P50, P95 19-23 ms) chủ yếu là chi phí đồng bộ Map/Copy của chính phép đo (WP-15: clear-only done P50 1,0 ms,
  P95 3,1 ms khi máy ít tải) cộng nhiễu tải nền; không phải thời gian vẽ overlay. Cần đo lại khi máy rảnh nếu muốn con số chính xác.
- Layout cache MISS lần đầu (JIT + nạp font) ~44 ms một lần - đã gánh ở khung đầu; nên dựng sẵn layout của overlay trước khung
  hiện đầu (WP-20) nếu chạm ngân sách khởi động.
